using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Mapeia número WhatsApp → (BotConfig, ILlmClient) para lookup O(1) por mensagem recebida.
/// Carrega configs/bots na startup e de novo a cada <see cref="Reload"/> (POST /api/admin/reload),
/// sem reiniciar o processo. A troca é atômica: quem está atendendo uma mensagem continua com o
/// snapshot antigo até terminar.
/// </summary>
public sealed class TenantRegistry
{
    private readonly string _configsDir;
    private readonly ILogger<TenantRegistry> _logger;
    private readonly Func<BotConfig, ILlmClient> _llmFactory;
    private readonly Lock _reloadLock = new();
    private volatile Dictionary<string, TenantEntry> _byNumber = new(StringComparer.OrdinalIgnoreCase);

    public TenantRegistry(
        string configsDir,
        ILogger<TenantRegistry> logger,
        Func<BotConfig, ILlmClient>? llmFactory = null)
    {
        _configsDir = configsDir;
        _logger = logger;
        _llmFactory = llmFactory ?? (config => LlmClientFactory.Create(config, new HttpClient()));
        Reload();
    }

    public DateTimeOffset LoadedAt { get; private set; }

    /// <summary>Relê todos os configs. Um config com erro é pulado; os demais continuam ativos.</summary>
    public TenantReloadResult Reload()
    {
        lock (_reloadLock)
        {
            var next = new Dictionary<string, TenantEntry>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<TenantLoadError>();
            var tenants = BotConfigLoader.ListTenants(_configsDir).ToList();

            if (tenants.Count == 0)
                _logger.LogWarning("Nenhum config encontrado em '{Dir}'. Nenhum bot ativo.", BotConfigLoader.BotsDir(_configsDir));

            foreach (var id in tenants)
            {
                try
                {
                    var config = BotConfigLoader.Load(_configsDir, id);
                    if (string.IsNullOrWhiteSpace(config.WhatsAppNumber))
                    {
                        Fail(id, "Config sem WhatsAppNumber.");
                        continue;
                    }
                    if (next.TryGetValue(config.WhatsAppNumber, out var other))
                    {
                        Fail(id, $"Número {config.WhatsAppNumber} já usado pelo bot '{other.Config.TenantId}'.");
                        continue;
                    }

                    next[config.WhatsAppNumber] = new TenantEntry(config, _llmFactory(config));
                    _logger.LogInformation("Bot carregado: {Tenant} → {Number}", id, config.WhatsAppNumber);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao carregar config do tenant '{Id}'. Ignorando.", id);
                    errors.Add(new TenantLoadError(id, ex.Message));
                }
            }

            var removed = _byNumber.Values.Select(e => e.Config.TenantId)
                .Except(next.Values.Select(e => e.Config.TenantId), StringComparer.OrdinalIgnoreCase)
                .ToList();

            _byNumber = next;
            LoadedAt = DateTimeOffset.UtcNow;

            return new TenantReloadResult(
                next.Values.Select(e => e.Config.TenantId).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                removed,
                errors,
                LoadedAt);

            void Fail(string id, string reason)
            {
                _logger.LogError("Config do tenant '{Id}' ignorado: {Reason}", id, reason);
                errors.Add(new TenantLoadError(id, reason));
            }
        }
    }

    /// <summary>
    /// Resolve o tenant pelo número WhatsApp de destino da mensagem.
    /// </summary>
    public TenantEntry? Resolve(string whatsappNumber) =>
        _byNumber.TryGetValue(whatsappNumber, out var entry) ? entry : null;

    /// <summary>
    /// Resolve o tenant pelo TenantId.
    /// </summary>
    public TenantEntry? Get(string tenantId) =>
        _byNumber.Values.FirstOrDefault(e => string.Equals(e.Config.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

    public int Count => _byNumber.Count;
}

public sealed record TenantEntry(BotConfig Config, ILlmClient LlmClient);

public sealed record TenantLoadError(string TenantId, string Error);

public sealed record TenantReloadResult(
    IReadOnlyList<string> Loaded,
    IReadOnlyList<string> Removed,
    IReadOnlyList<TenantLoadError> Errors,
    DateTimeOffset LoadedAt);
