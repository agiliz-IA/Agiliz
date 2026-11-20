using Agiliz.Core.Models;

namespace Agiliz.Core.LLM;

/// <summary>
/// Catálogo central de modelos: preço por token, modelos desligados e parâmetros especiais.
/// Preços em USD por 1 milhão de tokens. Revisar a cada trimestre — fornecedores mudam preços
/// e desligam modelos (o Groq desligou llama-3.3-70b-versatile para contas self-serve em 16/08/2026).
/// </summary>
public static class ModelCatalog
{
    /// <summary>Modelo padrão para bots novos (Groq, self-serve, suporta tool calling).</summary>
    public const string DefaultGroqModel = "openai/gpt-oss-120b";

    /// <summary>Modelo padrão quando o provedor é Claude (mais barato da linha atual).</summary>
    public const string DefaultClaudeModel = "claude-haiku-5-5";

    /// <summary>Data da última conferência dos preços abaixo.</summary>
    public const string PricesCheckedAt = "2026-11-20";

    public sealed record ModelPrice(decimal InputPerMillionUsd, decimal OutputPerMillionUsd);

    private static readonly Dictionary<string, ModelPrice> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        // Groq (console.groq.com/docs/models, conferido em 2026-11-20)
        ["openai/gpt-oss-120b"] = new(0.15m, 0.60m),
        ["openai/gpt-oss-20b"] = new(0.075m, 0.30m),
        ["qwen/qwen3.8-27b"] = new(0.80m, 4.00m),

        // Anthropic (preços da API first-party, conferidos em 2026-11-20)
        ["claude-haiku-5-5"] = new(0.10m, 0.50m),
        ["claude-haiku-4-5"] = new(1.00m, 5.00m),
        ["claude-sonnet-5-5"] = new(2.00m, 10.00m),
        ["claude-sonnet-5"] = new(2.00m, 10.00m),
        ["claude-sonnet-4-6"] = new(3.00m, 15.00m),
        ["claude-opus-5-5"] = new(4.00m, 20.00m),
    };

    /// <summary>
    /// Preço usado quando o modelo não está no catálogo. É propositalmente alto
    /// (faixa Sonnet 4.x) para não subestimar custo de cliente.
    /// </summary>
    public static readonly ModelPrice UnknownModelPrice = new(3.00m, 15.00m);

    /// <summary>Modelos desligados → substituto recomendado pelo próprio fornecedor.</summary>
    private static readonly Dictionary<string, string> Replacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["llama-3.3-70b-versatile"] = "openai/gpt-oss-120b",
        ["llama-3.1-8b-instant"] = "openai/gpt-oss-20b",
        ["qwen/qwen3-32b"] = "openai/gpt-oss-120b",
        ["qwen/qwen3.6-27b"] = "qwen/qwen3.8-27b",
        ["meta-llama/llama-4-scout-17b-16e-instruct"] = "openai/gpt-oss-120b",
        ["meta-llama/llama-4-maverick-17b-128e-instruct"] = "openai/gpt-oss-120b",
        ["moonshotai/kimi-k2-instruct-0905"] = "openai/gpt-oss-120b",
        ["claude-3-5-sonnet-20241022"] = "claude-haiku-5-5",
        ["claude-sonnet-4-20250514"] = "claude-haiku-5-5",
        ["claude-sonnet-4"] = "claude-haiku-5-5",
    };

    /// <summary>Whisper no Groq, em USD por hora de áudio. Groq cobra no mínimo 10 s por requisição.</summary>
    public const decimal WhisperTurboPerHourUsd = 0.04m;
    public const string WhisperTurboModel = "whisper-large-v3-turbo";

    /// <summary>Retorna o modelo efetivo, trocando IDs desligados pelo substituto.</summary>
    public static string Resolve(string model) =>
        !string.IsNullOrWhiteSpace(model) && Replacements.TryGetValue(model, out var replacement)
            ? replacement
            : model;

    public static bool IsDeprecated(string model) =>
        !string.IsNullOrWhiteSpace(model) && Replacements.ContainsKey(model);

    /// <summary>Modelos de raciocínio do Groq aceitam reasoning_effort/include_reasoning.</summary>
    public static bool IsGroqReasoningModel(string model) =>
        Resolve(model).StartsWith("openai/gpt-oss", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetPrice(string model, out ModelPrice price)
    {
        if (Prices.TryGetValue(Resolve(model), out var found))
        {
            price = found;
            return true;
        }

        price = UnknownModelPrice;
        return false;
    }

    public static decimal EstimateCostUsd(string model, TokenUsage usage)
    {
        TryGetPrice(model, out var price);
        return (usage.Prompt * price.InputPerMillionUsd + usage.Completion * price.OutputPerMillionUsd) / 1_000_000m;
    }

    public static decimal EstimateTranscriptionCostUsd(double seconds)
    {
        var billedSeconds = Math.Max(10d, seconds);
        return (decimal)billedSeconds / 3600m * WhisperTurboPerHourUsd;
    }

    public static string DefaultModelFor(LlmProvider provider) =>
        provider == LlmProvider.Claude ? DefaultClaudeModel : DefaultGroqModel;
}
