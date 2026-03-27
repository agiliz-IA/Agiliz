using Agiliz.CLI.UI;
using Agiliz.Core.Config;
using Agiliz.Core.Models;
using Spectre.Console;

namespace Agiliz.CLI.Commands;

public static class ListCommand
{
    public static Task RunAsync(string configsDir)
    {
        var tenants = BotConfigLoader.ListTenants(configsDir).ToList();

        if (tenants.Count == 0)
        {
            ConsoleRenderer.Info("Nenhum bot configurado. Use 'agiliz create <id>' para começar.");
            return Task.CompletedTask;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Tenant ID")
            .AddColumn("Número Twilio")
            .AddColumn("Provider")
            .AddColumn("Flows");

        foreach (var id in tenants)
        {
            try
            {
                var config = BotConfigLoader.Load(configsDir, id);
                table.AddRow(
                    $"[bold]{id}[/]",
                    config.WhatsAppNumber,
                    config.Llm.Provider == LlmProvider.Groq ? "[yellow]Groq[/]" : "[mediumpurple1]Claude[/]",
                    config.Flows.Count.ToString()
                );
            }
            catch
            {
                table.AddRow(id, "[red]erro ao ler config[/]", "-", "-");
            }
        }

        AnsiConsole.Write(table);
        return Task.CompletedTask;
    }
}
