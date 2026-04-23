using Agiliz.Runtime.Data;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace Agiliz.CLI.Commands;

public static class DbMigrateCommand
{
    public static async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[bold]Iniciando migrações do banco de dados (EF Core)...[/]");
        
        try
        {
            var connString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") 
                ?? "Host=127.0.0.1;Port=5433;Database=evolution;Username=evolution;Password=evolution";

            var optionsBuilder = new DbContextOptionsBuilder<AgilizDbContext>();
            optionsBuilder.UseNpgsql(connString);

            using var db = new AgilizDbContext(optionsBuilder.Options);

            AnsiConsole.MarkupLine($"[grey]String de conexão: {connString}[/]");
            await db.Database.MigrateAsync();

            AnsiConsole.MarkupLine("[bold green]Migrações aplicadas com sucesso![/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Erro ao aplicar migrações: {ex.Message}[/]");
            AnsiConsole.WriteException(ex);
        }
    }
}
