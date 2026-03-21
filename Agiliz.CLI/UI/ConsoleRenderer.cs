using Spectre.Console;

namespace Agiliz.CLI.UI;

public static class ConsoleRenderer
{
    public static void Header()
    {
        AnsiConsole.Write(new FigletText("Agiliz").Color(Color.MediumPurple1));
        AnsiConsole.MarkupLine("[grey]Meta-agente de criação de bots WhatsApp[/]\n");
    }

    public static void AgentSay(string message)
    {
        AnsiConsole.MarkupLine($"[mediumpurple1]●[/] [bold]{Markup.Escape(message)}[/]");
    }

    public static void AgentThinking()
    {
        AnsiConsole.Markup("[grey]  pensando...[/]");
    }

    public static void ClearLastLine()
    {
        Console.SetCursorPosition(0, Console.CursorTop);
        Console.Write(new string(' ', Console.WindowWidth));
        Console.SetCursorPosition(0, Console.CursorTop);
    }

    public static string AskUser(string prompt = "Você")
    {
        return AnsiConsole.Ask<string>($"[green]{prompt}[/]>");
    }

    public static bool Confirm(string question)
    {
        return AnsiConsole.Confirm(question);
    }

    public static void Success(string message)
    {
        AnsiConsole.MarkupLine($"\n[green]✓[/] {Markup.Escape(message)}");
    }

    public static void Error(string message)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(message)}");
    }

    public static void Info(string message)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    public static void PrintConfig(string json)
    {
        var panel = new Panel(new Text(json))
        {
            Header = new PanelHeader("Config gerado"),
            Border = BoxBorder.Rounded
        };
        AnsiConsole.Write(panel);
    }
}
