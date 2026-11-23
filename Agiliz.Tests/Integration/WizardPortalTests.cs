using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agiliz.Core.Config;
using Agiliz.Core.Integrations;
using Agiliz.Core.Models;
using Agiliz.Tests.Helpers;
using Agiliz.Wizard.Portal;
using Agiliz.Wizard.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Agiliz.Tests.Integration;

/// <summary>Portal do cliente: separado do admin, login por código, escopo por bot, CSRF e demonstração.</summary>
public sealed class WizardPortalTests : IDisposable
{
    private const string Phone = "5521911112222";
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();
    private readonly WireMockServer _runtime = WireMockServer.Start();

    public WizardPortalTests()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-groq-key");
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "5521900000020") with
        {
            Type = BotType.Scheduling,
            DisplayName = "Clínica Sorriso",
            PortalPhones = [Phone],
            PlanId = "essencial" // inclui agenda e transbordo; áudio é adicional
        });
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "outra", whatsappNumber: "5521900000021") with { PortalPhones = ["5521933334444"] });
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "demo", whatsappNumber: "5521900000022") with { IsDemo = true, PortalPhones = [Phone] });

        _runtime.Given(Request.Create().WithPath("/api/admin/reload").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { }));
        _runtime.Given(Request.Create().WithPath("/api/admin/tenants/*/portal-code").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        _runtime.Given(Request.Create().WithPath("/api/admin/usage").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { month = "2026-11", tenants = new[] { new { tenantId = "clinica", contacts = 130, messages = 400 } } }));
        foreach (var tenant in new[] { "clinica", "demo" })
        {
            _runtime.Given(Request.Create().WithPath($"/api/admin/tenants/{tenant}/conversations").UsingGet())
                    .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
                    {
                        new { phone = "5521988887777", name = (string?)null, isPaused = true, pausedAt = DateTimeOffset.UtcNow, lastActivityAt = DateTimeOffset.UtcNow, lastMessagePreview = "quero falar com alguém" }
                    }));
        }
        _runtime.Given(Request.Create().WithPath("/api/admin/tenants/clinica/appointments").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));
        _runtime.Given(Request.Create().WithPath("/api/admin/tenants/clinica/conversations/5521988887777/messages").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { isPaused = true, messages = new[] { new { role = "cliente", text = "<b>oi</b>" } } }));
        _runtime.Given(Request.Create().WithPath("/api/admin/tenants/clinica/conversations/5521988887777/reply").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        _runtime.Given(Request.Create().WithPath("/api/admin/tenants/demo/demo/reset").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { }));
    }

    private WebApplicationFactory<Agiliz.Wizard.Program> BuildFactory(int loginLimit = 100) =>
        new WebApplicationFactory<Agiliz.Wizard.Program>().WithWebHostBuilder(b => b
            .UseSetting("ConfigsDir", _dir.Path)
            .UseSetting("Runtime:ApiUrl", _runtime.Url)
            .UseSetting("Runtime:AdminToken", "token")
            .UseSetting("Wizard:AdminPassword", "senha-admin")
            .UseSetting("Portal:LoginRateLimitPerMinute", loginLimit.ToString()));

    private static HttpRequestMessage Post(string path, object? body = null, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (csrf) request.Headers.Add(PortalEndpoints.CsrfHeader, "1");
        return request;
    }

    private string LastCode() =>
        JsonDocument.Parse(_runtime.LogEntries.Last(e => e.RequestMessage.Path.EndsWith("/portal-code")).RequestMessage.Body!)
            .RootElement.GetProperty("code").GetString()!;

    /// <summary>Faz o login completo; o cookie fica no HttpClient.</summary>
    private async Task<HttpClient> LoggedInAsync(WebApplicationFactory<Agiliz.Wizard.Program> factory)
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/portal/api/login/start", new { phone = "(21) 91111-2222" })).EnsureSuccessStatusCode();
        var verify = await client.PostAsJsonAsync("/portal/api/login/verify", new { phone = "21911112222", code = LastCode() });
        verify.StatusCode.Should().Be(HttpStatusCode.NoContent);
        verify.Headers.GetValues("Set-Cookie").Single().Should().Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/portal");
        return client;
    }

    // ─── Separação do admin ───────────────────────────────────────────────────

    [Fact]
    public async Task Portal_IsOutsideAdminBasicAuth_AndAdminStaysProtected()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var page = await client.GetAsync("/portal/");
        page.StatusCode.Should().Be(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
        page.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'self'");
        page.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");

        (await client.GetAsync("/portal/portal.js")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/bots")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "o admin continua no Basic Auth");

        var me = await client.GetAsync("/portal/api/me");
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        me.Headers.WwwAuthenticate.Should().BeEmpty("o portal não pede a senha do admin");
    }

    // ─── Login ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithCode_ListsOnlyAllowedBots()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        var me = await client.GetFromJsonAsync<JsonElement>("/portal/api/me");

        me.GetProperty("bots").EnumerateArray().Select(b => b.GetProperty("tenantId").GetString()).Should().BeEquivalentTo("clinica", "demo");
        _runtime.LogEntries.Single(e => e.RequestMessage.Path.EndsWith("/portal-code")).RequestMessage.Path
            .Should().Be("/api/admin/tenants/clinica/portal-code", "o código sai do número de um bot do próprio cliente");
    }

    [Fact]
    public async Task Login_UnknownPhone_GetsSameAnswerAndNoCode()
    {
        using var factory = BuildFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/portal/api/login/start", new { phone = "21900001111" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _runtime.LogEntries.Should().NotContain(e => e.RequestMessage.Path.EndsWith("/portal-code"));
    }

    [Fact]
    public async Task Login_AfterFiveWrongCodes_RightCodeNoLongerWorks()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/portal/api/login/start", new { phone = Phone });
        var code = LastCode();
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < PortalAuthService.MaxAttempts; i++)
            (await client.PostAsJsonAsync("/portal/api/login/verify", new { phone = Phone, code = wrong })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync("/portal/api/login/verify", new { phone = Phone, code })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_IsRateLimitedPerClient()
    {
        using var factory = BuildFactory(loginLimit: 2);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/portal/api/login/start", new { phone = "21900001111" });
        await client.PostAsJsonAsync("/portal/api/login/start", new { phone = "21900001112" });
        var third = await client.PostAsJsonAsync("/portal/api/login/start", new { phone = "21900001113" });

        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RemovingPhoneFromBot_RevokesAccessImmediately()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);
        var config = BotConfigLoader.Load(_dir.Path, "clinica");
        BotConfigLoader.Save(_dir.Path, config with { PortalPhones = [] });

        (await client.GetAsync("/portal/api/bots/clinica/overview")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Escopo e CSRF ────────────────────────────────────────────────────────

    [Fact]
    public async Task OtherClientsBot_Returns404()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        (await client.GetAsync("/portal/api/bots/outra/overview")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/portal/api/bots/outra/conversations")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChangingRequests_NeedPortalHeader()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        var withoutHeader = await client.SendAsync(Post("/portal/api/bots/clinica/pause", new { paused = true }, csrf: false));

        withoutHeader.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        BotConfigLoader.Load(_dir.Path, "clinica").Paused.Should().BeFalse();
    }

    // ─── Telas ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Overview_ShowsUsageWaitingAndPlan()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        var body = await client.GetFromJsonAsync<JsonElement>("/portal/api/bots/clinica/overview");

        body.GetProperty("name").GetString().Should().Be("Clínica Sorriso");
        body.GetProperty("waiting").GetInt32().Should().Be(1);
        body.GetProperty("usage").GetProperty("contacts").GetInt32().Should().Be(130);
        body.GetProperty("usage").GetProperty("allowance").GetInt32().Should().Be(150);
        body.GetProperty("plan").GetProperty("name").GetString().Should().Be("Essencial");
    }

    [Fact]
    public async Task Conversations_UseOpaqueIds_AndReplyGoesToRightContact()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/portal/api/bots/clinica/conversations");
        var item = list!.Single();
        var id = item.GetProperty("id").GetString()!;
        id.Should().NotContain("5521988887777", "o telefone não vai na URL");
        item.GetProperty("phone").GetString().Should().Be("+55 (21) 98888-7777");

        var detail = await client.GetFromJsonAsync<JsonElement>($"/portal/api/bots/clinica/conversations/{Uri.EscapeDataString(id)}");
        detail.GetProperty("messages")[0].GetProperty("text").GetString().Should().Be("<b>oi</b>", "o texto vai cru; a página escapa");

        var reply = await client.SendAsync(Post($"/portal/api/bots/clinica/conversations/{Uri.EscapeDataString(id)}/reply", new { body = "Já te ligo!" }));
        reply.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _runtime.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/api/admin/tenants/clinica/conversations/5521988887777/reply" && e.RequestMessage.Body!.Contains("te ligo!"));

        (await client.GetAsync($"/portal/api/bots/demo/conversations/{Uri.EscapeDataString(id)}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "id de conversa de outro bot não vale");
    }

    [Fact]
    public async Task DemoBot_MasksPhones()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/portal/api/bots/demo/conversations");

        list!.Single().GetProperty("phone").GetString().Should().Be("+55 (21) •••••-•777");
        list!.Single().GetProperty("contact").GetString().Should().NotContain("98888");
    }

    [Fact]
    public async Task PauseBot_SavesConfigAndReloadsRuntime()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        (await client.SendAsync(Post("/portal/api/bots/clinica/pause", new { paused = true }))).StatusCode.Should().Be(HttpStatusCode.OK);

        BotConfigLoader.Load(_dir.Path, "clinica").Paused.Should().BeTrue();
        _runtime.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/api/admin/reload");
    }

    [Fact]
    public async Task Integrations_ClientTogglesOnlyWhatIsInThePlan()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        var items = await client.GetFromJsonAsync<JsonElement[]>("/portal/api/bots/clinica/integrations");
        items!.Single(i => i.GetProperty("id").GetString() == "audio").GetProperty("canToggle").GetBoolean().Should().BeFalse();
        items!.Single(i => i.GetProperty("id").GetString() == "transbordo").GetProperty("canToggle").GetBoolean().Should().BeTrue();
        items!.Where(i => i.TryGetProperty("costNote", out _)).Should().BeEmpty("custo interno não aparece para o cliente");

        (await client.SendAsync(Post("/portal/api/bots/clinica/integrations/audio", new { enabled = false })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post("/portal/api/bots/clinica/integrations/transbordo", new { enabled = false })))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        BotIntegrations.IsEnabled(BotConfigLoader.Load(_dir.Path, "clinica"), IntegrationCatalog.Handoff).Should().BeFalse();
    }

    [Fact]
    public async Task DemoReset_OnlyForDemoBots()
    {
        using var factory = BuildFactory();
        var client = await LoggedInAsync(factory);

        (await client.SendAsync(Post("/portal/api/bots/demo/demo/reset"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(Post("/portal/api/bots/clinica/demo/reset"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Admin: bot de demonstração e acesso ao portal ────────────────────────

    [Fact]
    public async Task Admin_CreatesDemoBot_AndManagesPortalPhones()
    {
        using var factory = BuildFactory();
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:senha-admin")));

        var created = await admin.PostAsJsonAsync("/api/demo-bot", new { whatsAppNumber = "21 90000-0099", sellerPhones = new[] { "(21) 97777-6666" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var demo = BotConfigLoader.Load(_dir.Path, DemoBotService.TenantId);
        demo.IsDemo.Should().BeTrue();
        demo.PortalPhones.Should().Equal("5521977776666");
        demo.Type.Should().Be(BotType.Scheduling);
        (await admin.PostAsJsonAsync("/api/demo-bot", new { whatsAppNumber = "2190000009", sellerPhones = new[] { "21977776666" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "só existe um bot de demonstração");

        (await admin.PutAsJsonAsync("/api/bots/outra/portal", new { displayName = "Loja", portalPhones = new[] { "123" }, isDemo = false }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync("/api/bots/outra/portal", new { displayName = "Loja", portalPhones = new[] { "21 95555-4444" }, isDemo = false }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        BotConfigLoader.Load(_dir.Path, "outra").PortalPhones.Should().Equal("5521955554444");
    }

    [Theory]
    [InlineData("(21) 98888-7777", "5521988887777")]
    [InlineData("+55 21 98888-7777", "5521988887777")]
    [InlineData("2133334444", "552133334444")]
    public void NormalizePhone_AddsBrazilCode(string input, string expected) =>
        PortalAuthService.NormalizePhone(input).Should().Be(expected);

    public void Dispose()
    {
        _runtime.Stop();
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}
