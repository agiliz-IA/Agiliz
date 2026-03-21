using Agiliz.Core.Models;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Núcleo de processamento de uma mensagem recebida.
/// Ordem: flow match → LLM. Sem side effects (não envia nada, só retorna string).
/// </summary>
public sealed class BotRunner(SessionStore sessions, ILogger<BotRunner> logger)
{
    public async Task<string> ProcessAsync(
        TenantEntry tenant,
        string userPhone,
        string messageBody,
        CancellationToken ct = default)
    {
        var body = messageBody.Trim();

        // ─── 1. Flow match (sem chamar o LLM) ────────────────────────────────
        var flow = tenant.Config.Flows.FirstOrDefault(f =>
            body.Contains(f.Trigger, StringComparison.OrdinalIgnoreCase));

        if (flow is not null)
        {
            logger.LogInformation("[{Tenant}] Flow match: '{Trigger}'", tenant.Config.TenantId, flow.Trigger);
            return flow.Response;
        }

        // ─── 2. LLM com histórico da sessão ──────────────────────────────────
        var history = sessions.AddAndGet(userPhone, ConversationMessage.User(body));

        try
        {
            var reply = await tenant.LlmClient.CompleteAsync(history, ct);
            sessions.AddAssistantReply(userPhone, reply);
            logger.LogInformation("[{Tenant}] LLM respondeu ({Chars} chars)", tenant.Config.TenantId, reply.Length);
            return reply;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Tenant}] Erro ao chamar LLM para {Phone}", tenant.Config.TenantId, userPhone);
            return "Desculpe, tive um problema técnico. Pode tentar novamente em instantes?";
        }
    }
}
