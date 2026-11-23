using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Agiliz.Runtime.Endpoints;
using Agiliz.Runtime.Services;
using Agiliz.Tests.Fakes;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Agiliz.Tests.Integration;

/// <summary>Rotas /api/admin/tenants/{id} que o portal do cliente usa (via painel).</summary>
public sealed class RuntimeTenantTests : IDisposable
{
    private const string Token = "token-teste-admin";
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();
    private readonly FakeMessageProvider _provider = new();
    private readonly FakeLlmClient _llm = new();
    private readonly string _dbName = $"agiliz-tenant-{Guid.NewGuid():N}";

    public RuntimeTenantTests()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-groq-key");
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "5521900000010") with { Type = BotType.Scheduling });
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "outra", whatsappNumber: "5521900000011"));
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "demo", whatsappNumber: "5521900000012") with { IsDemo = true });
    }

    private WebApplicationFactory<Runtime.Program> BuildFactory() =>
        new WebApplicationFactory<Runtime.Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConfigsDir", _dir.Path);
            b.UseSetting("Runtime:AdminToken", Token);
            b.ConfigureServices(services =>
            {
                services.AddSingleton<IMessageProvider>(_provider);
                services.AddSingleton<Func<BotConfig, ILlmClient>>(_ => _ => _llm);
                services.AddSingleton<Agiliz.Core.Billing.ICostRecorder>(new FakeCostRecorder());
                services.RemoveAll<DbContextOptions<AgilizDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AgilizDbContext>>();
                services.AddDbContext<AgilizDbContext>(o => o.UseInMemoryDatabase(_dbName));
            });
        });

    private static HttpClient Client(WebApplicationFactory<Runtime.Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminTokenFilter.HeaderName, Token);
        return client;
    }

    private static StringContent Message(string instance, string phone, string text) => new(JsonSerializer.Serialize(new
    {
        @event = "messages.upsert",
        instance,
        data = new
        {
            key = new { remoteJid = $"{phone}@s.whatsapp.net", fromMe = false, id = Guid.NewGuid().ToString() },
            message = new { conversation = text }
        }
    }), Encoding.UTF8, "application/json");

    private static async Task SeedAsync(WebApplicationFactory<Runtime.Program> factory, Action<AgilizDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TenantRoutes_RequireAdminToken()
    {
        using var factory = BuildFactory();
        (await factory.CreateClient().GetAsync("/api/admin/tenants/clinica/conversations")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Conversations_AreScopedToTenant_WithContactNames()
    {
        using var factory = BuildFactory();
        await SeedAsync(factory, db =>
        {
            db.ConversationStates.Add(new ConversationState { TenantId = "clinica", Phone = "5521911110001", LastMessagePreview = "quero marcar", LastActivityAt = DateTimeOffset.UtcNow });
            db.ConversationStates.Add(new ConversationState { TenantId = "outra", Phone = "5521911110002", LastMessagePreview = "oi", LastActivityAt = DateTimeOffset.UtcNow });
            db.Users.Add(new SchedulingUser { TenantId = "clinica", Phone = "5521911110001", Name = "Ana" });
        });

        var body = await Client(factory).GetFromJsonAsync<JsonElement[]>("/api/admin/tenants/clinica/conversations");

        body.Should().ContainSingle();
        body![0].GetProperty("phone").GetString().Should().Be("5521911110001");
        body[0].GetProperty("name").GetString().Should().Be("Ana");
        (await Client(factory).GetAsync("/api/admin/tenants/nao-existe/conversations")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HumanReply_SendsPausesAndKeepsBotQuiet()
    {
        _llm.Returns("Olá! Quer marcar?");
        using var factory = BuildFactory();
        var client = Client(factory);
        await client.PostAsync("/webhook", Message("5521900000010", "5521911110003", "bom dia"));

        var reply = await client.PostAsJsonAsync("/api/admin/tenants/clinica/conversations/5521911110003/reply", new { body = "Oi, aqui é a Ana da recepção." });
        reply.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _provider.Sent.Should().Contain(m => m.To == "5521911110003" && m.Body == "Oi, aqui é a Ana da recepção.");

        var history = await client.GetFromJsonAsync<JsonElement>("/api/admin/tenants/clinica/conversations/5521911110003/messages");
        history.GetProperty("isPaused").GetBoolean().Should().BeTrue();
        history.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString())
            .Should().Equal("bom dia", "Olá! Quer marcar?", "Oi, aqui é a Ana da recepção.");

        var sentBefore = _provider.Sent.Count;
        await client.PostAsync("/webhook", Message("5521900000010", "5521911110003", "obrigada"));
        _provider.Sent.Count.Should().Be(sentBefore, "a conversa está com o atendente");

        (await client.PostAsync("/api/admin/tenants/clinica/conversations/5521911110003/resume", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonElement>("/api/admin/tenants/clinica/conversations/5521911110003/messages"))
            .GetProperty("isPaused").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Appointments_FilterByTenantAndDay()
    {
        var day = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(-3));
        using var factory = BuildFactory();
        await SeedAsync(factory, db =>
        {
            db.Appointments.Add(new Appointment { TenantId = "clinica", Phone = "5521911110004", ScheduledTime = day.AddHours(10), DurationMinutes = 30 });
            db.Appointments.Add(new Appointment { TenantId = "clinica", Phone = "5521911110005", ScheduledTime = day.AddDays(1).AddHours(10) });
            db.Appointments.Add(new Appointment { TenantId = "outra", Phone = "5521911110006", ScheduledTime = day.AddHours(11) });
            db.Users.Add(new SchedulingUser { TenantId = "clinica", Phone = "5521911110004", Name = "Bruna" });
        });

        var from = Uri.EscapeDataString(day.ToString("O"));
        var to = Uri.EscapeDataString(day.AddDays(1).ToString("O"));
        var body = await Client(factory).GetFromJsonAsync<JsonElement[]>($"/api/admin/tenants/clinica/appointments?from={from}&to={to}");

        body.Should().ContainSingle();
        body![0].GetProperty("name").GetString().Should().Be("Bruna");
        body[0].GetProperty("status").GetString().Should().Be("Pending");
    }

    [Fact]
    public async Task PortalCode_IsSentFromBotNumber()
    {
        using var factory = BuildFactory();
        var client = Client(factory);

        (await client.PostAsJsonAsync("/api/admin/tenants/clinica/portal-code", new { phone = "5521911110007", code = "123456" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        _provider.Sent.Should().ContainSingle(m => m.From == "5521900000010" && m.To == "5521911110007" && m.Body.Contains("123456"));

        (await client.PostAsJsonAsync("/api/admin/tenants/clinica/portal-code", new { phone = "5521911110007", code = "12ab" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Pairing_ReturnsCodeFromProvider()
    {
        using var factory = BuildFactory();

        var body = await (await Client(factory).PostAsync("/api/admin/tenants/clinica/whatsapp/pairing", null)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("pairingCode").GetString().Should().Be("ABCD1234");
    }

    [Fact]
    public async Task DemoReset_ClearsOnlyDemoTenant()
    {
        using var factory = BuildFactory();
        await SeedAsync(factory, db =>
        {
            foreach (var tenant in new[] { "demo", "clinica" })
            {
                db.ConversationStates.Add(new ConversationState { TenantId = tenant, Phone = "5521911110008" });
                db.Users.Add(new SchedulingUser { TenantId = tenant, Phone = "5521911110008", Name = "Lead" });
                db.Appointments.Add(new Appointment { TenantId = tenant, Phone = "5521911110008", ScheduledTime = DateTimeOffset.UtcNow.AddDays(1) });
                db.ContactActivities.Add(new ContactActivity { TenantId = tenant, Month = "2026-11", Phone = "5521911110008", MessageCount = 3 });
            }
        });
        var client = Client(factory);

        var reset = await client.PostAsync("/api/admin/tenants/demo/demo/reset", null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/admin/tenants/clinica/demo/reset", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();
        db.ConversationStates.Count(c => c.TenantId == "demo").Should().Be(0);
        db.Users.Count(u => u.TenantId == "demo").Should().Be(0);
        db.Appointments.Count(a => a.TenantId == "demo").Should().Be(0);
        db.ContactActivities.Count(c => c.TenantId == "demo").Should().Be(0);
        db.ConversationStates.Count(c => c.TenantId == "clinica").Should().Be(1, "outro cliente não é tocado");
        db.Appointments.Count(a => a.TenantId == "clinica").Should().Be(1);
    }

    [Theory]
    [InlineData("2026-11-23T05:00:00Z", 1.0)]   // 02:00 em Brasília → 03:00
    [InlineData("2026-11-23T06:30:00Z", 23.5)]  // 03:30 → 03:00 do dia seguinte
    public void DemoReset_RunsAt3amBrasilia(string utc, double hours) =>
        DemoResetService.DelayUntilNextReset(DateTimeOffset.Parse(utc)).TotalHours.Should().BeApproximately(hours, 0.01);

    public void Dispose()
    {
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}
