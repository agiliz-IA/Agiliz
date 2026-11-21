using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.Billing;
using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Agiliz.Runtime.Endpoints;
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

/// <summary>Rotas /api/admin do Runtime: reload de bots e billing no banco (em memória nos testes).</summary>
public sealed class RuntimeAdminTests : IDisposable
{
    private const string Token = "token-teste-admin";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();
    private readonly FakeMessageProvider _provider = new();
    private readonly FakeLlmClient _llm = new();
    private readonly string _dbName = $"agiliz-tests-{Guid.NewGuid():N}";

    public RuntimeAdminTests()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-groq-key");
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

                // Postgres → banco em memória; o BillingWriter real continua gravando.
                services.RemoveAll<DbContextOptions<AgilizDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AgilizDbContext>>();
                services.AddDbContext<AgilizDbContext>(o => o.UseInMemoryDatabase(_dbName));
            });
        });

    private static HttpClient Client(WebApplicationFactory<Runtime.Program> factory, string? token = Token)
    {
        var client = factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Add(AdminTokenFilter.HeaderName, token);
        return client;
    }

    private static StringContent Message(string instance, string text) => new(JsonSerializer.Serialize(new
    {
        @event = "messages.upsert",
        instance,
        data = new
        {
            key = new { remoteJid = "5521988887777@s.whatsapp.net", fromMe = false, id = Guid.NewGuid().ToString() },
            message = new { conversation = text }
        }
    }), Encoding.UTF8, "application/json");

    /// <summary>O BillingWriter grava em segundo plano; espera o relatório chegar ao estado esperado.</summary>
    private static async Task<BillingReport> WaitForBillingAsync(HttpClient client, Func<BillingReport, bool> until, string query = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var report = (await client.GetFromJsonAsync<BillingReport>("/api/admin/billing" + query, JsonOpts))!;
            if (until(report) || DateTime.UtcNow > deadline) return report;
            await Task.Delay(50);
        }
    }

    // ─── Token ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("token-errado")]
    public async Task AdminRoutes_WithoutValidToken_Return401(string? token)
    {
        using var factory = BuildFactory();
        var client = Client(factory, token);

        (await client.PostAsync("/api/admin/reload", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/admin/billing")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/handoff/paused")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── Reload ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reload_ActivatesBotSavedAfterStartup()
    {
        using var factory = BuildFactory();
        var client = Client(factory);

        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "novo", whatsappNumber: "5521900000001")
            .WithFlows(("oi", "Olá do bot novo!")));

        await client.PostAsync("/webhook", Message("5521900000001", "oi"));
        _provider.Sent.Should().BeEmpty("o Runtime ainda não releu os configs");

        var reload = await client.PostAsync("/api/admin/reload", null);
        reload.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("loaded")
            .EnumerateArray().Select(e => e.GetString()).Should().Equal("novo");

        await client.PostAsync("/webhook", Message("5521900000001", "oi"));
        _provider.Sent.Should().ContainSingle(m => m.Body == "Olá do bot novo!");
    }

    // ─── Billing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Billing_ConversationCost_IsPersistedPerTenant()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "5521900000002"));
        _llm.Returns("Claro, qual horário?");
        using var factory = BuildFactory();
        var client = Client(factory);

        await client.PostAsync("/webhook", Message("5521900000002", "quero marcar"));
        var report = await WaitForBillingAsync(client, r => r.EntryCount >= 1);

        var tenant = report.Tenants.Should().ContainSingle().Subject;
        tenant.TenantId.Should().Be("clinica");
        tenant.LlmUsd.Should().Be(ModelCatalog.EstimateCostUsd("openai/gpt-oss-120b", new TokenUsage(10, 20)));
        report.Entries.Should().ContainSingle(e => e.Type == CostType.TokensLLM);
    }

    [Fact]
    public async Task Billing_PostFromPanel_IsAcceptedAndReported()
    {
        using var factory = BuildFactory();
        var client = Client(factory);

        var post = await client.PostAsJsonAsync("/api/admin/billing", new
        {
            tenantId = "painel-admin",
            type = "TokensLLM",
            description = "Wizard MetaAgent (1200 tok)",
            amountUsd = 0.002m
        });
        post.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var report = await WaitForBillingAsync(client, r => r.EntryCount >= 1);
        report.TotalUsd.Should().Be(0.002m);
        report.Tenants.Should().ContainSingle(t => t.TenantId == "painel-admin" && t.LlmUsd == 0.002m);

        var raw = await client.GetStringAsync("/api/admin/billing");
        raw.Should().Contain("\"TokensLLM\"", "o painel lê o tipo como texto");
    }

    [Theory]
    [InlineData("../x", "ok", 0.1)]
    [InlineData("painel-admin", "", 0.1)]
    [InlineData("painel-admin", "ok", -1)]
    public async Task Billing_PostWithInvalidData_Returns400(string tenantId, string description, decimal amount)
    {
        using var factory = BuildFactory();

        var post = await Client(factory).PostAsJsonAsync("/api/admin/billing",
            new { tenantId, type = "TokensLLM", description, amountUsd = amount });

        post.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Billing_FromFilter_LimitsTotalsAndEntries()
    {
        using var factory = BuildFactory();
        var client = Client(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();
            db.CostRecords.AddRange(
                new CostRecord { Id = Guid.NewGuid(), TenantId = "a", Type = CostType.TokensLLM, Description = "antigo", AmountUsd = 1m, Timestamp = DateTimeOffset.UtcNow.AddDays(-60) },
                new CostRecord { Id = Guid.NewGuid(), TenantId = "a", Type = CostType.ToolExecution, Description = "recente", AmountUsd = 0.5m, Timestamp = DateTimeOffset.UtcNow.AddHours(-1) });
            await db.SaveChangesAsync();
        }

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        var report = (await client.GetFromJsonAsync<BillingReport>($"/api/admin/billing?from={from}", JsonOpts))!;

        report.TotalUsd.Should().Be(0.5m);
        report.Tenants.Should().ContainSingle(t => t.ToolsUsd == 0.5m && t.LlmUsd == 0m);
        report.Entries.Should().ContainSingle(e => e.Description == "recente");
    }

    // ─── Custo por integração (I6) ────────────────────────────────────────────

    [Fact]
    public async Task Billing_GroupsCostsByIntegration()
    {
        using var factory = BuildFactory();
        var client = Client(factory);

        await client.PostAsJsonAsync("/api/admin/billing", new { tenantId = "clinica", type = "ToolExecution", description = "Transcrição", amountUsd = 0.01m, integrationId = "audio" });
        await client.PostAsJsonAsync("/api/admin/billing", new { tenantId = "clinica", type = "TokensLLM", description = "LLM", amountUsd = 0.03m, integrationId = "ia" });
        await client.PostAsJsonAsync("/api/admin/billing", new { tenantId = "outra", type = "TokensLLM", description = "LLM", amountUsd = 0.02m, integrationId = "ia" });

        var report = await WaitForBillingAsync(client, r => r.EntryCount >= 3);

        report.Integrations.Should().ContainSingle(i => i.IntegrationId == "ia" && i.TotalUsd == 0.05m && i.TenantCount == 2);
        report.Tenants.Single(t => t.TenantId == "clinica").ByIntegration
            .Should().ContainSingle(i => i.IntegrationId == "audio" && i.TotalUsd == 0.01m);
        report.Entries.Should().Contain(e => e.IntegrationId == "audio");
    }

    [Fact]
    public async Task Billing_PostWithUnknownIntegration_Returns400()
    {
        using var factory = BuildFactory();
        var post = await Client(factory).PostAsJsonAsync("/api/admin/billing",
            new { tenantId = "clinica", type = "TokensLLM", description = "x", amountUsd = 0.01m, integrationId = "inventada" });
        post.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Franquia: contatos por mês (P2) ──────────────────────────────────────

    private static StringContent MessageFrom(string instance, string phone, string text) => new(JsonSerializer.Serialize(new
    {
        @event = "messages.upsert",
        instance,
        data = new
        {
            key = new { remoteJid = $"{phone}@s.whatsapp.net", fromMe = false, id = Guid.NewGuid().ToString() },
            message = new { conversation = text }
        }
    }), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Usage_CountsDistinctContactsPerMonth()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "5521900000006")
            .WithFlows(("oi", "Olá!")));
        using var factory = BuildFactory();
        var client = Client(factory);

        await client.PostAsync("/webhook", MessageFrom("5521900000006", "5521911110001", "oi"));
        await client.PostAsync("/webhook", MessageFrom("5521900000006", "5521911110001", "oi de novo"));
        await client.PostAsync("/webhook", MessageFrom("5521900000006", "5521911110002", "oi"));

        var month = Agiliz.Core.Plans.UsageMonth.Current();
        JsonElement tenant = default;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var body = await client.GetFromJsonAsync<JsonElement>($"/api/admin/usage?month={month}");
            var tenants = body.GetProperty("tenants").EnumerateArray().ToList();
            if (tenants.Count == 1 && tenants[0].GetProperty("messages").GetInt32() == 3) { tenant = tenants[0]; break; }
            await Task.Delay(50);
        }

        tenant.ValueKind.Should().Be(JsonValueKind.Object, "o UsageRecorder deveria ter gravado as 3 mensagens");
        tenant.GetProperty("tenantId").GetString().Should().Be("clinica");
        tenant.GetProperty("contacts").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Usage_InvalidMonth_Returns400()
    {
        using var factory = BuildFactory();
        (await Client(factory).GetAsync("/api/admin/usage?month=nov")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Testar conexão (I5) ──────────────────────────────────────────────────

    [Fact]
    public async Task TestIntegration_WhatsApp_ChecksBotInstance()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "5521900000003"));
        _provider.ConnectionResult = new Agiliz.Core.Integrations.IntegrationTestResult(false, "WhatsApp desconectado: escaneie o QR code.");
        using var factory = BuildFactory();

        var response = await Client(factory).PostAsJsonAsync("/api/admin/integrations/whatsapp/test", new { tenantId = "clinica" });

        var result = (await response.Content.ReadFromJsonAsync<Agiliz.Core.Integrations.IntegrationTestResult>(JsonOpts))!;
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("QR code");
        _provider.ConnectionChecks.Should().Equal("5521900000003");
    }

    [Fact]
    public async Task TestIntegration_GoogleWithoutCredentials_ExplainsInPortuguese()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "5521900000004") with { Type = BotType.Scheduling });
        using var factory = BuildFactory();

        var response = await Client(factory).PostAsJsonAsync("/api/admin/integrations/google-agenda/test",
            new { tenantId = "clinica", fields = new Dictionary<string, string> { ["calendarId"] = "agenda@gmail.com" } });

        var result = (await response.Content.ReadFromJsonAsync<Agiliz.Core.Integrations.IntegrationTestResult>(JsonOpts))!;
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("Credenciais do Google");
    }

    [Fact]
    public async Task TestIntegration_GoogleWithoutCalendarId_AsksForIt()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "5521900000005") with { Type = BotType.Scheduling });
        using var factory = BuildFactory();

        var response = await Client(factory).PostAsJsonAsync("/api/admin/integrations/google-agenda/test", new { tenantId = "clinica" });

        (await response.Content.ReadFromJsonAsync<Agiliz.Core.Integrations.IntegrationTestResult>(JsonOpts))!
            .Message.Should().Contain("ID da agenda");
    }

    [Fact]
    public async Task TestIntegration_UnknownBot_Returns404()
    {
        using var factory = BuildFactory();
        var response = await Client(factory).PostAsJsonAsync("/api/admin/integrations/whatsapp/test", new { tenantId = "nao-existe" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}
