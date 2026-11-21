using Microsoft.EntityFrameworkCore;
using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Runtime.Endpoints;
using Agiliz.Runtime.Services;
using Agiliz.Core.Tools;

var builder = WebApplication.CreateBuilder(args);

// ─── Configs dir: raiz da solução /configs (bots em configs/bots) ─────────────
var configsDir = BotConfigLoader.ResolveConfigsDir(builder.Configuration);
var movedBots = BotConfigLoader.MigrateLegacyLayout(configsDir);

// Enums como texto na API (ex.: "TokensLLM"), igual ao que o painel envia e lê.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// ─── Serviços ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<TenantRegistry>>();
    var llmFactory = sp.GetService<Func<BotConfig, ILlmClient>>(); // null in production → uses default
    return new TenantRegistry(configsDir, logger, llmFactory);
});

builder.Services.AddSingleton<SessionStore>();

// Billing no Postgres (antes: configs/billing.json). Gravação em segundo plano.
builder.Services.AddSingleton<BillingWriter>();
builder.Services.AddSingleton<Agiliz.Core.Billing.ICostRecorder>(sp => sp.GetRequiredService<BillingWriter>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BillingWriter>());
builder.Services.AddSingleton<BotRunner>();
builder.Services.AddSingleton<IMessageProvider, EvolutionClient>();

// Transcrição de áudio (notas de voz) via Whisper no Groq. Desliga com Audio:TranscriptionEnabled=false.
builder.Services.AddSingleton<Agiliz.Core.Media.IAudioTranscriber>(_ =>
    new Agiliz.Core.Media.GroqWhisperTranscriber(
        enabled: builder.Configuration.GetValue("Audio:TranscriptionEnabled", true)));
builder.Services.AddHostedService<SessionPurgeService>();

// Tools
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<ITool, SendEmailTool>();

// Database & Anti-Fraud
var connString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=127.0.0.1;Port=5433;Database=evolution;Username=evolution;Password=evolution";

builder.Services.AddDbContext<Agiliz.Runtime.Data.AgilizDbContext>(options =>
    options.UseNpgsql(connString));

builder.Services.AddScoped<AntiFraudService>();
builder.Services.AddSingleton<GoogleCalendarService>();
builder.Services.AddSingleton<ITool, Agiliz.Runtime.Tools.VerificarAgendaTool>();
builder.Services.AddSingleton<ITool, Agiliz.Runtime.Tools.MarcarAgendaTool>();
builder.Services.AddSingleton<ITool, Agiliz.Runtime.Tools.ConfirmarAgendamentoTool>();
builder.Services.AddSingleton<ITool, Agiliz.Runtime.Tools.CancelarAgendamentoTool>();
builder.Services.AddSingleton<ITool, Agiliz.Runtime.Tools.ReagendarAgendamentoTool>();
builder.Services.AddSingleton<ITool, Agiliz.Runtime.Tools.PausarBotTool>();
builder.Services.AddHostedService<ReminderBackgroundService>();
builder.Services.AddSingleton<IntegrationTester>();

// ─── Build ────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (movedBots.Count > 0)
    app.Logger.LogInformation("Bots movidos para configs/bots/: {Bots}", string.Join(", ", movedBots));

// Valida que ao menos um tenant foi carregado antes de aceitar tráfego
var registry = app.Services.GetRequiredService<TenantRegistry>();
if (registry.Count == 0)
    app.Logger.LogWarning("Runtime iniciado sem nenhum bot ativo. Salve um bot no painel (ele chama POST /api/admin/reload).");

if (string.IsNullOrWhiteSpace(app.Configuration["Runtime:AdminToken"]))
    app.Logger.LogWarning(app.Environment.IsDevelopment()
        ? "Runtime:AdminToken vazio: rotas do painel abertas (só em Development)."
        : "Runtime:AdminToken vazio: rotas do painel (/api/admin, /api/handoff) respondem 503. Defina RUNTIME_ADMIN_TOKEN.");

if (app.Environment.IsProduction())
{
    var evolutionUrl = app.Configuration["Evolution:ApiUrl"];
    var evolutionToken = app.Configuration["Evolution:ApiToken"];
    if (string.IsNullOrWhiteSpace(evolutionUrl) ||
        string.IsNullOrWhiteSpace(evolutionToken) ||
        evolutionToken.StartsWith("change-me", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Evolution:ApiUrl e Evolution:ApiToken devem estar configurados em producao.");
}

if (app.Configuration.GetValue("Database:AutoMigrate", false))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<Agiliz.Runtime.Data.AgilizDbContext>();
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Migrations do Agiliz.Runtime aplicadas.");
}

// ─── Endpoints ────────────────────────────────────────────────────────────────
WhatsAppWebhook.Map(app);
AdminEndpoints.Map(app);

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    bots = registry.Count,
    botsLoadedAt = registry.LoadedAt,
    time = DateTimeOffset.UtcNow
}));

app.Run();

// Exposes Program to WebApplicationFactory in tests
namespace Agiliz.Runtime { public partial class Program { } }
