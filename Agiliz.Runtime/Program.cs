using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Runtime.Endpoints;
using Agiliz.Runtime.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Configs dir: raiz da solução /configs ────────────────────────────────────
var configsDir = builder.Configuration["ConfigsDir"]
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"));

// ─── Serviços ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<TenantRegistry>>();
    var llmFactory = sp.GetService<Func<BotConfig, ILlmClient>>(); // null in production → uses default
    return new TenantRegistry(configsDir, logger, llmFactory);
});

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<BotRunner>();
builder.Services.AddSingleton<IMessageProvider, EvolutionClient>();
builder.Services.AddHostedService<SessionPurgeService>();

// ─── Build ────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Valida que ao menos um tenant foi carregado antes de aceitar tráfego
var registry = app.Services.GetRequiredService<TenantRegistry>();
if (registry.Count == 0)
    app.Logger.LogWarning("Runtime iniciado sem nenhum bot ativo. Adicione configs e reinicie.");

// ─── Endpoints ────────────────────────────────────────────────────────────────
WhatsAppWebhook.Map(app);

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    bots = registry.Count,
    time = DateTimeOffset.UtcNow
}));

app.Run();

// Exposes Program to WebApplicationFactory in tests
namespace Agiliz.Runtime { public partial class Program { } }
