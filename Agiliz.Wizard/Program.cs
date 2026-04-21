using System.Text.Json;
using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;
using Agiliz.Wizard.Services;

var builder = WebApplication.CreateBuilder(args);

var configsDir = builder.Configuration["ConfigsDir"]
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"));

builder.Services.AddSingleton<WizardSessionStore>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(options => { options.DetailedErrors = true; });

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseCors();
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
    try
    {
        var config = BotConfigLoader.Load(configsDir, id);
        return Results.Json(config, jsonOpts);
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound();
    }
});

// DELETE /api/bots/{id}
app.MapDelete("/api/bots/{id}", (string id) =>
{
    var path = Path.Combine(configsDir, $"{id}.json");
    if (!File.Exists(path)) return Results.NotFound();
    File.Delete(path);
    return Results.NoContent();
});

// POST /api/bots — salva bot a partir dos dados do wizard
app.MapPost("/api/bots", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;

    var tenantId = root.GetProperty("tenantId").GetString()!.Trim().ToLower().Replace(" ", "-");
    var whatsappNumber = root.GetProperty("whatsappNumber").GetString()!;
    // Remove any non-digits
    whatsappNumber = System.Text.RegularExpressions.Regex.Replace(whatsappNumber, @"\D", "");

    var providerStr = root.GetProperty("provider").GetString()!;
    var provider = providerStr == "Claude" ? LlmProvider.Claude : LlmProvider.Groq;
    var model = provider == LlmProvider.Groq ? "llama-3.3-70b-versatile" : "claude-sonnet-4-20250514";
    var maxTokens = root.TryGetProperty("maxTokens", out var mt) ? mt.GetInt32() : 300;

    var systemPrompt = root.GetProperty("systemPrompt").GetString()!;
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
        WhatsAppNumber = whatsappNumber,
        SystemPrompt = systemPrompt,
        Flows = flows,
        Llm = new LlmSettings { Provider = provider, Model = model, MaxTokens = maxTokens }
    };

    BotConfigLoader.Save(configsDir, config);
    return Results.Created($"/api/bots/{tenantId}", new { tenantId });
});

// POST /api/bots/{id}/test — testa mensagem contra o bot (stateless, histórico vem do cliente)
app.MapPost("/api/bots/{id}/test", async (string id, HttpRequest req) =>
{
    BotConfig config;
    try { config = BotConfigLoader.Load(configsDir, id); }
    catch (FileNotFoundException) { return Results.NotFound(); }

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
    return Results.Json(new { reply, flowMatch = (string?)null }, jsonOpts);
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

    var session = store.Create(editContext);
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
