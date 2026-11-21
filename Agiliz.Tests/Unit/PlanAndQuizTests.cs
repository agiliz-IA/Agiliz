using Agiliz.Core.Integrations;
using Agiliz.Core.Models;
using Agiliz.Core.Plans;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class PlanStoreTests : IDisposable
{
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();

    [Fact]
    public void FirstList_SeedsProvisionalPlans_AndDeletingAllDoesNotReseed()
    {
        var store = new PlanStore(_dir.Path);

        store.List().Select(p => p.Id).Should().Equal("essencial", "profissional", "clinica");
        store.List().Should().OnlyContain(p => p.Provisional);

        foreach (var p in store.List()) store.Delete(p.Id);
        store.List().Should().BeEmpty();
    }

    [Theory]
    [InlineData("../x", 100, "inventada", "Id do plano")]
    [InlineData("ok", 0, null, "franquia")]
    [InlineData("ok", 100, "inventada", "fora do catálogo")]
    public void Save_RejectsInvalidPlans(string id, int allowance, string? integration, string error)
    {
        var plan = new Plan
        {
            Id = id, Name = "X", MonthlyPriceBrl = 100, ConversationAllowance = allowance,
            IncludedIntegrations = integration is null ? [] : [integration]
        };
        new PlanStore(_dir.Path).Save(plan).Should().Contain(error);
    }

    public void Dispose() => _dir.Dispose();
}

public sealed class BotPricingAndAllowanceTests
{
    private static readonly Plan Essencial = PlanStore.ProvisionalPlans[0]; // R$149, 150 contatos, áudio +20

    [Fact]
    public void Price_IsPlanPlusEnabledAddOns_UnlessNegotiated()
    {
        var bot = TestFixtures.DefaultConfig() with { PlanId = "essencial" }; // legado: áudio, e-mail, transbordo ligados

        BotPricing.MonthlyPriceBrl(bot, Essencial).Should().Be(149m + 20m + 10m, "áudio e e-mail são adicionais no Essencial");
        BotPricing.MonthlyPriceBrl(bot with { MonthlyPriceBrl = 120m }, Essencial).Should().Be(120m);
        BotPricing.MonthlyPriceBrl(bot, plan: null).Should().BeNull();
    }

    [Theory]
    [InlineData(119, AllowanceLevel.Ok)]
    [InlineData(120, AllowanceLevel.Warning)]
    [InlineData(150, AllowanceLevel.Exceeded)]
    public void Allowance_WarnsAt80AndExceedsAt100Percent(int contacts, AllowanceLevel level) =>
        AllowanceStatus.For("bot", Essencial, contacts).Level.Should().Be(level);

    [Fact]
    public void Allowance_ComputesOverageRevenue()
    {
        var status = AllowanceStatus.For("bot", Essencial, 160);
        status.OverageContacts.Should().Be(10);
        status.OverageRevenueBrl.Should().Be(10 * 0.80m);
        AllowanceStatus.For("bot", plan: null, 500).Level.Should().Be(AllowanceLevel.NoPlan);
    }

    [Fact]
    public void UsageMonth_UsesBrasiliaTime()
    {
        // 02:00 UTC de 1º/12 ainda é 30/11 em Brasília.
        UsageMonth.For(new DateTimeOffset(2026, 12, 1, 2, 0, 0, TimeSpan.Zero)).Should().Be("2026-11");
        UsageMonth.IsValid("2026-11").Should().BeTrue();
        UsageMonth.IsValid("2026-13").Should().BeFalse();
        UsageMonth.IsValid("11-2026").Should().BeFalse();
    }
}

public sealed class QuizEngineTests
{
    private static readonly IReadOnlyList<Plan> Plans = PlanStore.ProvisionalPlans;

    private static Dictionary<string, string> Answers(params (string Q, string A)[] overrides)
    {
        var answers = new Dictionary<string, string>
        {
            ["ramo"] = "odonto", ["profissionais"] = "um", ["volume"] = "ate100", ["agenda"] = "google",
            ["numero"] = "novo", ["audio"] = "as-vezes", ["lembretes"] = "sim", ["sinal"] = "nao",
            ["transbordo"] = "sim", ["sensiveis"] = "sim"
        };
        foreach (var (q, a) in overrides) answers[q] = a;
        return answers;
    }

    private static QuizRecommendation Run(Dictionary<string, string> answers, Func<string, decimal>? fixedCosts = null) =>
        QuizEngine.Evaluate(answers, Plans, usdToBrl: 5.5m, fixedCosts);

    [Fact]
    public void Incomplete_ListsMissingQuestions()
    {
        var result = QuizEngine.Evaluate(new Dictionary<string, string> { ["ramo"] = "odonto", ["volume"] = "invalida" }, Plans, 5.5m);
        result.Complete.Should().BeFalse();
        result.MissingAnswers.Should().HaveCount(9);
        result.Plan.Should().BeNull();
    }

    [Fact]
    public void SmallDentalClinic_GetsCheapestPlanWithAddOns()
    {
        var result = Run(Answers());

        result.BotType.Should().Be(BotType.Scheduling);
        result.Plan!.Id.Should().Be("essencial", "149 + áudio 20 + lembretes 20 = 189 é menor que 249");
        result.MonthlyPriceBrl.Should().Be(189m);
        result.LgpdConsentRequired.Should().BeTrue();
        result.Integrations.Should().Contain(i => i.Id == IntegrationCatalog.GoogleCalendar && i.Offer == IntegrationOffer.Included);
        result.Integrations.Should().Contain(i => i.Id == IntegrationCatalog.Audio && i.Offer == IntegrationOffer.AddOn && i.AddOnPriceBrl == 20m);
        result.IntegrationsToEnable.Should().BeEquivalentTo(
            IntegrationCatalog.GoogleCalendar, IntegrationCatalog.Reminders, IntegrationCatalog.Audio, IntegrationCatalog.Handoff);
    }

    [Fact]
    public void BigTeam_GetsPlanWithoutProfessionalLimit() =>
        Run(Answers(("profissionais", "mais"))).Plan!.Id.Should().Be("clinica");

    [Fact]
    public void VolumeAboveEveryPlan_PicksBiggestAllowanceWithOverage()
    {
        var result = Run(Answers(("volume", "mais800")));

        result.Plan!.Id.Should().Be("clinica");
        result.EstimatedContacts.Should().Be(1200);
        result.OverageRevenueBrl.Should().Be((1200 - 1000) * 0.40m);
        result.PlanReason.Should().Contain("excedente");
    }

    [Fact]
    public void Commerce_IsGenericBotWithoutCalendar()
    {
        var result = Run(Answers(("ramo", "comercio"), ("agenda", "nenhuma"), ("sensiveis", "nao")));

        result.BotType.Should().Be(BotType.Generic);
        result.Integrations.Should().NotContain(i => i.Id == IntegrationCatalog.GoogleCalendar || i.Id == IntegrationCatalog.Reminders);
        result.LgpdConsentRequired.Should().BeFalse();
        result.Notes.Should().Contain(n => n.Contains("Lembretes dependem de agenda"));
    }

    [Fact]
    public void MainNumberAndOtherSystem_AreFlaggedAsNotYetAvailable()
    {
        var result = Run(Answers(("numero", "principal"), ("agenda", "outro-sistema"), ("sinal", "sim")));

        result.Integrations.Should().Contain(i => i.Id == IntegrationCatalog.WhatsAppOfficial && i.Offer == IntegrationOffer.OnRequest);
        result.Integrations.Should().Contain(i => i.Id == IntegrationCatalog.ClinicSystems && i.Offer == IntegrationOffer.ComingSoon);
        result.Integrations.Should().Contain(i => i.Id == IntegrationCatalog.Pix && i.Offer == IntegrationOffer.ComingSoon);
        result.IntegrationsToEnable.Should().NotContain([IntegrationCatalog.WhatsAppOfficial, IntegrationCatalog.ClinicSystems, IntegrationCatalog.Pix]);
    }

    [Fact]
    public void Margin_IsPriceMinusUsageAndFixedCosts()
    {
        var result = Run(Answers(), id => id == IntegrationCatalog.WhatsApp ? 15m : 0m);

        result.EstimatedUsageCostBrl.Should().BeGreaterThan(0m).And.BeLessThan(5m, "80 contatos com gpt-oss-120b custam centavos");
        result.EstimatedFixedCostBrl.Should().Be(15m);
        result.MarginBrl.Should().Be(result.MonthlyPriceBrl - result.EstimatedCostBrl);
    }

    [Fact]
    public void InactivePlans_AreIgnored()
    {
        var plans = Plans.Select(p => p.Id == "essencial" ? p with { Active = false } : p).ToList();
        QuizEngine.Evaluate(Answers(), plans, 5.5m).Plan!.Id.Should().Be("profissional");
    }

    [Fact]
    public void MetaAgentContext_CarriesAnswersAndSaysNotToAskAgain()
    {
        var answers = Answers();
        var text = QuizEngine.DescribeForMetaAgent(answers, Run(answers), "Clínica Sorriso");

        text.Should().Contain("NÃO pergunte").And.Contain("Clínica Sorriso").And.Contain("Odontologia").And.Contain("lgpdConsentRequired = true");
    }
}
