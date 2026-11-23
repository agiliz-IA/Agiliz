using Agiliz.Core.Integrations;
using Agiliz.Core.Models;
using Agiliz.Core.Plans;
using Agiliz.Wizard.Services;

namespace Agiliz.Wizard.Portal;

/// <summary>
/// API do portal do cliente (/portal/api). Fica fora do Basic Auth do admin e tem login próprio
/// (código pelo WhatsApp + cookie HttpOnly). Toda rota com {tenantId} confere se o telefone logado
/// tem acesso àquele bot; se não tiver, responde 404 (não revela que o bot existe).
/// </summary>
public static class PortalEndpoints
{
    public const string CsrfHeader = "X-Agiliz-Portal";
    public const string LoginRateLimit = "portal-login";

    public sealed record StartRequest(string? Phone);
    public sealed record VerifyRequest(string? Phone, string? Code);
    public sealed record ReplyRequest(string? Body);
    public sealed record PauseRequest(bool Paused);
    public sealed record ToggleRequest(bool Enabled);

    private static readonly TimeZoneInfo Brasilia = FindBrasilia();

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/portal/api").DisableAntiforgery();

        api.MapPost("/login/start", StartAsync).RequireRateLimiting(LoginRateLimit);
        api.MapPost("/login/verify", Verify).RequireRateLimiting(LoginRateLimit);
        api.MapPost("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(PortalAuthService.CookieName, new CookieOptions { Path = "/portal" });
            return Results.NoContent();
        });

        var session = api.MapGroup("").AddEndpointFilter<PortalSessionFilter>();
        session.MapGet("/me", (HttpContext http, PortalAuthService auth) =>
        {
            var phone = PortalSessionFilter.Phone(http);
            return Results.Ok(new
            {
                phone = PortalAuthService.DisplayPhone(phone, mask: false),
                bots = auth.BotsFor(phone).Select(b => new { b.TenantId, name = Name(b), b.Type, b.IsDemo, b.Paused })
            });
        });

        var bot = session.MapGroup("/bots/{tenantId}");
        bot.MapGet("/overview", OverviewAsync);
        bot.MapGet("/conversations", ConversationsAsync);
        bot.MapGet("/conversations/{id}", ConversationAsync);
        bot.MapPost("/conversations/{id}/reply", ReplyAsync);
        bot.MapPost("/conversations/{id}/takeover", (string tenantId, string id, HttpContext http, PortalAuthService auth, RuntimeApiClient runtime, CancellationToken ct) =>
            WithContact(http, auth, tenantId, id, async phone => await runtime.PauseConversationAsync(tenantId, phone, ct)));
        bot.MapPost("/conversations/{id}/release", (string tenantId, string id, HttpContext http, PortalAuthService auth, RuntimeApiClient runtime, CancellationToken ct) =>
            WithContact(http, auth, tenantId, id, async phone => await runtime.ResumeConversationAsync(tenantId, phone, ct)));
        bot.MapGet("/appointments", AppointmentsAsync);
        bot.MapPost("/pause", async (string tenantId, PauseRequest body, BotIntegrationService integrations, CancellationToken ct) =>
        {
            var outcome = await integrations.SetPausedAsync(tenantId, body.Paused, ct);
            return outcome.Ok ? Results.Ok(new { paused = body.Paused, applied = outcome.RuntimeReloaded }) : Results.BadRequest(new { error = outcome.Error });
        });
        bot.MapGet("/integrations", Integrations);
        bot.MapPost("/integrations/{integrationId}", ToggleIntegrationAsync);
        bot.MapPost("/integrations/{integrationId}/test", async (string tenantId, string integrationId, RuntimeApiClient runtime, CancellationToken ct) =>
            IntegrationCatalog.Find(integrationId) is { Testable: true }
                ? Results.Ok(await runtime.TestIntegrationAsync(integrationId, tenantId, null, ct))
                : Results.NotFound());
        bot.MapPost("/whatsapp/pairing", async (string tenantId, RuntimeApiClient runtime, CancellationToken ct) =>
            Results.Ok(await runtime.RequestPairingAsync(tenantId, ct)));
        bot.MapGet("/whatsapp/status", async (string tenantId, RuntimeApiClient runtime, CancellationToken ct) =>
            Results.Ok(await runtime.TestIntegrationAsync(IntegrationCatalog.WhatsApp, tenantId, null, ct)));
        bot.MapPost("/demo/reset", async (HttpContext http, string tenantId, RuntimeApiClient runtime, CancellationToken ct) =>
        {
            if (!PortalSessionFilter.Bot(http).IsDemo) return Results.NotFound();
            return await runtime.ResetDemoAsync(tenantId, ct)
                ? Results.NoContent()
                : Results.Problem("Não foi possível limpar a demonstração agora.", statusCode: StatusCodes.Status502BadGateway);
        });
    }

    // ─── Login ────────────────────────────────────────────────────────────────

    private static async Task<IResult> StartAsync(StartRequest body, PortalAuthService auth, CancellationToken ct) =>
        await auth.StartAsync(body.Phone, ct) switch
        {
            // Telefone desconhecido recebe a mesma resposta: não dá para descobrir quem é cliente.
            PortalLoginStart.Sent or PortalLoginStart.UnknownPhone =>
                Results.Ok(new { message = "Se este número tiver acesso, o código chega no WhatsApp em instantes." }),
            PortalLoginStart.TooSoon => Results.Json(new { error = "Aguarde um minuto para pedir outro código." }, statusCode: StatusCodes.Status429TooManyRequests),
            _ => Results.Json(new { error = "Não conseguimos enviar o código agora. Tente de novo em alguns minutos." }, statusCode: StatusCodes.Status502BadGateway)
        };

    private static IResult Verify(VerifyRequest body, HttpContext http, PortalAuthService auth, IConfiguration config)
    {
        var token = auth.Verify(body.Phone, body.Code?.Trim());
        if (token is null) return Results.BadRequest(new { error = "Código inválido ou expirado." });

        http.Response.Cookies.Append(PortalAuthService.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps || config.GetValue("Portal:SecureCookies", false),
            Path = "/portal",
            MaxAge = PortalAuthService.SessionLifetime
        });
        return Results.NoContent();
    }

    // ─── Início ───────────────────────────────────────────────────────────────

    private static async Task<IResult> OverviewAsync(string tenantId, HttpContext http, RuntimeApiClient runtime, PlanStore plans,
        AllowanceService allowance, CancellationToken ct)
    {
        var bot = PortalSessionFilter.Bot(http);
        var plan = plans.Get(bot.PlanId);
        var usage = await allowance.GetAsync(ct: ct);
        var status = usage.Error is null ? usage.For(tenantId) : null;

        IReadOnlyList<RuntimeConversation> conversations = [];
        IReadOnlyList<RuntimeAppointment> today = [];
        string? error = null;
        try
        {
            conversations = await runtime.GetConversationsAsync(tenantId, 50, ct);
            var (from, to) = Day(DateTimeOffset.UtcNow);
            today = await runtime.GetAppointmentsAsync(tenantId, from, to, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            error = "Os dados de atendimento estão indisponíveis agora.";
        }

        return Results.Ok(new
        {
            name = Name(bot),
            bot.Paused,
            bot.IsDemo,
            bot.Type,
            plan = plan is null ? null : new { plan.Name, plan.ConversationAllowance },
            usage = status is null ? null : new
            {
                status.Contacts,
                allowance = status.Level == AllowanceLevel.NoPlan ? (int?)null : status.Allowance,
                ratio = status.Level == AllowanceLevel.NoPlan ? (decimal?)null : status.Ratio,
                level = status.Level.ToString()
            },
            waiting = conversations.Count(c => c.IsPaused),
            appointmentsToday = today.Count(a => a.Status != "Cancelled"),
            recent = conversations.Take(5).Select(c => Conversation(bot, c, http)),
            error
        });
    }

    // ─── Conversas ────────────────────────────────────────────────────────────

    private static async Task<IResult> ConversationsAsync(string tenantId, HttpContext http, RuntimeApiClient runtime, CancellationToken ct)
    {
        var bot = PortalSessionFilter.Bot(http);
        try
        {
            var conversations = await runtime.GetConversationsAsync(tenantId, 100, ct);
            return Results.Ok(conversations.Select(c => Conversation(bot, c, http)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> ConversationAsync(string tenantId, string id, HttpContext http, PortalAuthService auth, RuntimeApiClient runtime, CancellationToken ct)
    {
        var bot = PortalSessionFilter.Bot(http);
        if (auth.ContactFromId(tenantId, id) is not { } phone) return Results.NotFound();
        try
        {
            var history = await runtime.GetMessagesAsync(tenantId, phone, ct);
            var summary = (await runtime.GetConversationsAsync(tenantId, 200, ct)).FirstOrDefault(c => c.Phone == phone);
            return Results.Ok(new
            {
                id,
                contact = summary?.Name ?? PortalAuthService.DisplayPhone(phone, bot.IsDemo),
                phone = PortalAuthService.DisplayPhone(phone, bot.IsDemo),
                withHuman = history?.IsPaused ?? summary?.IsPaused ?? false,
                lastActivityAt = summary?.LastActivityAt,
                lastMessagePreview = summary?.LastMessagePreview,
                messages = history?.Messages ?? []
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> ReplyAsync(string tenantId, string id, ReplyRequest body, HttpContext http, PortalAuthService auth, RuntimeApiClient runtime, CancellationToken ct)
    {
        if (auth.ContactFromId(tenantId, id) is not { } phone) return Results.NotFound();
        var text = body.Body?.Trim();
        if (string.IsNullOrEmpty(text)) return Results.BadRequest(new { error = "Escreva a mensagem." });
        if (text.Length > 4000) return Results.BadRequest(new { error = "Mensagem longa demais." });

        return await runtime.ReplyAsync(tenantId, phone, text, ct)
            ? Results.NoContent()
            : Results.Problem("A mensagem não foi enviada. Confira se o WhatsApp está conectado.", statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> WithContact(HttpContext http, PortalAuthService auth, string tenantId, string id, Func<string, Task<bool>> action)
    {
        if (auth.ContactFromId(tenantId, id) is not { } phone) return Results.NotFound();
        return await action(phone) ? Results.NoContent() : Unavailable();
    }

    private static object Conversation(BotConfig bot, RuntimeConversation c, HttpContext http)
    {
        var auth = http.RequestServices.GetRequiredService<PortalAuthService>();
        return new
        {
            id = auth.ConversationId(bot.TenantId, c.Phone),
            contact = c.Name ?? PortalAuthService.DisplayPhone(c.Phone, bot.IsDemo),
            phone = PortalAuthService.DisplayPhone(c.Phone, bot.IsDemo),
            withHuman = c.IsPaused,
            c.LastActivityAt,
            preview = c.LastMessagePreview
        };
    }

    // ─── Agenda ───────────────────────────────────────────────────────────────

    private static async Task<IResult> AppointmentsAsync(string tenantId, string? day, HttpContext http, RuntimeApiClient runtime, CancellationToken ct)
    {
        var bot = PortalSessionFilter.Bot(http);
        var date = DateOnly.TryParseExact(day, "yyyy-MM-dd", out var parsed) ? parsed : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Brasilia).DateTime);
        var (from, to) = Day(date);
        try
        {
            var appointments = await runtime.GetAppointmentsAsync(tenantId, from, to, ct);
            return Results.Ok(new
            {
                day = date.ToString("yyyy-MM-dd"),
                appointments = appointments.Select(a => new
                {
                    time = TimeZoneInfo.ConvertTime(a.ScheduledTime, Brasilia).ToString("HH:mm"),
                    a.DurationMinutes,
                    contact = a.Name ?? PortalAuthService.DisplayPhone(a.Phone, bot.IsDemo),
                    status = a.Status switch { "Confirmed" => "confirmada", "Cancelled" => "cancelada", "NoShow" => "faltou", _ => "marcada" }
                })
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Unavailable();
        }
    }

    // ─── Integrações ──────────────────────────────────────────────────────────

    /// <summary>Mesma tela para qualquer integração. O cliente liga ou desliga só o que já está no plano.</summary>
    private static IResult Integrations(string tenantId, HttpContext http, BotIntegrationService integrations, PlanStore plans)
    {
        var bot = PortalSessionFilter.Bot(http);
        var plan = plans.Get(bot.PlanId);
        var view = integrations.Get(tenantId);
        if (view is null) return Results.NotFound();

        return Results.Ok(view.Integrations
            .Where(i => i.Definition.Status != IntegrationStatus.ComingSoon && (i.Definition.OnlyFor is null || i.Definition.OnlyFor == bot.Type))
            .Select(i => new
            {
                id = i.Definition.Id,
                name = i.Definition.Name,
                description = i.Definition.Description,
                enabled = i.Enabled,
                alwaysOn = i.Definition.AlwaysOn,
                status = i.Definition.Status == IntegrationStatus.OnRequest ? "sob consulta" : i.Enabled ? "ligada" : "desligada",
                offer = i.Definition.AlwaysOn ? "incluída"
                    : plan is null ? null
                    : plan.Includes(i.Definition.Id) ? "incluída no plano"
                    : plan.AddOnPrice(i.Definition.Id) is { } price ? $"adicional de {price.ToString("C2", new System.Globalization.CultureInfo("pt-BR"))}/mês"
                    : "fora do plano",
                canToggle = CanToggle(i, plan),
                testable = i.Definition.Testable,
                steps = i.Definition.ClientSteps.Select(s => new { s.Text, done = i.CompletedSteps.Contains(s.Id) })
            }));
    }

    private static bool CanToggle(IntegrationState i, Plan? plan) =>
        i.Definition.Status == IntegrationStatus.Available && !i.Definition.AlwaysOn &&
        plan is not null && plan.Includes(i.Definition.Id) && (i.Enabled || i.BlockedReason is null);

    private static async Task<IResult> ToggleIntegrationAsync(string tenantId, string integrationId, ToggleRequest body, HttpContext http,
        BotIntegrationService integrations, PlanStore plans, CancellationToken ct)
    {
        var bot = PortalSessionFilter.Bot(http);
        var state = integrations.Get(tenantId)?.Integrations.FirstOrDefault(i => i.Definition.Id == integrationId);
        if (state is null) return Results.NotFound();
        if (!CanToggle(state, plans.Get(bot.PlanId)))
            return Results.Json(new { error = "Esta integração não está no seu plano. Fale com a equipe Agiliz para contratar." }, statusCode: StatusCodes.Status403Forbidden);

        var outcome = await integrations.UpdateAsync(tenantId, integrationId, new IntegrationUpdate
        {
            Enabled = body.Enabled,
            Fields = new Dictionary<string, string>(state.Fields),
            CompletedSteps = state.CompletedSteps.ToList()
        }, ct);

        return outcome.Ok
            ? Results.Ok(new { enabled = body.Enabled })
            : Results.BadRequest(new { error = $"{outcome.Error} A equipe Agiliz ajuda a configurar." });
    }

    // ─── Apoio ────────────────────────────────────────────────────────────────

    public static string Name(BotConfig bot) => string.IsNullOrWhiteSpace(bot.DisplayName) ? bot.TenantId : bot.DisplayName!;

    private static IResult Unavailable() =>
        Results.Json(new { error = "Os dados de atendimento estão indisponíveis agora. Tente de novo em instantes." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static (DateTimeOffset From, DateTimeOffset To) Day(DateTimeOffset instant) =>
        Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Brasilia).DateTime));

    private static (DateTimeOffset From, DateTimeOffset To) Day(DateOnly date)
    {
        var start = date.ToDateTime(TimeOnly.MinValue);
        var offset = Brasilia.GetUtcOffset(start);
        var from = new DateTimeOffset(start, offset);
        return (from, from.AddDays(1));
    }

    private static TimeZoneInfo FindBrasilia()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT"); }
    }
}

/// <summary>
/// Exige a sessão do portal; em rotas com {tenantId}, exige acesso àquele bot (senão 404).
/// Requisições que mudam algo precisam do header <see cref="PortalEndpoints.CsrfHeader"/> (proteção contra CSRF).
/// </summary>
public sealed class PortalSessionFilter(PortalAuthService auth) : IEndpointFilter
{
    private const string PhoneKey = "portal.phone";
    private const string BotKey = "portal.bot";

    public static string Phone(HttpContext http) => (string)http.Items[PhoneKey]!;
    public static BotConfig Bot(HttpContext http) => (BotConfig)http.Items[BotKey]!;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers[PortalEndpoints.CsrfHeader] != "1")
            return Results.BadRequest(new { error = "Requisição inválida." });

        var phone = auth.ReadSession(http.Request.Cookies[PortalAuthService.CookieName]);
        if (phone is null) return Results.Unauthorized();
        http.Items[PhoneKey] = phone;

        if (http.Request.RouteValues.TryGetValue("tenantId", out var raw) && raw is string tenantId)
        {
            if (auth.BotFor(phone, tenantId) is not { } bot) return Results.NotFound();
            http.Items[BotKey] = bot;
        }

        return await next(context);
    }
}
