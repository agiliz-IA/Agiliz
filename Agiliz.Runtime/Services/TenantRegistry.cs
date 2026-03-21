using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Carregado uma única vez na startup.
/// Mapeia número Twilio → (BotConfig, ILlmClient) para lookup O(1) por mensagem recebida.
/// </summary>
public sealed class TenantRegistry
{
    private readonly Dictionary<string, TenantEntry> _byNumber;

    public TenantRegistry(string configsDir, ILogger<TenantRegistry> logger)
    {
        _byNumber = new Dictionary<string, TenantEntry>(StringComparer.OrdinalIgnoreCase);

        var tenants = BotConfigLoader.ListTenants(configsDir).ToList();

        if (tenants.Count == 0)
            logger.LogWarning("Nenhum config encontrado em '{Dir}'. Nenhum bot ativo.", configsDir);

        foreach (var id in tenants)
        {
            try
            {
                var config = BotConfigLoader.Load(configsDir, id);
                var llm = LlmClientFactory.Create(config, new HttpClient());
                _byNumber[config.TwilioNumber] = new TenantEntry(config, llm);
                logger.LogInformation("Bot carregado: {Tenant} → {Number}", id, config.TwilioNumber);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao carregar config do tenant '{Id}'. Ignorando.", id);
            }
        }
    }

    /// <summary>
    /// Resolve o tenant pelo número Twilio de destino da mensagem (campo 'To' do webhook).
    /// </summary>
    public TenantEntry? Resolve(string twilioNumber) =>
        _byNumber.TryGetValue(twilioNumber, out var entry) ? entry : null;

    public int Count => _byNumber.Count;
}

public sealed record TenantEntry(BotConfig Config, ILlmClient LlmClient);
