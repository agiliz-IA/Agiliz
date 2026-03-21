using Agiliz.Core.Config;
using Agiliz.Core.Twilio;
using Agiliz.Runtime.Endpoints;
using Agiliz.Runtime.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Configs dir: raiz da solução /configs ────────────────────────────────────
var configsDir = builder.Configuration["ConfigsDir"]
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"));

// ─── Serviços ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(_ =>
{
    var logger = LoggerFactory.Create(b => b.AddConsole())
                              .CreateLogger<TenantRegistry>();
    return new TenantRegistry(configsDir, logger);
});

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<BotRunner>();
builder.Services.AddSingleton<ITwilioSender, TwilioSender>();
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
