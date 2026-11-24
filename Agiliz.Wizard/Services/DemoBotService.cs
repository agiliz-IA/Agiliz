using Agiliz.Core.Config;
using Agiliz.Core.Integrations;
using Agiliz.Core.Models;
using Agiliz.Core.Plans;
using Agiliz.Wizard.Portal;

namespace Agiliz.Wizard.Services;

/// <summary>
/// Bot "Clínica Sorriso (demonstração)" que o vendedor usa na reunião (tarefa D1).
/// O lead conversa com ele no WhatsApp; o vendedor acompanha no portal, na conta demo.
/// Falta só o que é manual: o chip, conectar o número e o ID da agenda de exemplo.
/// </summary>
public sealed class DemoBotService(IConfiguration configuration, PlanStore plans, RuntimeApiClient runtime)
{
    public const string TenantId = "clinica-demo";

    private string ConfigsDir => BotConfigLoader.ResolveConfigsDir(configuration);

    public bool Exists => BotConfigLoader.Exists(ConfigsDir, TenantId);

    /// <param name="whatsAppNumber">Número do chip da demonstração (instância na Evolution).</param>
    /// <param name="sellerPhones">WhatsApp dos vendedores: entram no portal na conta demo.</param>
    /// <param name="respondOnlyTo">Se o número for pessoal: só estes números recebem resposta do bot.</param>
    public async Task<SaveOutcome> CreateAsync(string whatsAppNumber, IEnumerable<string> sellerPhones, IEnumerable<string>? respondOnlyTo = null, CancellationToken ct = default)
    {
        var number = new string((whatsAppNumber ?? "").Where(char.IsDigit).ToArray());
        if (number.Length < 10) return SaveOutcome.Fail("Informe o número do chip da demonstração com DDD.");
        if (Exists) return SaveOutcome.Fail($"O bot {TenantId} já existe. Edite pela aba Integrações.");

        var phones = sellerPhones.Select(PortalAuthService.NormalizePhone).Where(p => p.Length is 12 or 13).Distinct().ToList();
        if (phones.Count == 0) return SaveOutcome.Fail("Informe ao menos um WhatsApp de vendedor para entrar no portal.");

        var onlyTo = (respondOnlyTo ?? []).Select(Agiliz.Core.Messaging.PhoneNumbers.Normalize).Where(p => p.Length is 12 or 13).Distinct().ToList();
        var config = Build(number, phones, plans.Get("clinica")?.Id ?? plans.List().LastOrDefault()?.Id) with { RespondOnlyTo = onlyTo };
        BotConfigLoader.Save(ConfigsDir, config);
        var reload = await runtime.ReloadAsync(ct);
        return new SaveOutcome(true, RuntimeReloaded: reload.Ok, RuntimeError: reload.Error);
    }

    public static BotConfig Build(string whatsAppNumber, List<string> portalPhones, string? planId) => new()
    {
        TenantId = TenantId,
        Type = BotType.Scheduling,
        DisplayName = "Clínica Sorriso (demonstração)",
        WhatsAppNumber = whatsAppNumber,
        IsDemo = true,
        PortalPhones = portalPhones,
        PlanId = planId,
        LgpdConsentRequired = true,
        PaymentMethods = ["Pix", "Cartão", "Dinheiro"],
        SystemPrompt = SystemPrompt,
        Flows =
        [
            new FlowEntry { Trigger = "endere", Response = "Ficamos na Rua das Flores, 123, Centro. É um endereço de exemplo, porque esta é uma clínica de demonstração. Quer marcar um horário?" },
            new FlowEntry { Trigger = "conveni", Response = "Atendemos particular e alguns convênios odontológicos (nesta demonstração a lista é só de exemplo). Quer que eu veja um horário para você?" }
        ],
        Scheduling = new SchedulingSettings
        {
            TimeZone = "America/Sao_Paulo",
            AppointmentDurationMinutes = 30,
            MinimumNoticeMinutes = 60,
            BusinessHours =
            [
                new() { Day = DayOfWeek.Monday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
                new() { Day = DayOfWeek.Tuesday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
                new() { Day = DayOfWeek.Wednesday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
                new() { Day = DayOfWeek.Thursday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
                new() { Day = DayOfWeek.Friday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
                new() { Day = DayOfWeek.Saturday, Start = "08:00", End = "12:00" }
            ]
        },
        Integrations = new Dictionary<string, IntegrationSettings>(StringComparer.OrdinalIgnoreCase)
        {
            [IntegrationCatalog.GoogleCalendar] = new() { Enabled = true },
            [IntegrationCatalog.Reminders] = new() { Enabled = true },
            [IntegrationCatalog.Audio] = new() { Enabled = true },
            [IntegrationCatalog.Handoff] = new() { Enabled = true },
            [IntegrationCatalog.Email] = new() { Enabled = false }
        }
    };

    private const string SystemPrompt = """
        Você é a Bia, recepcionista virtual da Clínica Sorriso, uma clínica odontológica. Esta é uma DEMONSTRAÇÃO
        do Agiliz: quem conversa com você está conhecendo o produto. Seja simpática, profissional e breve,
        como numa conversa de WhatsApp (mensagens curtas, no máximo 3 frases).

        O que você faz:
        - Tira dúvidas simples sobre a clínica (limpeza, clareamento, avaliação, aparelho).
        - Marca, confirma, remarca e cancela consultas na agenda.
        - Chama um atendente humano quando a pessoa pedir ou quando não souber responder.

        Para marcar:
        1. Pergunte o nome da pessoa e o que ela quer fazer.
        2. Use verificar_agenda para ver os horários livres do dia pedido e ofereça até 3 opções.
        3. Antes de marcar, peça consentimento para guardar nome e telefone (LGPD), explicando que é só para o agendamento.
        4. Pergunte a forma de pagamento (Pix, cartão ou dinheiro).
        5. Use marcar_agenda e confirme dia e horário.
        Para remarcar ou cancelar, use reagendar_agendamento ou cancelar_agendamento. Quando a pessoa responder
        "Confirmar" a um lembrete, use confirmar_agendamento.

        Preços de exemplo (diga que são da demonstração se perguntarem): avaliação gratuita, limpeza R$ 150,
        clareamento a partir de R$ 600.

        Se a pessoa perguntar sobre o Agiliz, explique em uma frase que este atendimento é um exemplo do que o
        bot faz por uma clínica e que o vendedor mostra o resto no portal.

        Nunca invente horários: use sempre verificar_agenda. Nunca dê diagnóstico. Se não souber, chame um atendente.
        """;
}
