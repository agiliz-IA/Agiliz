using Agiliz.Core.Models;

namespace Agiliz.Core.LLM;

public static class LlmClientFactory
{
    /// <summary>
    /// Cria o ILlmClient adequado com base no BotConfig.
    /// API keys são lidas das variáveis de ambiente:
    ///   - GROQ_API_KEY
    ///   - ANTHROPIC_API_KEY
    /// </summary>
    public static ILlmClient Create(BotConfig config, HttpClient? http = null)
    {
        http ??= new HttpClient();

        return config.Llm.Provider switch
        {
            LlmProvider.Groq => CreateGroq(config, http),
            LlmProvider.Claude => CreateClaude(config, http),
            _ => throw new NotSupportedException($"Provider desconhecido: {config.Llm.Provider}")
        };
    }

    private static GroqClient CreateGroq(BotConfig config, HttpClient http)
    {
        var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY")
                     ?? throw new InvalidOperationException("GROQ_API_KEY não encontrada nas variáveis de ambiente.");

        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        return new GroqClient(http, config.SystemPrompt, config.Llm);
    }

    private static ClaudeClient CreateClaude(BotConfig config, HttpClient http)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                     ?? throw new InvalidOperationException("ANTHROPIC_API_KEY não encontrada nas variáveis de ambiente.");

        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        return new ClaudeClient(http, config.SystemPrompt, config.Llm);
    }
}
