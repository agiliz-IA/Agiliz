using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Agiliz.Wizard.Accounts;

/// <summary>
/// "Continuar com Google" (OpenID Connect, fluxo de código). Precisa de Auth:Google:ClientId e Auth:Google:ClientSecret
/// (Google Cloud → APIs e serviços → Credenciais → ID do cliente OAuth, tipo "Aplicativo da Web") e do endereço de retorno
/// {endereço público}/entrar/google/retorno cadastrado lá. Sem essas chaves o botão nem aparece.
/// O id_token vem direto do Google por HTTPS, em troca do código, então não precisa conferir a assinatura (OIDC 3.1.3.7).
/// </summary>
public sealed class GoogleSignIn(IConfiguration config, IHttpClientFactory httpFactory, IDataProtectionProvider protection, ILogger<GoogleSignIn> logger)
{
    public const string StateCookie = "agiliz_google";
    public const string HttpClientName = "google";

    public sealed record State(string Nonce, string Mode, string? Return, string? AccountId);

    public sealed record GoogleUser(string Sub, string Email);

    private readonly ITimeLimitedDataProtector _state = protection.CreateProtector("agiliz.google.state.v1").ToTimeLimitedDataProtector();

    private string? ClientId => config["Auth:Google:ClientId"];
    private string? ClientSecret => config["Auth:Google:ClientSecret"];

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public static string RedirectUri(HttpRequest request, IConfiguration config)
    {
        var baseUrl = config["Auth:PublicUrl"] is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}{request.PathBase}";
        return baseUrl + "/entrar/google/retorno";
    }

    /// <summary>Endereço do Google para onde mandar a pessoa; o estado vai num cookie protegido de 10 minutos.</summary>
    public string Begin(HttpContext http, string mode, string? returnPath, string? accountId)
    {
        var state = new State(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), mode, returnPath, accountId);
        http.Response.Cookies.Append(StateCookie, _state.Protect(JsonSerializer.Serialize(state), TimeSpan.FromMinutes(10)), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps || config.GetValue("Portal:SecureCookies", false),
            Path = "/entrar/google",
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["redirect_uri"] = RedirectUri(http.Request, config),
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state.Nonce,
            ["prompt"] = "select_account"
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Confere o estado e troca o código pelo usuário do Google. Null = cancelado, vencido ou inválido.</summary>
    public async Task<(State State, GoogleUser User)?> CompleteAsync(HttpContext http, string? code, string? nonce, CancellationToken ct)
    {
        var raw = http.Request.Cookies[StateCookie];
        http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/entrar/google" });
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(nonce)) return null;

        State? state;
        try { state = JsonSerializer.Deserialize<State>(_state.Unprotect(raw)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
        if (state is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state.Nonce), Encoding.UTF8.GetBytes(nonce))) return null;

        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName)
                .PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = ClientId!,
                    ["client_secret"] = ClientSecret!,
                    ["redirect_uri"] = RedirectUri(http.Request, config),
                    ["grant_type"] = "authorization_code"
                }), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Google recusou o código: HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var user = ReadIdToken(doc.RootElement.GetProperty("id_token").GetString());
            return user is null ? null : (state, user);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Falha ao falar com o Google");
            return null;
        }
    }

    /// <summary>Lê o id_token (payload do JWT): confere emissor, destinatário, validade e e-mail verificado.</summary>
    public GoogleUser? ReadIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        var root = doc.RootElement;

        var issuer = root.TryGetProperty("iss", out var iss) ? iss.GetString() : null;
        var audience = root.TryGetProperty("aud", out var aud) ? aud.GetString() : null;
        var expires = root.TryGetProperty("exp", out var exp) ? exp.GetInt64() : 0;
        var verified = root.TryGetProperty("email_verified", out var ev) && (ev.ValueKind == JsonValueKind.True || ev.ValueKind == JsonValueKind.String && ev.GetString() == "true");

        if (issuer is not ("https://accounts.google.com" or "accounts.google.com") || audience != ClientId
            || DateTimeOffset.FromUnixTimeSeconds(expires) < DateTimeOffset.UtcNow || !verified)
            return null;

        var sub = root.GetProperty("sub").GetString();
        var email = root.GetProperty("email").GetString();
        return string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email) ? null : new GoogleUser(sub, email.ToLowerInvariant());
    }
}
