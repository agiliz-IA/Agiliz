using Agiliz.CLI.UI;
using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;
using Spectre.Console;

namespace Agiliz.CLI.Commands;

/// <summary>
/// Simula o runtime do bot diretamente no terminal.
/// Perfeito para validar o systemPrompt e os flows antes de subir o webhook.
/// </summary>
public static class TestCommand
{
    public static async Task RunAsync(string configsDir, string? tenantArg)
    {
        var tenantId = tenantArg ?? AnsiConsole.Ask<string>("[bold]ID do bot a testar:[/]");

        BotConfig config;
        try
        {
            config = BotConfigLoader.Load(configsDir, tenantId);
        }
        catch (FileNotFoundException)
        {
            ConsoleRenderer.Error($"Config '{tenantId}' não encontrado.");
            return;
        }

        AnsiConsole.MarkupLine($"\n[bold]Testando bot:[/] [mediumpurple1]{tenantId}[/]");
        AnsiConsole.MarkupLine($"[grey]Provider: {config.Llm.Provider} | Flows carregados: {config.Flows.Count}[/]");
        AnsiConsole.MarkupLine("[grey]Digite 'sair' para encerrar. Flows são resolvidos antes do LLM.[/]\n");

        var llm = LlmClientFactory.Create(config);
        var history = new List<ConversationMessage>();
        const int maxHistory = 5; // espelha o comportamento real do runtime

        while (true)
        {
            var input = ConsoleRenderer.AskUser("Usuário");

            if (input.Equals("sair", StringComparison.OrdinalIgnoreCase))
                break;

            // ─── Verifica flows antes de chamar o LLM ──────────────────────────
            var flowMatch = config.Flows.FirstOrDefault(f =>
                input.ToLower().Contains(f.Trigger));

            if (flowMatch is not null)
            {
                AnsiConsole.MarkupLine($"[grey]  ↳ flow match: '{flowMatch.Trigger}'[/]");
                ConsoleRenderer.AgentSay(flowMatch.Response);
                continue;
            }

            // ─── Chama o LLM com histórico limitado ────────────────────────────
            history.Add(ConversationMessage.User(input));

            if (history.Count > maxHistory * 2)
                history.RemoveRange(0, 2); // remove o par mais antigo

            ConsoleRenderer.AgentThinking();

            try
            {
                var response = await llm.CompleteAsync(history);
                ConsoleRenderer.ClearLastLine();
                history.Add(ConversationMessage.Assistant(response));
                ConsoleRenderer.AgentSay(response);
            }
            catch (Exception ex)
            {
                ConsoleRenderer.ClearLastLine();
                ConsoleRenderer.Error($"Erro ao chamar o LLM: {ex.Message}");
            }
        }

        AnsiConsole.MarkupLine("\n[grey]Sessão de teste encerrada.[/]");
    }
}
