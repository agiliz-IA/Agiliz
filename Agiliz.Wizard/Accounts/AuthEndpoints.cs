using Agiliz.Wizard.Portal;

namespace Agiliz.Wizard.Accounts;

/// <summary>
/// Entrada única (/entrar): a mesma tela para funcionários e clientes. Página estática + API (/entrar/api),
/// fora do Basic Auth e sem circuito Blazor, como o portal. Requisições que mudam algo exigem o header
/// <see cref="PortalEndpoints.CsrfHeader"/> (o navegador não manda esse header de outro site sem pedir licença).
/// </summary>
public static class AuthEndpoints
{
    public const string LoginRateLimit = PortalEndpoints.LoginRateLimit;

    public sealed record StartRequest(string? Identifier, string? Via);
    public sealed record VerifyRequest(string? Identifier, string? Code, string? Return);
    public sealed record LinkRequest(string? Identifier);
    public sealed record CodeRequest(string? Code);
    public sealed record RemoveRequest(IdentityKind Kind, string? Value);

    public static void Map(WebApplication app)
    {
        var page = Path.Combine(app.Environment.WebRootPath, "entrar", "index.html");
        IResult Page() => Results.File(page, "text/html; charset=utf-8");

        // Só as telas conhecidas: uma rota "pega-tudo" impediria o UseStaticFiles de servir entrar.js e entrar.css.
        foreach (var screen in new[] { "/entrar", "/entrar/proteger", "/entrar/escolher", "/entrar/conta", "/entrar/sair" })
            app.MapGet(screen, Page);

        app.MapGet("/entrar/convite/{token}", AcceptInvite).RequireRateLimiting(LoginRateLimit);
        app.MapGet("/entrar/google", (HttpContext http, string? modo, string? volta, GoogleSignIn google, AuthService auth) =>
        {
            if (!google.Enabled) return Results.Redirect("/entrar?erro=google-desligado");
            var linking = modo == "vincular";
            var account = linking ? auth.ReadSession(http.Request.Cookies[AuthService.CookieName]) : null;
            if (linking && account is null) return Results.Redirect("/entrar");
            return Results.Redirect(google.Begin(http, linking ? "vincular" : "entrar", AuthService.SafeReturn(volta), account?.Id));
        });
        app.MapGet("/entrar/google/retorno", GoogleReturnAsync);

        var api = app.MapGroup("/entrar/api").DisableAntiforgery().AddEndpointFilter<CsrfFilter>();

        api.MapGet("/config", (GoogleSignIn google, AuthService auth) => Results.Ok(new { google = google.Enabled, email = auth.EmailAvailable }));
        api.MapPost("/start", StartAsync).RequireRateLimiting(LoginRateLimit);
        api.MapPost("/verify", (VerifyRequest body, HttpContext http, AuthService auth, IConfiguration config) =>
        {
            var account = auth.VerifyLogin(body.Identifier, body.Code);
            if (account is null) return Results.BadRequest(new { error = "Código errado ou vencido. Confira os números ou peça outro." });
            AuthService.SignIn(http, auth.CreateSession(account), config);
            return Results.Ok(new { next = NextAfterLogin(auth, account, body.Return) });
        }).RequireRateLimiting(LoginRateLimit);
        api.MapPost("/logout", (HttpContext http) =>
        {
            AuthService.SignOut(http);
            return Results.NoContent();
        });

        var session = api.MapGroup("").AddEndpointFilter<AccountSessionFilter>();
        session.MapGet("/me", (HttpContext http, AuthService auth) => Results.Ok(Me(auth, AccountSessionFilter.Account(http))));
        session.MapPost("/protect-seen", (HttpContext http, AccountStore store) =>
        {
            store.Update(AccountSessionFilter.Account(http).Id, a => a.ProtectPromptSeen = true);
            return Results.NoContent();
        });
        session.MapPost("/identities/start", async (LinkRequest body, HttpContext http, AuthService auth, CancellationToken ct) =>
            StartResult(await auth.StartLinkAsync(AccountSessionFilter.Account(http), body.Identifier, ct))).RequireRateLimiting(LoginRateLimit);
        session.MapPost("/identities/verify", (CodeRequest body, HttpContext http, AuthService auth) =>
            auth.VerifyLink(AccountSessionFilter.Account(http), body.Code) is { } account
                ? Results.Ok(Me(auth, account))
                : Results.BadRequest(new { error = "Código errado ou vencido. Confira os números ou peça outro." })).RequireRateLimiting(LoginRateLimit);
        session.MapPost("/identities/remove", (RemoveRequest body, HttpContext http, AuthService auth) =>
            auth.RemoveIdentity(AccountSessionFilter.Account(http), body.Kind, body.Value ?? "") is { } account
                ? Results.Ok(Me(auth, account))
                : Results.BadRequest(new { error = "Essa é a única forma de entrar da sua conta. Adicione outra antes de remover." }));
        session.MapPost("/logout-all", (HttpContext http, AuthService auth) =>
        {
            auth.SignOutEverywhere(AccountSessionFilter.Account(http));
            AuthService.SignOut(http);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> StartAsync(StartRequest body, AuthService auth, CancellationToken ct) =>
        StartResult(await auth.StartLoginAsync(body.Identifier, body.Via, ct));

    private static IResult StartResult(LoginStartResult result) => result.Status switch
    {
        LoginStart.Sent => Results.Ok(new { channel = result.Channel, sentTo = result.SentTo }),
        LoginStart.Invalid => Results.BadRequest(new { error = "Digite um WhatsApp com DDD, como (21) 98888-7777, ou um e-mail." }),
        LoginStart.NoAccess => Results.NotFound(new
        {
            code = "sem-acesso",
            error = "Este número ou e-mail ainda não tem acesso. Confira se digitou certo ou entre de outro jeito. Se é cliente novo, peça o convite ao seu contato na Agiliz."
        }),
        LoginStart.TooSoon => Results.Json(new { error = "Já mandamos um código há menos de um minuto. Espere um pouco para pedir outro.", channel = result.Channel, sentTo = result.SentTo },
            statusCode: StatusCodes.Status429TooManyRequests),
        LoginStart.EmailUnavailable => Results.BadRequest(new { error = "Não dá para mandar por e-mail: a conta não tem e-mail cadastrado ou o envio de e-mail não está configurado." }),
        LoginStart.AlreadyUsed => Results.Conflict(new
        {
            error = result.Channel is null
                ? "Essa forma de entrar já é de outra conta. Fale com a equipe Agiliz se precisar juntar as duas."
                : "Essa forma de entrar já está na sua conta."
        }),
        _ => Results.Json(new
        {
            error = result.Channel == IdentityKind.Email
                ? "Não conseguimos mandar o e-mail agora. Tente pelo WhatsApp ou pelo Google."
                : "Não conseguimos mandar pelo WhatsApp agora. Tente pelo e-mail ou pelo Google, ou peça de novo em alguns minutos."
        }, statusCode: StatusCodes.Status502BadGateway)
    };

    private static IResult AcceptInvite(string token, HttpContext http, AuthService auth, IConfiguration config)
    {
        var account = auth.AcceptInvite(token);
        if (account is null) return Results.Redirect("/entrar?erro=convite");
        AuthService.SignIn(http, auth.CreateSession(account), config);
        return Results.Redirect(NextAfterLogin(auth, account, null));
    }

    private static async Task<IResult> GoogleReturnAsync(HttpContext http, string? code, string? state, string? error,
        GoogleSignIn google, AuthService auth, IConfiguration config, CancellationToken ct)
    {
        if (error is not null) return Results.Redirect("/entrar");
        var result = await google.CompleteAsync(http, code, state, ct);
        if (result is not { } ok) return Results.Redirect("/entrar?erro=google");

        if (ok.State.Mode == "vincular")
        {
            var current = auth.ReadSession(http.Request.Cookies[AuthService.CookieName]);
            if (current is null || current.Id != ok.State.AccountId) return Results.Redirect("/entrar");
            var linked = auth.LinkGoogle(current, ok.User.Sub, ok.User.Email);
            return Results.Redirect(AuthService.SafeReturn(ok.State.Return) is { } back
                ? back + (linked ? "" : (back.Contains('?') ? "&" : "?") + "erro=google-outra-conta")
                : linked ? "/entrar/conta" : "/entrar/conta?erro=google-outra-conta");
        }

        var account = auth.SignInWithGoogle(ok.User.Sub, ok.User.Email);
        if (account is null) return Results.Redirect("/entrar?erro=google-sem-acesso");
        AuthService.SignIn(http, auth.CreateSession(account), config);
        return Results.Redirect(NextAfterLogin(auth, account, ok.State.Return));
    }

    /// <summary>Primeira vez e uma forma só de entrar: passa pela tela "Proteja sua conta" antes.</summary>
    public static string NextAfterLogin(AuthService auth, Account account, string? requested)
    {
        var destination = AuthService.SafeReturn(requested) ?? auth.Destination(account);
        return !account.ProtectPromptSeen && account.Identities.Count == 1
            ? "/entrar/proteger?volta=" + Uri.EscapeDataString(destination)
            : destination;
    }

    public static object Me(AuthService auth, Account account)
    {
        var bots = auth.BotsFor(account);
        return new
        {
            name = account.Name,
            role = account.Role,
            staff = account.IsStaff,
            client = bots.Count > 0,
            bots = bots.Count,
            destination = auth.Destination(account),
            protectPromptSeen = account.ProtectPromptSeen,
            identities = account.Identities.Select(i => new { kind = i.Kind, value = i.Value, label = AuthService.Display(i) })
        };
    }
}

/// <summary>Mudanças (POST) só com o header anti-CSRF.</summary>
public sealed class CsrfFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers[PortalEndpoints.CsrfHeader] != "1")
            return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
        return next(context);
    }
}

/// <summary>Exige sessão válida e guarda a conta no HttpContext.</summary>
public sealed class AccountSessionFilter(AuthService auth) : IEndpointFilter
{
    private const string Key = "agiliz.account";

    public static Account Account(HttpContext http) => (Account)http.Items[Key]!;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (auth.ReadSession(http.Request.Cookies[AuthService.CookieName]) is not { } account)
            return ValueTask.FromResult<object?>(Results.Json(new { error = "Sessão expirada. Entre de novo." }, statusCode: StatusCodes.Status401Unauthorized));
        http.Items[Key] = account;
        return next(context);
    }
}
