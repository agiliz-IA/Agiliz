using Agiliz.Core.Integrations;
using Agiliz.Core.Models;
using Agiliz.Core.Tools;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class IntegrationCatalogTests
{
    [Fact]
    public void Catalog_IdsAreUniqueAndRequirementsExist()
    {
        var ids = IntegrationCatalog.All.Select(i => i.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
        IntegrationCatalog.All.SelectMany(i => i.Requires).Should().OnlyContain(r => ids.Contains(r));
        IntegrationCatalog.All.Where(i => i.Testable).Should().OnlyContain(i => i.Status == IntegrationStatus.Available);
    }

    [Fact]
    public void EveryRuntimeTool_BelongsToAnIntegration()
    {
        // Uma ferramenta fora do catálogo nunca chegaria ao agente (BotRunner filtra pelo catálogo).
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-groq-key");
        using var dir = TestFixtures.CreateTempDir();
        using var factory = new WebApplicationFactory<Agiliz.Runtime.Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConfigsDir", dir.Path));

        var toolNames = factory.Services.GetServices<ITool>().Select(t => t.Name).ToList();

        toolNames.Should().NotBeEmpty();
        toolNames.Should().OnlyContain(name => IntegrationCatalog.ForTool(name) != null);
    }

    [Fact]
    public void ForTool_MapsToolsToTheirIntegration()
    {
        IntegrationCatalog.ForTool("send_email")!.Id.Should().Be(IntegrationCatalog.Email);
        IntegrationCatalog.ForTool("marcar_agenda")!.Id.Should().Be(IntegrationCatalog.GoogleCalendar);
        IntegrationCatalog.ForTool("chamar_atendente")!.Id.Should().Be(IntegrationCatalog.Handoff);
        IntegrationCatalog.ForTool("nao_existe").Should().BeNull();
    }
}

public sealed class BotIntegrationsTests
{
    private static BotConfig Scheduling() => TestFixtures.DefaultConfig() with { Type = BotType.Scheduling };

    private static BotConfig WithIntegrations(BotConfig config, params (string Id, bool Enabled)[] integrations) => config with
    {
        Integrations = integrations.ToDictionary(i => i.Id, i => new IntegrationSettings { Enabled = i.Enabled })
    };

    [Fact]
    public void LegacyGenericBot_KeepsOldBehavior()
    {
        var config = TestFixtures.DefaultConfig(); // sem Integrations: bot criado antes do catálogo

        BotIntegrations.IsEnabled(config, IntegrationCatalog.Ai).Should().BeTrue();
        BotIntegrations.IsEnabled(config, IntegrationCatalog.WhatsApp).Should().BeTrue();
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Audio).Should().BeTrue();
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Email).Should().BeTrue();
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Handoff).Should().BeTrue();
        BotIntegrations.IsEnabled(config, IntegrationCatalog.GoogleCalendar).Should().BeFalse("agenda é só para bot de agendamento");
        BotIntegrations.AllowedToolNames(config).Should().BeEquivalentTo("send_email", "chamar_atendente");
    }

    [Fact]
    public void LegacySchedulingBot_HasCalendarToolsAndReminders()
    {
        var config = Scheduling();

        BotIntegrations.IsEnabled(config, IntegrationCatalog.GoogleCalendar).Should().BeTrue();
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Reminders).Should().BeTrue();
        BotIntegrations.AllowedToolNames(config).Should().Contain(["verificar_agenda", "marcar_agenda", "send_email"]);
    }

    [Fact]
    public void ExplicitSettings_DisableToolsOfTurnedOffIntegrations()
    {
        var config = WithIntegrations(TestFixtures.DefaultConfig(), (IntegrationCatalog.Email, false), (IntegrationCatalog.Handoff, true));

        BotIntegrations.AllowedToolNames(config).Should().BeEquivalentTo("chamar_atendente");
        BotIntegrations.IsEnabled(config, IntegrationCatalog.Audio).Should().BeFalse("não está na lista explícita");
        BotIntegrations.IsEnabled(config, IntegrationCatalog.WhatsApp).Should().BeTrue("sempre ligada");
    }

    [Fact]
    public void Reminders_RequireCalendar()
    {
        var config = WithIntegrations(Scheduling(), (IntegrationCatalog.GoogleCalendar, false), (IntegrationCatalog.Reminders, true));

        BotIntegrations.IsEnabled(config, IntegrationCatalog.Reminders).Should().BeFalse();
        BotIntegrations.BlockedReason(config, IntegrationCatalog.Find(IntegrationCatalog.Reminders)!).Should().Contain("Google Agenda");
    }

    [Fact]
    public void OnRequestIntegration_IsNeverEnabledByConfig()
    {
        var config = WithIntegrations(TestFixtures.DefaultConfig(), (IntegrationCatalog.WhatsAppOfficial, true));
        BotIntegrations.IsEnabled(config, IntegrationCatalog.WhatsAppOfficial).Should().BeFalse();
    }

    [Fact]
    public void CalendarId_PrefersIntegrationFieldOverLegacyProperty()
    {
        var legacy = Scheduling() with { GoogleCalendarId = "antigo@group.calendar.google.com" };
        BotIntegrations.CalendarId(legacy).Should().Be("antigo@group.calendar.google.com");

        var configured = legacy with
        {
            Integrations = new()
            {
                [IntegrationCatalog.GoogleCalendar] = new IntegrationSettings
                {
                    Enabled = true,
                    Fields = new() { [IntegrationCatalog.CalendarIdField] = " novo@gmail.com " }
                }
            }
        };
        BotIntegrations.CalendarId(configured).Should().Be("novo@gmail.com");
    }

    [Fact]
    public void Materialize_PreservesLegacyStateAndCalendarId()
    {
        var legacy = Scheduling() with { GoogleCalendarId = "agenda@gmail.com" };

        var materialized = BotIntegrations.Materialize(legacy);
        var explicitConfig = legacy with { Integrations = materialized };

        foreach (var def in IntegrationCatalog.All)
            BotIntegrations.IsEnabled(explicitConfig, def.Id).Should().Be(BotIntegrations.IsEnabled(legacy, def.Id), def.Id);
        materialized[IntegrationCatalog.GoogleCalendar].Fields[IntegrationCatalog.CalendarIdField].Should().Be("agenda@gmail.com");
    }
}
