using Agiliz.Core.Integrations;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;

namespace Agiliz.Core.Plans;

public sealed record QuizOption(string Id, string Label);

/// <summary>Pergunta do quiz. <see cref="Decides"/> aparece no painel para quem aplica entender o porquê.</summary>
public sealed record QuizQuestion(string Id, string Text, string Decides, IReadOnlyList<QuizOption> Options);

public enum IntegrationOffer { Included, AddOn, NotInPlan, OnRequest, ComingSoon }

public sealed record RecommendedIntegration(string Id, string Name, IntegrationOffer Offer, decimal? AddOnPriceBrl, string Reason);

public sealed record QuizRecommendation
{
    public IReadOnlyList<string> MissingAnswers { get; init; } = [];
    public bool Complete => MissingAnswers.Count == 0;

    public BotType BotType { get; init; }
    public Plan? Plan { get; init; }
    public string? PlanReason { get; init; }

    /// <summary>Integrações sugeridas (inclusive as que ainda não dá para ligar, com o motivo).</summary>
    public IReadOnlyList<RecommendedIntegration> Integrations { get; init; } = [];

    public bool LgpdConsentRequired { get; init; }
    public int EstimatedContacts { get; init; }
    public decimal EstimatedUsageCostBrl { get; init; }
    public decimal EstimatedFixedCostBrl { get; init; }
    public decimal EstimatedCostBrl => EstimatedUsageCostBrl + EstimatedFixedCostBrl;

    /// <summary>Plano + adicionais das integrações sugeridas.</summary>
    public decimal? MonthlyPriceBrl { get; init; }
    public decimal? OverageRevenueBrl { get; init; }
    public decimal? MarginBrl => MonthlyPriceBrl is { } price ? price + (OverageRevenueBrl ?? 0m) - EstimatedCostBrl : null;

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Integrações que podem ser ligadas no bot criado a partir do quiz.</summary>
    public IEnumerable<string> IntegrationsToEnable => Integrations
        .Where(i => i.Offer is IntegrationOffer.Included or IntegrationOffer.AddOn or IntegrationOffer.NotInPlan)
        .Select(i => i.Id);
}

/// <summary>
/// Quiz de configuração aplicado na conversa de venda. Regras fixas, sem IA: as mesmas respostas
/// sempre dão a mesma recomendação. Para mudar preços e franquias, edite os planos no painel.
/// </summary>
public static class QuizEngine
{
    private static readonly System.Globalization.CultureInfo PtBr = new("pt-BR");

    // Premissas da estimativa de custo (mostradas no painel).
    public const int TurnsPerContact = 8;
    public const int PromptTokensPerTurn = 2500;
    public const int CompletionTokensPerTurn = 300;
    public const int AudioSecondsPerMessage = 20;

    public static IReadOnlyList<QuizQuestion> Questions { get; } =
    [
        new("ramo", "Qual é o ramo do negócio?", "Tipo de bot (agendamento ou atendimento) e integrações sugeridas.",
        [
            new("saude", "Clínica ou consultório de saúde"),
            new("odonto", "Odontologia"),
            new("estetica", "Estética, beleza ou bem-estar"),
            new("servicos", "Outro serviço com hora marcada"),
            new("comercio", "Comércio ou atendimento sem agenda")
        ]),
        new("profissionais", "Quantos profissionais atendem?", "Plano (limite de profissionais).",
        [
            new("um", "1"),
            new("ate3", "2 ou 3"),
            new("mais", "4 ou mais")
        ]),
        new("volume", "Quantas conversas novas chegam por mês no WhatsApp?", "Franquia do plano e custo estimado.",
        [
            new("ate100", "Até 100"),
            new("ate300", "De 100 a 300"),
            new("ate800", "De 300 a 800"),
            new("mais800", "Mais de 800")
        ]),
        new("agenda", "Onde fica a agenda hoje?", "Google Agenda, sistema de clínica (em breve) ou sem agenda.",
        [
            new("google", "Google Agenda"),
            new("outro-sistema", "Outro sistema de agenda"),
            new("papel", "Papel ou caderno"),
            new("nenhuma", "Não usa agenda")
        ]),
        new("numero", "Pode usar um número novo, ou precisa ser o número principal?", "WhatsApp não oficial ou oficial (maior diferença de custo e risco).",
        [
            new("novo", "Pode ser um número novo só para o bot"),
            new("principal", "Precisa ser o número principal da empresa")
        ]),
        new("audio", "Os clientes mandam muito áudio?", "Transcrição de áudio.",
        [
            new("muito", "Sim, muito"),
            new("as-vezes", "Às vezes"),
            new("nao", "Quase nunca")
        ]),
        new("lembretes", "Quer confirmação automática na véspera?", "Lembretes.",
        [
            new("sim", "Sim"),
            new("nao", "Não")
        ]),
        new("sinal", "Cobra sinal ou pré-pagamento?", "Pix (em breve).",
        [
            new("sim", "Sim"),
            new("nao", "Não")
        ]),
        new("transbordo", "Alguém da equipe precisa assumir conversas?", "Transbordo para humano.",
        [
            new("sim", "Sim"),
            new("nao", "Não, o bot resolve sozinho")
        ]),
        new("sensiveis", "Lida com dados de saúde ou outros dados sensíveis?", "Consentimento LGPD no fluxo do bot.",
        [
            new("sim", "Sim"),
            new("nao", "Não")
        ])
    ];

    public static QuizQuestion? Question(string id) => Questions.FirstOrDefault(q => q.Id == id);

    /// <summary>Respostas válidas (ids de pergunta e opção conhecidos); o resto é descartado.</summary>
    public static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? answers)
    {
        var clean = new Dictionary<string, string>();
        foreach (var q in Questions)
            if (answers?.GetValueOrDefault(q.Id) is { } option && q.Options.Any(o => o.Id == option))
                clean[q.Id] = option;
        return clean;
    }

    /// <param name="plans">Planos cadastrados (só os ativos entram na escolha).</param>
    /// <param name="usdToBrl">Câmbio para o custo estimado.</param>
    /// <param name="fixedMonthlyBrl">Custo fixo mensal por integração (configuração do painel).</param>
    public static QuizRecommendation Evaluate(
        IReadOnlyDictionary<string, string>? answers,
        IReadOnlyList<Plan> plans,
        decimal usdToBrl,
        Func<string, decimal>? fixedMonthlyBrl = null)
    {
        var a = Clean(answers);
        var missing = Questions.Where(q => !a.ContainsKey(q.Id)).Select(q => q.Text).ToList();
        if (missing.Count > 0) return new QuizRecommendation { MissingAnswers = missing };

        var notes = new List<string>();
        var scheduling = a["ramo"] != "comercio";
        var botType = scheduling ? BotType.Scheduling : BotType.Generic;

        // ─── Integrações sugeridas (antes do plano) ──────────────────────────
        var wanted = new List<(string Id, string Reason)>();

        if (scheduling)
        {
            switch (a["agenda"])
            {
                case "google":
                    wanted.Add((IntegrationCatalog.GoogleCalendar, "A agenda já está no Google."));
                    break;
                case "papel":
                case "nenhuma":
                    wanted.Add((IntegrationCatalog.GoogleCalendar, "Criamos uma Google Agenda para o cliente; o bot precisa de uma agenda para marcar."));
                    break;
                case "outro-sistema":
                    wanted.Add((IntegrationCatalog.GoogleCalendar, "Enquanto a integração com o sistema dele não existe, a agenda do bot fica no Google."));
                    wanted.Add((IntegrationCatalog.ClinicSystems, "Agenda direto no sistema que o cliente já usa."));
                    notes.Add("O cliente usa outro sistema de agenda: por enquanto ele teria duas agendas (Google e a dele).");
                    break;
            }

            if (a["lembretes"] == "sim")
                wanted.Add((IntegrationCatalog.Reminders, "Pediu confirmação automática na véspera."));
        }
        else if (a["lembretes"] == "sim")
        {
            notes.Add("Lembretes dependem de agenda; num bot sem agenda eles não se aplicam.");
        }

        if (a["audio"] is "muito" or "as-vezes")
            wanted.Add((IntegrationCatalog.Audio, a["audio"] == "muito" ? "Clientes mandam muito áudio." : "Clientes às vezes mandam áudio."));

        if (a["transbordo"] == "sim")
            wanted.Add((IntegrationCatalog.Handoff, "A equipe quer assumir algumas conversas."));

        if (a["numero"] == "principal")
        {
            wanted.Add((IntegrationCatalog.WhatsAppOfficial, "Precisa ser o número principal: o não oficial pode ser bloqueado pela Meta."));
            notes.Add("Número principal: recomendamos o WhatsApp oficial (sob consulta, custo por mensagem). No não oficial, um bloqueio derruba o número da empresa.");
        }

        if (a["sinal"] == "sim")
        {
            wanted.Add((IntegrationCatalog.Pix, "Cobra sinal ou pré-pagamento."));
            notes.Add("Pix ainda não existe: o bot pode informar a chave Pix no texto, sem confirmar o pagamento.");
        }

        var lgpd = a["sensiveis"] == "sim" || a["ramo"] is "saude" or "odonto";
        if (lgpd && a["sensiveis"] == "nao")
            notes.Add("Ramo de saúde: deixamos o consentimento LGPD ligado mesmo assim.");

        // ─── Plano ───────────────────────────────────────────────────────────
        var professionals = a["profissionais"] switch { "um" => 1, "ate3" => 3, _ => 4 };
        var contacts = a["volume"] switch { "ate100" => 80, "ate300" => 250, "ate800" => 650, _ => 1200 };
        var sellable = wanted.Where(w => IntegrationCatalog.Find(w.Id)?.Status == IntegrationStatus.Available).Select(w => w.Id).ToList();

        var (plan, planReason) = ChoosePlan(plans, professionals, contacts, sellable);

        var integrations = wanted.Select(w =>
        {
            var def = IntegrationCatalog.Find(w.Id)!;
            var offer = def.Status switch
            {
                IntegrationStatus.OnRequest => IntegrationOffer.OnRequest,
                IntegrationStatus.ComingSoon => IntegrationOffer.ComingSoon,
                _ when plan is null => IntegrationOffer.NotInPlan,
                _ when plan.Includes(def.Id) => IntegrationOffer.Included,
                _ when plan.AddOnPrice(def.Id) is not null => IntegrationOffer.AddOn,
                _ => IntegrationOffer.NotInPlan
            };
            return new RecommendedIntegration(def.Id, def.Name, offer, offer == IntegrationOffer.AddOn ? plan!.AddOnPrice(def.Id) : null, w.Reason);
        }).ToList();

        foreach (var outside in integrations.Where(i => i.Offer == IntegrationOffer.NotInPlan && plan is not null))
            notes.Add($"{outside.Name} não faz parte do plano {plan!.Name} nem tem preço de adicional: combine o valor à parte.");

        // ─── Custo estimado e preço ──────────────────────────────────────────
        var llmUsd = ModelCatalog.EstimateCostUsd(ModelCatalog.DefaultGroqModel, new TokenUsage(PromptTokensPerTurn, CompletionTokensPerTurn)) * TurnsPerContact;
        var audiosPerContact = a["audio"] switch { "muito" => 3, "as-vezes" => 1, _ => 0 };
        var audioUsd = audiosPerContact * ModelCatalog.EstimateTranscriptionCostUsd(AudioSecondsPerMessage);
        var usageBrl = Math.Round(contacts * (llmUsd + audioUsd) * usdToBrl, 2);

        var enabledIds = integrations.Where(i => i.Offer is IntegrationOffer.Included or IntegrationOffer.AddOn or IntegrationOffer.NotInPlan).Select(i => i.Id)
            .Concat(IntegrationCatalog.All.Where(d => d.AlwaysOn).Select(d => d.Id));
        var fixedBrl = fixedMonthlyBrl is null ? 0m : enabledIds.Sum(fixedMonthlyBrl);

        decimal? price = plan is null ? null : plan.MonthlyPriceBrl + integrations.Sum(i => i.AddOnPriceBrl ?? 0m);
        decimal? overage = plan?.OveragePriceBrl is { } overagePrice && contacts > plan.ConversationAllowance
            ? (contacts - plan.ConversationAllowance) * overagePrice
            : null;

        notes.Add($"Estimativa: {contacts} contatos/mês, {TurnsPerContact} mensagens por contato, ~{PromptTokensPerTurn + CompletionTokensPerTurn} tokens por mensagem, câmbio R$ {usdToBrl.ToString("0.00", PtBr)}.");
        if (plan?.Provisional == true)
            notes.Add($"O plano {plan.Name} ainda tem valores provisórios.");

        return new QuizRecommendation
        {
            BotType = botType,
            Plan = plan,
            PlanReason = planReason,
            Integrations = integrations,
            LgpdConsentRequired = lgpd,
            EstimatedContacts = contacts,
            EstimatedUsageCostBrl = usageBrl,
            EstimatedFixedCostBrl = fixedBrl,
            MonthlyPriceBrl = price,
            OverageRevenueBrl = overage,
            Notes = notes
        };
    }

    /// <summary>
    /// Entre os planos ativos que cobrem os profissionais e o volume, o de menor preço total
    /// (plano + adicionais). Planos que oferecem todas as integrações pedidas têm preferência.
    /// Se nenhum cobre o volume, fica o de maior franquia (com excedente).
    /// </summary>
    private static (Plan? Plan, string? Reason) ChoosePlan(IReadOnlyList<Plan> plans, int professionals, int contacts, IReadOnlyList<string> wanted)
    {
        var active = plans.Where(p => p.Active).ToList();
        if (active.Count == 0) return (null, "Nenhum plano ativo cadastrado.");

        var fitsTeam = active.Where(p => p.MaxProfessionals is null || p.MaxProfessionals >= professionals).ToList();
        if (fitsTeam.Count == 0) fitsTeam = active;

        var fitsVolume = fitsTeam.Where(p => p.ConversationAllowance >= contacts).ToList();
        if (fitsVolume.Count == 0)
        {
            var biggest = fitsTeam.OrderByDescending(p => p.ConversationAllowance).ThenBy(p => p.MonthlyPriceBrl).First();
            return (biggest, $"Nenhum plano cobre ~{contacts} contatos/mês; {biggest.Name} tem a maior franquia ({biggest.ConversationAllowance}) e o resto vira excedente.");
        }

        var ranked = fitsVolume
            .Select(p => (Plan: p,
                Covers: wanted.All(w => p.Includes(w) || p.AddOnPrice(w) is not null),
                Total: p.MonthlyPriceBrl + wanted.Sum(w => p.AddOnPrice(w) ?? 0m)))
            .OrderByDescending(x => x.Covers)
            .ThenBy(x => x.Total)
            .ThenBy(x => x.Plan.SortOrder)
            .ToList();

        var best = ranked[0];
        return (best.Plan, $"Menor preço total ({best.Total.ToString("C0", PtBr)}) entre os planos que cobrem {professionals switch { 1 => "1 profissional", 3 => "até 3 profissionais", _ => "4 ou mais profissionais" }} e ~{contacts} contatos/mês.");
    }

    /// <summary>Resumo das respostas para o meta-agente que escreve o prompt do bot (P4).</summary>
    public static string DescribeForMetaAgent(IReadOnlyDictionary<string, string> answers, QuizRecommendation recommendation, string? businessName)
    {
        var lines = new List<string>
        {
            "Contexto já levantado no quiz de venda. NÃO pergunte de novo estes pontos; use-os no prompt do bot:"
        };
        if (!string.IsNullOrWhiteSpace(businessName)) lines.Add($"- Nome do negócio: {businessName.Trim()}");
        foreach (var (questionId, optionId) in Clean(answers))
        {
            var q = Question(questionId)!;
            lines.Add($"- {q.Text} {q.Options.First(o => o.Id == optionId).Label}");
        }
        lines.Add($"- Consentimento LGPD: {(recommendation.LgpdConsentRequired ? "obrigatório (lgpdConsentRequired = true)" : "não necessário")}");
        var enabled = recommendation.Integrations.Where(i => recommendation.IntegrationsToEnable.Contains(i.Id)).Select(i => i.Name).ToList();
        if (enabled.Count > 0) lines.Add($"- Integrações contratadas: {string.Join(", ", enabled)}");
        if (recommendation.Integrations.All(i => i.Id != IntegrationCatalog.Handoff))
            lines.Add("- Não há equipe para assumir conversas: o bot não deve prometer atendimento humano.");
        lines.Add("Foque a entrevista no que falta: nome do atendente virtual, tom, serviços e preços, endereço, horários e o que mais o bot precisa saber.");
        return string.Join("\n", lines);
    }
}
