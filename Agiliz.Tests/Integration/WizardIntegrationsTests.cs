using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agiliz.Core.Config;
using Agiliz.Core.Integrations;
using Agiliz.Core.Models;
using Agiliz.Tests.Helpers;
using Agiliz.Wizard.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Agiliz.Tests.Integration;

/// <summary>Aba Integrações do painel: liga/desliga por bot, campos, preço e "Testar conexão".</summary>
public sealed class WizardIntegrationsTests : IDisposable
{
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();
    private readonly WireMockServer _runtime = WireMockServer.Start();

    public WizardIntegrationsTests()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-groq-key");
        _runtime.Given(Request.Create().WithPath("/api/admin/reload").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { loaded = Array.Empty<string>() }));
        _runtime.Given(Request.Create().WithPath("/api/admin/integrations/google-agenda/test").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { ok = true, message = "Agenda acessível." }));
    }

    private WebApplicationFactory<Agiliz.Wizard.Program> BuildFactory() =>
        new WebApplicationFactory<Agiliz.Wizard.Program>()
            .WithWebHostBuilder(b => b
                .UseSetting("ConfigsDir", _dir.Path)
                .UseSetting("Runtime:ApiUrl", _runtime.Url)
                .UseSetting("Runtime:AdminToken", "token"));

    private void SaveScheduling(string tenantId = "clinica") =>
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: tenantId) with { Type = BotType.Scheduling });

    private BotConfig Load(string tenantId = "clinica") => BotConfigLoader.Load(_dir.Path, tenantId);

    private static async Task<string?> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    [Fact]
    public async Task GetBotIntegrations_LegacySchedulingBot_ShowsCalendarOn()
    {
        SaveScheduling();
        using var factory = BuildFactory();

        var body = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/bots/clinica/integrations");

        var google = body.GetProperty("integrations").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "google-agenda");
        google.GetProperty("enabled").GetBoolean().Should().BeTrue();
        body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains("sem ID da agenda"));
    }

    [Fact]
    public async Task EnableCalendar_WithoutCalendarId_Returns400()
    {
        SaveScheduling();
        using var factory = BuildFactory();

        var response = await factory.CreateClient().PutAsJsonAsync("/api/bots/clinica/integrations/google-agenda", new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Contain("ID da agenda");
    }

    [Fact]
    public async Task EnableCalendar_WithCalendarId_SavesAndReloadsRuntime()
    {
        SaveScheduling();
        using var factory = BuildFactory();

        var response = await factory.CreateClient().PutAsJsonAsync("/api/bots/clinica/integrations/google-agenda", new
        {
            enabled = true,
            fields = new Dictionary<string, string> { ["calendarId"] = "agenda@gmail.com", ["campoInventado"] = "x" },
            completedSteps = new[] { "compartilhar", "passo-inventado" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runtimeReloaded").GetBoolean().Should().BeTrue();

        var config = Load();
        BotIntegrations.CalendarId(config).Should().Be("agenda@gmail.com");
        config.Integrations![IntegrationCatalog.GoogleCalendar].Fields.Keys.Should().Equal("calendarId");
        config.Integrations[IntegrationCatalog.GoogleCalendar].CompletedSteps.Should().Equal("compartilhar");
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Email).Should().BeTrue("as outras integrações mantêm o estado de antes");
        _runtime.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/api/admin/reload");
    }

    [Fact]
    public async Task DisableEmail_RemovesToolFromAgent()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "loja"));
        using var factory = BuildFactory();

        var response = await factory.CreateClient().PutAsJsonAsync("/api/bots/loja/integrations/email", new { enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        BotIntegrations.AllowedToolNames(Load("loja")).Should().NotContain("send_email").And.Contain("chamar_atendente");
    }

    [Theory]
    [InlineData("google-agenda", "agendamento")]
    [InlineData("whatsapp-oficial", "sob consulta")]
    [InlineData("pix", "Em breve")]
    [InlineData("inventada", "fora do catálogo")]
    public async Task EnableBlockedIntegration_Returns400WithReason(string integrationId, string reason)
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "loja")); // bot genérico
        using var factory = BuildFactory();

        var response = await factory.CreateClient().PutAsJsonAsync($"/api/bots/loja/integrations/{integrationId}",
            new { enabled = true, fields = new Dictionary<string, string> { ["calendarId"] = "a@b.com" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().ContainEquivalentOf(reason);
    }

    [Fact]
    public async Task SetPrice_SavesMonthlyPrice()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "loja"));
        using var factory = BuildFactory();

        var response = await factory.CreateClient().PutAsJsonAsync("/api/bots/loja/price", new { monthlyPriceBrl = 197.90m });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Load("loja").MonthlyPriceBrl.Should().Be(197.90m);
    }

    [Fact]
    public async Task TestConnection_IsRunByTheRuntime()
    {
        SaveScheduling();
        using var factory = BuildFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/bots/clinica/integrations/google-agenda/test",
            new { fields = new Dictionary<string, string> { ["calendarId"] = "agenda@gmail.com" } });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("message").GetString().Should().Be("Agenda acessível.");
        var call = _runtime.LogEntries.Single(e => e.RequestMessage.Path.EndsWith("/test"));
        call.RequestMessage.Body.Should().Contain("\"tenantId\":\"clinica\"").And.Contain("agenda@gmail.com");
        call.RequestMessage.Headers!["X-Admin-Token"].Should().ContainSingle().Which.Should().Be("token");
    }

    [Fact]
    public async Task Catalog_ListsWhoUsesEachIntegration()
    {
        SaveScheduling("clinica");
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "loja"));
        using var factory = BuildFactory();

        var body = await factory.CreateClient().GetFromJsonAsync<JsonElement[]>("/api/integrations");

        Tenants(body!, "google-agenda").Should().Equal("clinica");
        Tenants(body!, "whatsapp").Should().BeEquivalentTo("clinica", "loja");
        Tenants(body!, "pix").Should().BeEmpty();

        static IEnumerable<string?> Tenants(JsonElement[] catalog, string id) =>
            catalog.Single(i => i.GetProperty("id").GetString() == id).GetProperty("tenants").EnumerateArray().Select(t => t.GetString());
    }

    public void Dispose()
    {
        _runtime.Stop();
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}

public sealed class CostSettingsTests
{
    private static CostSettings Settings(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void UsesDefaultRateUntilConfigured()
    {
        Settings().ConfiguredUsdToBrl.Should().BeNull();
        Settings().ToBrl(1m).Should().Be(CostSettings.DefaultUsdToBrl);
        Settings(("Billing:UsdToBrl", "6.10")).ToBrl(2m).Should().Be(12.20m);
    }

    [Fact]
    public void FixedCosts_SumOnlyEnabledIntegrations()
    {
        var settings = Settings(("Billing:FixedMonthlyBrl:whatsapp", "15"), ("Billing:FixedMonthlyBrl:google-agenda", "5"));

        settings.FixedMonthlyBrl(TestFixtures.DefaultConfig()).Should().Be(15m, "bot genérico não tem agenda");
        settings.FixedMonthlyBrl(TestFixtures.DefaultConfig() with { Type = BotType.Scheduling }).Should().Be(20m);
    }
}
