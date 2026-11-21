using Agiliz.Core.Models;

namespace Agiliz.Core.Integrations;

/// <summary>Estado efetivo das integrações de um bot (o que o Runtime realmente usa).</summary>
public static class BotIntegrations
{
    /// <summary>
    /// Ligada de fato: disponível no catálogo, compatível com o tipo do bot, dependências ligadas
    /// e ligada no painel (ou, em bot antigo sem <see cref="BotConfig.Integrations"/>, pelo padrão antigo).
    /// </summary>
    public static bool IsEnabled(BotConfig config, string integrationId)
    {
        var def = IntegrationCatalog.Find(integrationId);
        if (def is null || BlockedReason(config, def) is not null) return false;
        if (def.AlwaysOn) return true;

        return config.Integrations is null
            ? LegacyDefault(config, def.Id)
            : config.Integrations.TryGetValue(def.Id, out var settings) && settings.Enabled;
    }

    /// <summary>Por que a integração não pode ser ligada neste bot (null = pode).</summary>
    public static string? BlockedReason(BotConfig config, IntegrationDefinition def)
    {
        if (def.Status == IntegrationStatus.OnRequest) return "Ativação sob consulta, feita pela equipe.";
        if (def.Status == IntegrationStatus.ComingSoon) return "Em breve.";
        if (def.OnlyFor is { } type && config.Type != type)
            return type == BotType.Scheduling ? "Só para bots de agendamento." : $"Só para bots do tipo {type}.";

        var missing = def.Requires.Where(r => !IsEnabled(config, r)).Select(r => IntegrationCatalog.Find(r)?.Name ?? r).ToList();
        return missing.Count > 0 ? $"Ligue antes: {string.Join(", ", missing)}." : null;
    }

    public static IReadOnlyList<IntegrationDefinition> Enabled(BotConfig config) =>
        IntegrationCatalog.All.Where(i => IsEnabled(config, i.Id)).ToList();

    /// <summary>Ferramentas que o agente deste bot pode receber.</summary>
    public static IReadOnlySet<string> AllowedToolNames(BotConfig config) =>
        Enabled(config).SelectMany(i => i.ToolNames).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string? Field(BotConfig config, string integrationId, string key) =>
        config.Integrations is not null &&
        config.Integrations.TryGetValue(integrationId, out var settings) &&
        settings.Fields.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <summary>ID da agenda: o campo da integração ou, em bot antigo, <see cref="BotConfig.GoogleCalendarId"/>.</summary>
    public static string? CalendarId(BotConfig config) =>
        Field(config, IntegrationCatalog.GoogleCalendar, IntegrationCatalog.CalendarIdField)
        ?? (string.IsNullOrWhiteSpace(config.GoogleCalendarId) ? null : config.GoogleCalendarId.Trim());

    /// <summary>Configuração do painel com todas as integrações explícitas, partindo do estado efetivo atual.</summary>
    public static Dictionary<string, IntegrationSettings> Materialize(BotConfig config)
    {
        var result = new Dictionary<string, IntegrationSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in IntegrationCatalog.All.Where(d => d.Status == IntegrationStatus.Available))
        {
            var existing = config.Integrations?.GetValueOrDefault(def.Id);
            if (def.AlwaysOn && existing is null) continue; // sempre ligada; só guarda entrada se houver passos marcados
            var fields = new Dictionary<string, string>(existing?.Fields ?? [], StringComparer.OrdinalIgnoreCase);
            if (def.Id == IntegrationCatalog.GoogleCalendar && !fields.ContainsKey(IntegrationCatalog.CalendarIdField) && CalendarId(config) is { } calendarId)
                fields[IntegrationCatalog.CalendarIdField] = calendarId;

            result[def.Id] = new IntegrationSettings
            {
                Enabled = def.AlwaysOn || (existing?.Enabled ?? (config.Integrations is null && LegacyDefault(config, def.Id))),
                Fields = fields,
                CompletedSteps = existing?.CompletedSteps ?? [],
                UpdatedAt = existing?.UpdatedAt
            };
        }
        return result;
    }

    /// <summary>
    /// Como os bots funcionavam antes do catálogo: tudo ligado (agenda e lembretes nos bots de agendamento,
    /// pelo OnlyFor). A agenda fica ligada mesmo sem ID: sem a ferramenta o agente inventaria horários;
    /// com ela, responde que não conseguiu consultar.
    /// </summary>
    private static bool LegacyDefault(BotConfig config, string integrationId) => integrationId switch
    {
        IntegrationCatalog.GoogleCalendar or IntegrationCatalog.Reminders or IntegrationCatalog.Audio
            or IntegrationCatalog.Email or IntegrationCatalog.Handoff => true,
        _ => false
    };
}
