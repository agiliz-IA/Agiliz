using System.Text.Json;
using Agiliz.CLI.Agent;
using Agiliz.CLI.UI;
using Agiliz.Core.Config;
using Agiliz.Core.Models;
using Spectre.Console;

namespace Agiliz.CLI.Commands;

public static class CreateCommand
{
    public static async Task RunAsync(string configsDir, string? tenantArg)
    {
        // ─── 1. TenantId ──────────────────────────────────────────────────────
        var tenantId = tenantArg
            ?? AnsiConsole.Ask<string>("[bold]ID do cliente[/] (ex: pizzaria-roma):");

        tenantId = tenantId.Trim().ToLower().Replace(" ", "-");

        var existing = BotConfigLoader.ListTenants(configsDir);
        if (existing.Contains(tenantId))
        {
            if (!ConsoleRenderer.Confirm($"[yellow]'{tenantId}' já existe. Sobrescrever?[/]"))
                return;
        }

        // ─── 2. Número Twilio ──────────────────────────────────────────────────
        ConsoleRenderer.Info("\nFormato esperado: whatsapp:+5521999999999");
        var twilioNumber = AnsiConsole.Ask<string>("[bold]Número Twilio do cliente[/]:");

        if (!twilioNumber.StartsWith("whatsapp:+"))
            twilioNumber = $"whatsapp:{twilioNumber}";

        // ─── 3. Provider ───────────────────────────────────────────────────────
        var provider = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Provedor LLM para este bot:[/]")
                .AddChoices("Groq (MVP / testes)", "Claude (produção)"));

        var llmProvider = provider.StartsWith("Groq") ? LlmProvider.Groq : LlmProvider.Claude;

        var model = llmProvider == LlmProvider.Groq
            ? "llama-3.3-70b-versatile"
            : "claude-sonnet-4-20250514";

        // ─── 4. Entrevista com o meta-agente ──────────────────────────────────
        AnsiConsole.WriteLine();
        ConsoleRenderer.AgentSay("Olá! Vou te ajudar a configurar o bot. Me conta: qual é o negócio e o que ele faz?");

        var session = new MetaAgentSession();
        string? generatedReply = null;

        while (true)
        {
            var userInput = ConsoleRenderer.AskUser();
            if (string.IsNullOrWhiteSpace(userInput)) continue;

            ConsoleRenderer.AgentThinking();
            var reply = await session.SendAsync(userInput);
            ConsoleRenderer.ClearLastLine();

            if (MetaAgentSession.HasConfigBlock(reply))
            {
                generatedReply = reply;

                // Imprime só o texto antes do bloco JSON
                var textPart = reply[..reply.IndexOf("===JSON_START===", StringComparison.Ordinal)].Trim();
                if (!string.IsNullOrEmpty(textPart))
                    ConsoleRenderer.AgentSay(textPart);

                break;
            }

            ConsoleRenderer.AgentSay(reply);
        }

        // ─── 5. Extrair e exibir config ────────────────────────────────────────
        var (systemPrompt, flows) = MetaAgentSession.ExtractConfig(generatedReply!);

        var config = new BotConfig
        {
            TenantId = tenantId,
            TwilioNumber = twilioNumber,
            SystemPrompt = systemPrompt,
            Flows = flows,
            Llm = new LlmSettings
            {
                Provider = llmProvider,
                Model = model,
                MaxTokens = 300
            }
        };

        var preview = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        AnsiConsole.WriteLine();
        ConsoleRenderer.PrintConfig(preview);

        // ─── 6. Confirmação e salvamento ──────────────────────────────────────
        if (!ConsoleRenderer.Confirm("\nSalvar este config?"))
        {
            ConsoleRenderer.Info("Config descartado.");
            return;
        }

        BotConfigLoader.Save(configsDir, config);
        ConsoleRenderer.Success($"Bot '{tenantId}' criado em configs/{tenantId}.json");
        ConsoleRenderer.Info("Use 'agiliz test " + tenantId + "' para testar antes de subir o Runtime.");
    }
}
