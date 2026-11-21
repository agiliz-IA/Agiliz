using System.Text.Json;
using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;
using Agiliz.Wizard.Services;

var builder = WebApplication.CreateBuilder(args);

var configsDir = BotConfigLoader.ResolveConfigsDir(builder.Configuration);
var movedBots = BotConfigLoader.MigrateLegacyLayout(configsDir);

builder.Services.AddSingleton<WizardSessionStore>();
builder.Services.AddHttpClient();
RuntimeApiClient.Register(builder.Services);
builder.Services.AddSingleton<BotIntegrationService>();
builder.Services.AddSingleton<CostSettings>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(options => { options.DetailedErrors = true; });

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAntiforgery();

var app = builder.Build();

if (movedBots.Count > 0)
    app.Logger.LogInformation("Bots movidos para configs/bots/: {Bots}", string.Join(", ", movedBots));

if (app.Environment.IsDevelopment())
    app.UseCors();

app.Use(async (context, next) =>
{
    var expectedPassword = app.Configuration["Wizard:AdminPassword"];
    if (string.IsNullOrWhiteSpace(expectedPassword))
    {
        await next();
        return;
    }

    var expectedUser = app.Configuration["Wizard:AdminUser"] ?? "admin";
    var auth = context.Request.Headers.Authorization.ToString();
    if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth["Basic ".Length..]));
            var parts = raw.Split(':', 2);
            if (parts.Length == 2 && parts[0] == expectedUser && parts[1] == expectedPassword)
            {
                await next();
                return;
            }
        }
        catch { }
    }

    context.Response.Headers.WWWAuthenticate = "Basic realm=\"Agiliz Wizard\"";
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
});
app.UseStaticFiles();
app.UseAntiforgery();

// ─── Session purge timer ──────────────────────────────────────────────────────
var store = app.Services.GetRequiredService<WizardSessionStore>();
using var purgeTimer = new Timer(_ => store.PurgeExpired(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

// ─── JSON options ─────────────────────────────────────────────────────────────
var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
};

// ═══════════════════════════════════════════════════════════════════════════════
// BOTS
// ═══════════════════════════════════════════════════════════════════════════════

// GET /api/bots — lista todos os bots
app.MapGet("/api/bots", () =>
{
    var tenants = BotConfigLoader.ListTenants(configsDir);
    var list = tenants.Select(id =>
    {
        try
        {
            var c = BotConfigLoader.Load(configsDir, id);
            return new { c.TenantId, c.WhatsAppNumber, Provider = c.Llm.Provider.ToString(), FlowCount = c.Flows.Count, c.Llm.Model };
        }
        catch
        {
            return new { TenantId = id, WhatsAppNumber = "erro", Provider = "-", FlowCount = 0, Model = "-" };
        }
    });
    return Results.Json(list, jsonOpts);
});

// GET /api/bots/{id} — retorna config completo (para edit)
app.MapGet("/api/bots/{id}", (string id) =>
{
    if (!BotConfigLoader.Exists(configsDir, id)) return Results.NotFound();
    var config = BotConfigLoader.Load(configsDir, id);
    return Results.Json(config, jsonOpts);
});

// DELETE /api/bots/{id} — apaga o config e avisa o Runtime para parar de atender o número
app.MapDelete("/api/bots/{id}", async (string id, RuntimeApiClient runtime) =>
{
    if (!BotConfigLoader.Delete(configsDir, id)) return Results.NotFound();
    var reload = await runtime.ReloadAsync();
    if (!reload.Ok)
        app.Logger.LogWarning("Bot {Tenant} apagado, mas o Runtime não recarregou: {Error}", id, reload.Error);
    return Results.NoContent();
});

// POST /api/bots — salva bot a partir dos dados do wizard
app.MapPost("/api/bots", async (HttpRequest req, RuntimeApiClient runtime) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;

    var tenantId = root.GetProperty("tenantId").GetString()!.Trim().ToLower().Replace(" ", "-");
    if (!BotConfigLoader.IsValidTenantId(tenantId))
        return Results.BadRequest(new { error = "tenantId inválido: use letras, números, '-' ou '_' (até 64)." });
    var whatsappNumber = root.GetProperty("whatsappNumber").GetString()!;
    // Remove any non-digits
    whatsappNumber = System.Text.RegularExpressions.Regex.Replace(whatsappNumber, @"\D", "");

    var providerStr = root.GetProperty("provider").GetString()!;
    var provider = providerStr == "Claude" ? LlmProvider.Claude : LlmProvider.Groq;
    var maxTokens = root.TryGetProperty("maxTokens", out var mt) ? mt.GetInt32() : new LlmSettings().MaxTokens;

    var systemPrompt = root.GetProperty("systemPrompt").GetString()!;
    var botType = root.TryGetProperty("type", out var typeEl)
        ? (typeEl.ValueKind == JsonValueKind.String
            ? Enum.Parse<BotType>(typeEl.GetString()!, true)
            : (BotType)typeEl.GetInt32())
        : BotType.Generic;

    if (botType == BotType.Scheduling && provider == LlmProvider.Claude)
        provider = LlmProvider.Groq;
    var model = ModelCatalog.DefaultModelFor(provider);

    var flows = new List<FlowEntry>();
    if (root.TryGetProperty("flows", out var flowsEl))
        foreach (var f in flowsEl.EnumerateArray())
            flows.Add(new FlowEntry
            {
                Trigger = f.GetProperty("trigger").GetString() ?? "",
                Response = f.GetProperty("response").GetString() ?? ""
            });

    var config = new BotConfig
    {
        TenantId = tenantId,
        Type = botType,
        WhatsAppNumber = whatsappNumber,
        SystemPrompt = systemPrompt,
        Flows = flows,
        GoogleCalendarId = root.TryGetProperty("googleCalendarId", out var cal) ? cal.GetString() : null,
        LgpdConsentRequired = root.TryGetProperty("lgpdConsentRequired", out var lgpd) && lgpd.GetBoolean(),
        PaymentMethods = root.TryGetProperty("paymentMethods", out var payments)
            ? payments.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
            : [],
        Scheduling = root.TryGetProperty("scheduling", out var scheduling)
            ? scheduling.Deserialize<SchedulingSettings>(jsonOpts) ?? new SchedulingSettings()
            : new SchedulingSettings(),
        Llm = new LlmSettings { Provider = provider, Model = model, MaxTokens = maxTokens }
    };

    BotConfigLoader.Save(configsDir, config);

    // Sem reload o Runtime só veria o bot novo/alterado depois de reiniciar.
    var reload = await runtime.ReloadAsync();
    return Results.Created($"/api/bots/{tenantId}", new { tenantId, runtimeReloaded = reload.Ok, runtimeError = reload.Error });
});

// POST /api/bots/{id}/test — testa mensagem contra o bot (stateless, histórico vem do cliente)
app.MapPost("/api/bots/{id}/test", async (string id, HttpRequest req) =>
{
    if (!BotConfigLoader.Exists(configsDir, id)) return Results.NotFound();
    var config = BotConfigLoader.Load(configsDir, id);

    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var message = root.GetProperty("message").GetString()!;

    var history = new List<ConversationMessage>();
    if (root.TryGetProperty("history", out var histEl))
        foreach (var h in histEl.EnumerateArray())
        {
            var role = h.GetProperty("role").GetString() == "user" ? MessageRole.User : MessageRole.Assistant;
            var content = h.GetProperty("content").GetString()!;
            history.Add(new ConversationMessage { Role = role, Content = content });
        }

    // Flow match
    var flow = config.Flows.FirstOrDefault(f =>
        message.Contains(f.Trigger, StringComparison.OrdinalIgnoreCase));

    if (flow is not null)
        return Results.Json(new { reply = flow.Response, flowMatch = flow.Trigger }, jsonOpts);

    // LLM
    history.Add(ConversationMessage.User(message));
    var llm = LlmClientFactory.Create(config, new HttpClient());
    var reply = await llm.CompleteAsync(history);
    return Results.Json(new { reply = reply.Text, flowMatch = (string?)null }, jsonOpts);
});

// ═══════════════════════════════════════════════════════════════════════════════
// INTEGRAÇÕES (catálogo + aba Integrações de cada bot)
// ═══════════════════════════════════════════════════════════════════════════════

// GET /api/integrations — catálogo e clientes que usam cada integração
app.MapGet("/api/integrations", (BotIntegrationService integrations) =>
    Results.Json(integrations.Usage().Select(u => new
    {
        u.Definition.Id,
        u.Definition.Name,
        u.Definition.Status,
        u.Definition.AlwaysOn,
        u.Definition.CostNote,
        u.Tenants
    }), jsonOpts));

// GET /api/bots/{id}/integrations — estado efetivo de cada integração no bot
app.MapGet("/api/bots/{id}/integrations", (string id, BotIntegrationService integrations) =>
    integrations.Get(id) is { } view
        ? Results.Json(new
        {
            view.TenantId,
            view.Type,
            view.MonthlyPriceBrl,
            view.Warnings,
            Integrations = view.Integrations.Select(i => new
            {
                i.Definition.Id,
                i.Definition.Name,
                i.Definition.Status,
                i.Enabled,
                i.BlockedReason,
                i.Fields,
                i.CompletedSteps
            })
        }, jsonOpts)
        : Results.NotFound());

// PUT /api/bots/{id}/integrations/{integrationId} — liga/desliga, campos e passos do cliente
app.MapPut("/api/bots/{id}/integrations/{integrationId}", async (string id, string integrationId, IntegrationUpdate update, BotIntegrationService integrations, CancellationToken ct) =>
{
    if (!BotConfigLoader.Exists(configsDir, id)) return Results.NotFound();
    var outcome = await integrations.UpdateAsync(id, integrationId, update, ct);
    return outcome.Ok
        ? Results.Json(new { runtimeReloaded = outcome.RuntimeReloaded, runtimeError = outcome.RuntimeError }, jsonOpts)
        : Results.BadRequest(new { error = outcome.Error });
});

// PUT /api/bots/{id}/price — preço mensal cobrado do cliente (margem no Financeiro)
app.MapPut("/api/bots/{id}/price", async (string id, PriceUpdate body, BotIntegrationService integrations, CancellationToken ct) =>
{
    if (!BotConfigLoader.Exists(configsDir, id)) return Results.NotFound();
    var outcome = await integrations.SetPriceAsync(id, body.MonthlyPriceBrl, ct);
    return outcome.Ok ? Results.NoContent() : Results.BadRequest(new { error = outcome.Error });
});

// POST /api/bots/{id}/integrations/{integrationId}/test — "Testar conexão" (roda no Runtime)
app.MapPost("/api/bots/{id}/integrations/{integrationId}/test", async (string id, string integrationId, HttpRequest req, RuntimeApiClient runtime, CancellationToken ct) =>
{
    if (!BotConfigLoader.Exists(configsDir, id)) return Results.NotFound();
    Dictionary<string, string>? fields = null;
    if (req.ContentLength > 0)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
            fields = f.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }
    return Results.Json(await runtime.TestIntegrationAsync(integrationId, id, fields, ct), jsonOpts);
});

// ═══════════════════════════════════════════════════════════════════════════════
// TELEMETRY (Armazenamento Simples)
// ═══════════════════════════════════════════════════════════════════════════════
var telemetryPath = Path.Combine(configsDir, "telemetry.json");

app.MapGet("/api/telemetry", () =>
{
    if (!File.Exists(telemetryPath)) return Results.Json(new object[] { }, jsonOpts);
    var json = File.ReadAllText(telemetryPath);
    return Results.Content(json, "application/json");
});

app.MapPost("/api/telemetry", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var entry = new
    {
        Timestamp = DateTimeOffset.UtcNow,
        Bot = root.TryGetProperty("bot", out var b) ? b.GetString() : "unknown",
        To = root.TryGetProperty("to", out var t) ? t.GetString() : "",
        Subject = root.TryGetProperty("subject", out var s) ? s.GetString() : "",
        Status = root.TryGetProperty("status", out var st) ? st.GetString() : "unknown"
    };

    var list = new List<object>();
    if (File.Exists(telemetryPath))
    {
        try
        {
            var existing = File.ReadAllText(telemetryPath);
            if (!string.IsNullOrWhiteSpace(existing))
                list = JsonSerializer.Deserialize<List<object>>(existing, jsonOpts) ?? new List<object>();
        }
        catch { /* ignora erro de parse */ }
    }
    
    list.Add(entry);
    File.WriteAllText(telemetryPath, JsonSerializer.Serialize(list, jsonOpts));
    return Results.Ok();
});

// ═══════════════════════════════════════════════════════════════════════════════
// SESSIONS (meta-agente)
// ═══════════════════════════════════════════════════════════════════════════════

// POST /api/sessions — inicia nova sessão de entrevista
app.MapPost("/api/sessions", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var mode = root.TryGetProperty("mode", out var m) ? m.GetString() : "create";
    var tenantId = root.TryGetProperty("tenantId", out var t) ? t.GetString() : null;

    string? editContext = null;
    if (mode == "edit" && tenantId is not null)
    {
        try
        {
            var existing = BotConfigLoader.Load(configsDir, tenantId);
            editContext = $"""
                Este é o config atual do bot '{tenantId}'. Use-o como base para as alterações pedidas:

                System prompt atual:
                {existing.SystemPrompt}

                Flows atuais:
                {JsonSerializer.Serialize(existing.Flows, jsonOpts)}
                """;
        }
        catch { /* config não encontrado, cria do zero */ }
    }

    var session = store.Create(BotType.Generic, editContext);
    var greeting = mode == "edit"
        ? $"Config do '{tenantId}' carregado. O que você quer alterar?"
        : "Olá! Vou te ajudar a configurar o bot. Me conta: qual é o negócio e o que ele faz?";

    return Results.Json(new { sessionId = session.Id, greeting }, jsonOpts);
});

// POST /api/sessions/{id}/message — envia mensagem para o meta-agente
app.MapPost("/api/sessions/{id}/message", async (Guid id, HttpRequest req, CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var message = doc.RootElement.GetProperty("message").GetString()!;

    try
    {
        var result = await store.SendAsync(id, message, ct);
        return Results.Json(new
        {
            reply = result.Reply,
            configReady = result.ConfigReady,
            jsonConfig = result.JsonConfig
        }, jsonOpts);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "Sessão não encontrada ou expirada." });
    }
});

// DELETE /api/sessions/{id} — descarta sessão
app.MapDelete("/api/sessions/{id}", (Guid id) =>
{
    store.Remove(id);
    return Results.NoContent();
});

// ─── SPA fallback ─────────────────────────────────────────────────────────────
app.MapRazorComponents<Agiliz.Wizard.Components.App>()
   .AddInteractiveServerRenderMode();

app.Run();

// Exposes Program to WebApplicationFactory in tests
namespace Agiliz.Wizard { public partial class Program { } }
