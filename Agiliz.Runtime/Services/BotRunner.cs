using Agiliz.Core.Models;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Núcleo de processamento de uma mensagem recebida.
/// Ordem: flow match → LLM. Sem side effects (não envia nada, só retorna string).
/// </summary>
public sealed class BotRunner(SessionStore sessions, ILogger<BotRunner> logger, IEnumerable<Agiliz.Core.Tools.ITool> tools, Agiliz.Core.Billing.ICostRecorder costs)
{
    public async Task<string?> ProcessAsync(
        TenantEntry tenant,
        string userPhone,
        string messageBody,
        CancellationToken ct = default)
    {
        RuntimeContext.Current.Value = new RuntimeContextData(tenant.Config.TenantId, userPhone);

        var body = messageBody.Trim();

        // ─── 2. Guard Rails & LLM ──────────────────────────────────
        var sessionState = sessions.AddAndGet(tenant.Config.TenantId, userPhone, ConversationMessage.User(body));

        // Bot pausado pelo cliente no portal: registra a mensagem e não responde.
        if (tenant.Config.Paused)
        {
            logger.LogInformation("[{Tenant}] Bot pausado pelo cliente. Mensagem de {Phone} registrada sem resposta.", tenant.Config.TenantId, userPhone);
            return null;
        }

        // Se a sessão está pausada, ignoramos a mensagem (o humano está no controle)
        if (sessionState.IsPaused)
        {
            logger.LogInformation("[{Tenant}] Sessão pausada para {Phone}. Ignorando mensagem.", tenant.Config.TenantId, userPhone);
            return null;
        }

        // ─── 1. Flow match (sem chamar o LLM) ────────────────────────────────
        var flow = tenant.Config.Flows.FirstOrDefault(f =>
            body.Contains(f.Trigger, StringComparison.OrdinalIgnoreCase));

        if (flow is not null)
        {
            logger.LogInformation("[{Tenant}] Flow match: '{Trigger}'", tenant.Config.TenantId, flow.Trigger);
            return flow.Response;
        }

        if (sessionState.TurnCount > tenant.Config.GuardRails.MaxSessionTurns || 
            sessionState.AccumulatedCostUsd > tenant.Config.GuardRails.MaxSessionSpendUsd)
        {
            logger.LogWarning("[{Tenant}] Guard Rail ativado ({Phone}). Turns: {Turns}/{MaxT}, Cost: {Cost}/{MaxC}", 
                tenant.Config.TenantId, userPhone, sessionState.TurnCount, tenant.Config.GuardRails.MaxSessionTurns, 
                sessionState.AccumulatedCostUsd, tenant.Config.GuardRails.MaxSessionSpendUsd);
            
            sessions.PauseSession(tenant.Config.TenantId, userPhone);
            return tenant.Config.GuardRails.FallbackMessage;
        }

        try
        {
            // Só as ferramentas das integrações ligadas para este cliente (painel → aba Integrações).
            var allowedTools = Agiliz.Core.Integrations.BotIntegrations.AllowedToolNames(tenant.Config);
            var activeTools = tools.Where(t => allowedTools.Contains(t.Name)).ToList();

            // Contexto dinâmico (data/hora local) vai como mensagem de sistema só para esta chamada:
            // sem ele o modelo não sabe o que é "amanhã" nem "sexta que vem".
            var llmHistory = new List<ConversationMessage>(sessionState.History.Count + 1)
            {
                new() { Role = MessageRole.System, Content = BuildTimeContext(tenant.Config, DateTimeOffset.UtcNow) }
            };
            llmHistory.AddRange(sessionState.History);

            var response = await tenant.LlmClient.CompleteAsync(llmHistory, activeTools, ct);
            var reply = response.Text;

            if (string.IsNullOrWhiteSpace(reply))
            {
                logger.LogWarning("[{Tenant}] LLM devolveu resposta vazia para {Phone}", tenant.Config.TenantId, userPhone);
                reply = "Desculpe, não consegui entender. Pode reformular sua mensagem?";
            }

            sessions.AddAssistantReply(tenant.Config.TenantId, userPhone, reply);
            logger.LogInformation("[{Tenant}] LLM respondeu ({Chars} chars)", tenant.Config.TenantId, reply.Length);

            // Faturamento: preço real do modelo usado (antes era fixo em US$ 3/15 por 1M tokens)
            var tokenCostUsd = Agiliz.Core.LLM.ModelCatalog.EstimateCostUsd(tenant.Config.Llm.Model, response.Usage);
            var totalOpCost = tokenCostUsd + response.ToolCosts.Sum(t => t.Cost);
            
            sessions.AddCost(tenant.Config.TenantId, userPhone, totalOpCost);

            RecordCost(tenant.Config.TenantId, CostType.TokensLLM, $"Inference {tenant.Config.Llm.Model} ({response.Usage.Total} tok)",
                tokenCostUsd, Agiliz.Core.Integrations.IntegrationCatalog.Ai);

            foreach(var tc in response.ToolCosts)
                RecordCost(tenant.Config.TenantId, CostType.ToolExecution, $"Tool: {tc.ToolName}", tc.Cost,
                    Agiliz.Core.Integrations.IntegrationCatalog.ForTool(tc.ToolName)?.Id);

            return reply;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Tenant}] Erro ao chamar LLM para {Phone}", tenant.Config.TenantId, userPhone);
            return "Desculpe, tive um problema técnico. Pode tentar novamente em instantes?";
        }
    }

    /// <summary>Registra um custo do tenant no billing (LLM, tools, transcrição...). Só enfileira; não espera o banco.</summary>
    public void RecordCost(string tenantId, CostType type, string description, decimal amountUsd, string? integrationId = null)
    {
        try
        {
            costs.Record(new CostEntry { TenantId = tenantId, Type = type, Description = description, AmountUsd = amountUsd, IntegrationId = integrationId });
        }
        catch (Exception ex)
        {
            // Falha de billing não pode derrubar o atendimento.
            logger.LogError(ex, "[{Tenant}] Falha ao registrar custo '{Description}'", tenantId, description);
        }
    }

    private static readonly System.Globalization.CultureInfo PtBr = new("pt-BR");

    /// <summary>Texto curto com data/hora local do negócio para ancorar o LLM no tempo.</summary>
    public static string BuildTimeContext(BotConfig config, DateTimeOffset utcNow)
    {
        var timeZoneId = string.IsNullOrWhiteSpace(config.Scheduling.TimeZone) ? "America/Sao_Paulo" : config.Scheduling.TimeZone;
        TimeSpan offset;
        try { offset = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(utcNow); }
        catch { offset = TimeSpan.FromHours(-3); }

        var local = utcNow.ToOffset(offset);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"Agora é {local.ToString("dddd, dd/MM/yyyy 'às' HH:mm", PtBr)} (fuso {timeZoneId}, UTC{sign}{offset:hh\\:mm}). " +
               $"Use esta data como referência para \"hoje\", \"amanhã\" e dias da semana. " +
               $"Ao chamar ferramentas, envie datas em ISO 8601 com o fuso (ex.: {local:yyyy-MM-dd}T14:00:00{sign}{offset:hh\\:mm}).";
    }
}
