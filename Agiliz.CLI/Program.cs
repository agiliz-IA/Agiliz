using Agiliz.CLI.Commands;
using Agiliz.CLI.UI;
using Spectre.Console;

// ─── Configs dir: sempre na raiz da solução ───────────────────────────────────
var configsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs");
configsDir = Path.GetFullPath(configsDir);

ConsoleRenderer.Header();

var command = args.ElementAtOrDefault(0)?.ToLower();
var tenantArg = args.ElementAtOrDefault(1);

await (command switch
{
    "create" => CreateCommand.RunAsync(configsDir, tenantArg),
    "edit"   => EditCommand.RunAsync(configsDir, tenantArg),
    "list"   => ListCommand.RunAsync(configsDir),
    "test"   => TestCommand.RunAsync(configsDir, tenantArg),
    "db"     => tenantArg == "migrate" ? DbMigrateCommand.RunAsync() : ShowHelp(),
    _        => ShowHelp()
});

static Task ShowHelp()
{
    AnsiConsole.MarkupLine("""
        [bold]Uso:[/]
          agiliz [green]create[/] [grey]<tenant-id>[/]   Cria um novo bot
          agiliz [green]edit[/]   [grey]<tenant-id>[/]   Edita um bot existente
          agiliz [green]list[/]              Lista todos os bots
          agiliz [green]test[/]   [grey]<tenant-id>[/]   Testa um bot no terminal
          agiliz [green]db migrate[/]        Aplica migrações pendentes no BD
        """);
    return Task.CompletedTask;
}
