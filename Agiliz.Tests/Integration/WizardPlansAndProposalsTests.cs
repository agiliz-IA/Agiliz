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
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Agiliz.Tests.Integration;

/// <summary>Fase 2 no painel: planos, franquia, quiz, propostas e bot criado a partir da proposta.</summary>
public sealed class WizardPlansAndProposalsTests : IDisposable
{
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();
    private readonly WireMockServer _runtime = WireMockServer.Start();

    private static readonly Dictionary<string, string> DentalAnswers = new()
    {
        ["ramo"] = "odonto", ["profissionais"] = "um", ["volume"] = "ate100", ["agenda"] = "google",
        ["numero"] = "novo", ["audio"] = "as-vezes", ["lembretes"] = "sim", ["sinal"] = "nao",
        ["transbordo"] = "sim", ["sensiveis"] = "sim"
    };

    public WizardPlansAndProposalsTests()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-groq-key");
        _runtime.Given(Request.Create().WithPath("/api/admin/reload").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { loaded = Array.Empty<string>() }));
    }

    private WebApplicationFactory<Agiliz.Wizard.Program> BuildFactory() =>
        new WebApplicationFactory<Agiliz.Wizard.Program>()
            .WithWebHostBuilder(b => b
                .UseSetting("ConfigsDir", _dir.Path)
                .UseSetting("Runtime:ApiUrl", _runtime.Url)
                .UseSetting("Runtime:AdminToken", "token"));

    private BotConfig Load(string tenantId) => BotConfigLoader.Load(_dir.Path, tenantId);

    // ─── P1: planos ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Plans_StartWithProvisionalSet_AndCanBeEdited()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var plans = await client.GetFromJsonAsync<JsonElement[]>("/api/plans");
        plans!.Select(p => p.GetProperty("id").GetString()).Should().Equal("essencial", "profissional", "clinica");

        var put = await client.PutAsJsonAsync("/api/plans/essencial", new
        {
            name = "Essencial", monthlyPriceBrl = 159m, conversationAllowance = 200, maxProfessionals = 1,
            includedIntegrations = new[] { "google-agenda", "transbordo" }, addOnPricesBrl = new Dictionary<string, decimal> { ["audio"] = 15m }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("monthlyPriceBrl").GetDecimal().Should().Be(159m);

        var invalid = await client.PutAsJsonAsync("/api/plans/novo", new { name = "Novo", monthlyPriceBrl = 10m, conversationAllowance = 0 });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeletePlan_InUse_Returns409()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica") with { PlanId = "essencial" });
        using var factory = BuildFactory();

        var response = await factory.CreateClient().DeleteAsync("/api/plans/essencial");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ─── P2: plano no bot e franquia ──────────────────────────────────────────

    [Fact]
    public async Task AssignPlan_SavesPlanOnBot()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "loja"));
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/bots/loja/plan", new { planId = "profissional" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Load("loja").PlanId.Should().Be("profissional");

        (await client.PutAsJsonAsync("/api/bots/loja/plan", new { planId = "nao-existe" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Usage_WarnsAt80PercentOfAllowance()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "1") with { PlanId = "essencial" });
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "loja", whatsappNumber: "2"));
        _runtime.Given(Request.Create().WithPath("/api/admin/usage").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
                {
                    month = "2026-11",
                    tenants = new[] { new { tenantId = "clinica", contacts = 130, messages = 400 }, new { tenantId = "loja", contacts = 50, messages = 90 } }
                }));
        using var factory = BuildFactory();

        var body = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/usage?month=2026-11");

        var bots = body.GetProperty("bots").EnumerateArray().ToDictionary(b => b.GetProperty("tenantId").GetString()!);
        bots["clinica"].GetProperty("level").GetString().Should().Be("Warning");
        bots["clinica"].GetProperty("allowance").GetInt32().Should().Be(150);
        bots["loja"].GetProperty("level").GetString().Should().Be("NoPlan");
        _runtime.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/api/admin/usage" && e.RequestMessage.Query!["month"].Contains("2026-11"));
    }

    // ─── P3/P5: quiz e propostas ──────────────────────────────────────────────

    [Fact]
    public async Task QuizEvaluate_ReturnsRecommendation()
    {
        using var factory = BuildFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/quiz/evaluate", new { answers = DentalAnswers });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("complete").GetBoolean().Should().BeTrue();
        body.GetProperty("plan").GetProperty("id").GetString().Should().Be("essencial");
        body.GetProperty("monthlyPriceBrl").GetDecimal().Should().Be(189m);
    }

    [Fact]
    public async Task Proposal_SaveLoseReopen_AndStats()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/proposals", new { businessName = "Clínica Sorriso", contactName = "Ana", answers = DentalAnswers });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var proposal = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = proposal.GetProperty("id").GetString()!;
        proposal.GetProperty("planId").GetString().Should().Be("essencial");

        (await client.PostAsJsonAsync("/api/proposals", new { businessName = " ", answers = DentalAnswers })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync($"/api/proposals/{id}/lost", new { reason = "achou caro" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/proposals");
        list.GetProperty("stats").GetProperty("lost").GetInt32().Should().Be(1);
        list.GetProperty("proposals")[0].GetProperty("lostReason").GetString().Should().Be("achou caro");

        (await client.PostAsync($"/api/proposals/{id}/reopen", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonElement>($"/api/proposals/{id}")).GetProperty("status").GetString().Should().Be("Open");
    }

    // ─── P4: bot criado a partir da proposta ──────────────────────────────────

    [Fact]
    public async Task ApplyProposal_SetsPlanIntegrationsLgpd_AndCountsConversion()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica-sorriso") with
        {
            Type = BotType.Scheduling,
            GoogleCalendarId = "agenda@gmail.com"
        });
        using var factory = BuildFactory();
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/proposals", new { businessName = "Clínica Sorriso", answers = DentalAnswers });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var applied = await client.PostAsync($"/api/proposals/{id}/apply/clinica-sorriso", null);

        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        var config = Load("clinica-sorriso");
        config.PlanId.Should().Be("essencial");
        config.LgpdConsentRequired.Should().BeTrue();
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Audio).Should().BeTrue();
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Email).Should().BeFalse("o quiz não pediu e-mail");
        BotIntegrations.CalendarId(config).Should().Be("agenda@gmail.com");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/proposals");
        list.GetProperty("stats").GetProperty("converted").GetInt32().Should().Be(1);
        list.GetProperty("proposals")[0].GetProperty("tenantId").GetString().Should().Be("clinica-sorriso");

        (await client.PutAsJsonAsync($"/api/proposals/{id}", new { businessName = "Outro", answers = DentalAnswers }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "proposta convertida não é editada");
    }

    [Fact]
    public void SuggestTenantId_SlugifiesBusinessName()
    {
        ProposalService.SuggestTenantId("Clínica Sorriso & Cia").Should().Be("clinica-sorriso-cia");
        ProposalService.SuggestTenantId("!!!").Should().Be("novo-bot");
    }

    // ─── Correção: salvar o bot de novo não apaga o que é gerido em outras telas ─

    [Fact]
    public async Task ResavingBot_KeepsIntegrationsPlanAndPrice()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "loja") with
        {
            PlanId = "essencial",
            MonthlyPriceBrl = 99m,
            Integrations = new() { [IntegrationCatalog.Email] = new IntegrationSettings { Enabled = false } }
        });
        using var factory = BuildFactory();

        await factory.CreateClient().PostAsJsonAsync("/api/bots", new
        {
            tenantId = "loja", whatsappNumber = "15005550006", provider = "Groq", systemPrompt = "Prompt novo.", flows = Array.Empty<object>()
        });

        var config = Load("loja");
        config.SystemPrompt.Should().Be("Prompt novo.");
        config.PlanId.Should().Be("essencial");
        config.MonthlyPriceBrl.Should().Be(99m);
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Email).Should().BeFalse();
    }

    public void Dispose()
    {
        _runtime.Stop();
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}
