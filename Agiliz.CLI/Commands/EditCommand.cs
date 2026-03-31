using System.Text.Json;
using Agiliz.CLI.Agent;
using Agiliz.CLI.UI;
using Agiliz.Core.Config;
using Agiliz.Core.Models;
using Spectre.Console;

namespace Agiliz.CLI.Commands;

public static class EditCommand
{
    public static async Task RunAsync(string configsDir, string? tenantArg)
    {
        var tenantId = tenantArg ?? AnsiConsole.Ask<string>("[bold]ID do cliente a editar:[/]");

        BotConfig existing;
        try
        {
            existing = BotConfigLoader.Load(configsDir, tenantId);
        }
        catch (FileNotFoundException)
        {
            ConsoleRenderer.Error($"Config '{tenantId}' não encontrado. Use 'agiliz create {tenantId}' para criar.");
            return;
        }

        AnsiConsole.WriteLine();
        ConsoleRenderer.AgentSay($"Config atual do '{tenantId}' carregado. O que você quer alterar? " +
                                  "(ex: \"mude o tom para mais formal\", \"adicione uma resposta sobre entrega\")");

        var session = new MetaAgentSession();

        // Injeta o config atual como contexto inicial
        var contextMessage = $"""
            Este é o config atual do bot '{tenantId}'. Use-o como base para as alterações pedidas:

            System prompt atual:
            {existing.SystemPrompt}

            Flows atuais:
            {JsonSerializer.Serialize(existing.Flows, new JsonSerializerOptions { WriteIndented = true })}
            """;
        await session.SendAsync(contextMessage);

        string? generatedReply = null;

        while (true)
        {
            var userInput = ConsoleRenderer.AskUser();
            if (string.IsNullOrWhiteSpace(userInput)) continue;

            ConsoleRenderer.AgentThinking();
            var reply = await session.SendAsync(userInput);
            ConsoleRenderer.ClearLastLine();

            if (MetaAgentSession.HasConfigBlock(reply.Text))
            {
                generatedReply = reply.Text;
                var textPart = reply.Text[..reply.Text.IndexOf("===JSON_START===", StringComparison.Ordinal)].Trim();
                if (!string.IsNullOrEmpty(textPart))
                    ConsoleRenderer.AgentSay(textPart);
                break;
            }

            ConsoleRenderer.AgentSay(reply.Text);
        }

        var (systemPrompt, flows) = MetaAgentSession.ExtractConfig(generatedReply!);

        var updated = new BotConfig
        {
            TenantId = existing.TenantId,
            WhatsAppNumber = existing.WhatsAppNumber,
            SystemPrompt = systemPrompt,
            Flows = flows,
            Llm = existing.Llm
        };

        var preview = JsonSerializer.Serialize(updated, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        AnsiConsole.WriteLine();
        ConsoleRenderer.PrintConfig(preview);

        if (!ConsoleRenderer.Confirm("\nSalvar alterações?"))
        {
            ConsoleRenderer.Info("Edição descartada. Config original mantido.");
            return;
        }

        BotConfigLoader.Save(configsDir, updated);
        ConsoleRenderer.Success($"Bot '{tenantId}' atualizado.");
    }
}
