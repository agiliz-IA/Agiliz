using System.Collections.Concurrent;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Core.Tools;
using Agiliz.Wizard.Portal;
using Agiliz.Wizard.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Agiliz.Wizard.Accounts;

public enum LoginStart { Sent, Invalid, NoAccess, TooSoon, SendFailed, EmailUnavailable, AlreadyUsed }

public sealed record LoginStartResult(LoginStart Status, IdentityKind? Channel = null, string? SentTo = null);

/// <summary>
/// Entrada única do painel e do portal: uma conta por pessoa, várias formas de entrar (WhatsApp, e-mail, Google), sem senha.
/// Códigos de 6 dígitos valem 10 minutos, 5 tentativas, reenvio a cada 60 s. A sessão (cookie) guarda só o id da conta
/// e a versão da sessão: tirar o acesso ou "sair de todos os aparelhos" vale na próxima requisição.
/// </summary>
public sealed class AuthService(
    AccountStore accounts,
    BotIntegrationService bots,
    RuntimeApiClient runtime,
    SmtpEmailSender email,
    IDataProtectionProvider protection,
    IConfiguration config,
    ILogger<AuthService> logger)
{
    public const string CookieName = "agiliz_sessao";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    public const int MaxAttempts = 5;

    private sealed class Pending
    {
        public required byte[] Hash { get; init; }
        public required DateTimeOffset SentAt { get; init; }
        public required IdentityKind Kind { get; init; }
        public required string Value { get; init; }
        public int Attempts { get; set; }
    }

    private sealed record SessionPayload(string A, int V, long E);

    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly IDataProtector _sessions = protection.CreateProtector("agiliz.session.v1");

    public bool EmailAvailable => email.IsConfigured;

    // ─── Identificadores ──────────────────────────────────────────────────────

    /// <summary>Reconhece sozinho se é e-mail ou WhatsApp. Null = nem um nem outro.</summary>
    public static (IdentityKind Kind, string Value)? Parse(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Contains('@'))
        {
            return MailAddress.TryCreate(text, out var address) && address.Address == text && address.Host.Contains('.')
                ? (IdentityKind.Email, text.ToLowerInvariant())
                : null;
        }

        var phone = PhoneNumbers.Normalize(text);
        return phone.Length is >= 12 and <= 15 ? (IdentityKind.WhatsApp, phone) : null;
    }

    public static string Display(IdentityKind kind, string value, bool mask = false) => kind switch
    {
        IdentityKind.WhatsApp => PortalAuthService.DisplayPhone(value, mask),
        IdentityKind.Email when mask => MaskEmail(value),
        _ => value
    };

    public static string Display(AccountIdentity identity) =>
        identity.Kind == IdentityKind.Google ? identity.Label ?? "Conta Google" : Display(identity.Kind, identity.Value);

    private static string MaskEmail(string value)
    {
        var at = value.IndexOf('@');
        return at <= 1 ? value : value[0] + "••••" + value[at..];
    }

    // ─── Acesso ───────────────────────────────────────────────────────────────

    /// <summary>Bots do portal: os da conta e os que listam um WhatsApp dela em "Telefones do portal" (cadastro antigo).</summary>
    public IReadOnlyList<BotConfig> BotsFor(Account account) =>
        bots.Bots().Values
            .Where(b => account.Bots.Contains(b.TenantId, StringComparer.OrdinalIgnoreCase)
                     || b.PortalPhones.Any(p => account.Phones.Any(phone => PhoneNumbers.Matches(p, phone))))
            .OrderBy(b => b.DisplayName ?? b.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public BotConfig? BotFor(Account account, string tenantId) =>
        BotsFor(account).FirstOrDefault(b => string.Equals(b.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Para onde a pessoa vai depois de entrar.</summary>
    public string Destination(Account account)
    {
        var client = BotsFor(account).Count > 0;
        return (account.IsStaff, client) switch
        {
            (true, true) => "/entrar/escolher",
            (true, false) => "/",
            (false, true) => "/portal",
            _ => "/entrar/conta"
        };
    }

    /// <summary>
    /// Conta para este WhatsApp ou e-mail. Quem estava só em "Telefones do portal" ganha a conta na primeira entrada,
    /// sem ninguém precisar refazer o cadastro.
    /// </summary>
    public Account? FindForLogin(IdentityKind kind, string value)
    {
        if (accounts.FindByIdentity(kind, value) is { } existing) return existing;
        if (kind != IdentityKind.WhatsApp) return null;

        var legacy = bots.Bots().Values.Where(b => b.PortalPhones.Any(p => PhoneNumbers.Matches(p, value))).ToList();
        if (legacy.Count == 0) return null;

        logger.LogInformation("Conta criada para telefone já cadastrado no portal ({Phone})", Mask(value));
        return accounts.Add(new Account
        {
            Name = legacy[0].DisplayName ?? legacy[0].TenantId,
            Identities = [new AccountIdentity { Kind = IdentityKind.WhatsApp, Value = value }]
        });
    }

    // ─── Login por código ─────────────────────────────────────────────────────

    /// <param name="via">"email" = mandar para o e-mail da conta em vez do WhatsApp digitado ("Não chegou?").</param>
    public async Task<LoginStartResult> StartLoginAsync(string? identifier, string? via = null, CancellationToken ct = default)
    {
        if (Parse(identifier) is not { } parsed) return new(LoginStart.Invalid);
        if (FindForLogin(parsed.Kind, parsed.Value) is not { } account) return new(LoginStart.NoAccess);

        var (kind, target) = parsed;
        if (via == "email" && kind != IdentityKind.Email)
        {
            if (account.Identities.FirstOrDefault(i => i.Kind == IdentityKind.Email) is not { } mail) return new(LoginStart.EmailUnavailable);
            (kind, target) = (IdentityKind.Email, mail.Value);
        }

        return await SendCodeAsync($"login:{account.Id}", account, kind, target, ct);
    }

    /// <summary>Confere o código. Devolve a conta (já com o último acesso gravado) ou null.</summary>
    public Account? VerifyLogin(string? identifier, string? code)
    {
        if (Parse(identifier) is not { } parsed || accounts.FindByIdentity(parsed.Kind, parsed.Value) is not { } account) return null;
        if (CheckCode($"login:{account.Id}", code) is null) return null;
        return accounts.Update(account.Id, a => a.LastLoginAt = DateTimeOffset.UtcNow);
    }

    /// <summary>Adicionar WhatsApp ou e-mail a uma conta logada: manda o código para confirmar que é dela.</summary>
    public async Task<LoginStartResult> StartLinkAsync(Account account, string? identifier, CancellationToken ct = default)
    {
        if (Parse(identifier) is not { } parsed) return new(LoginStart.Invalid);
        if (accounts.FindByIdentity(parsed.Kind, parsed.Value) is { } owner)
            return owner.Id == account.Id ? new(LoginStart.AlreadyUsed, parsed.Kind) : new(LoginStart.AlreadyUsed);
        return await SendCodeAsync($"link:{account.Id}", account, parsed.Kind, parsed.Value, ct);
    }

    public Account? VerifyLink(Account account, string? code)
    {
        if (CheckCode($"link:{account.Id}", code) is not { } pending) return null;
        if (accounts.FindByIdentity(pending.Kind, pending.Value) is not null) return null;
        return accounts.Update(account.Id, a => a.Identities.Add(new AccountIdentity { Kind = pending.Kind, Value = pending.Value }));
    }

    private async Task<LoginStartResult> SendCodeAsync(string key, Account account, IdentityKind kind, string target, CancellationToken ct)
    {
        if (_pending.TryGetValue(key, out var current) && DateTimeOffset.UtcNow - current.SentAt < ResendInterval)
            return new(LoginStart.TooSoon, kind, Display(kind, target, mask: true));
        if (kind == IdentityKind.Email && !email.IsConfigured) return new(LoginStart.EmailUnavailable);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _pending[key] = new Pending { Hash = Hash(key, code), SentAt = DateTimeOffset.UtcNow, Kind = kind, Value = target };

        if (config.GetValue("Auth:LogCodes", false))
            logger.LogWarning("Auth:LogCodes ligado: código {Code} para {Target}. Desligue em produção.", code, Mask(target));

        var text = $"Seu código de acesso à Agiliz é {code}. Ele vale por 10 minutos. Se não foi você, ignore esta mensagem.";
        var sent = kind == IdentityKind.Email
            ? await TrySendEmailAsync(target, "Seu código de acesso à Agiliz", text, ct)
            : await runtime.NotifyAsync(target, text, BotsFor(account).FirstOrDefault()?.TenantId, ct);

        if (sent) return new(LoginStart.Sent, kind, Display(kind, target, mask: true));
        _pending.TryRemove(key, out _);
        return new(LoginStart.SendFailed, kind);
    }

    private Pending? CheckCode(string key, string? code)
    {
        if (!_pending.TryGetValue(key, out var pending)) return null;
        if (DateTimeOffset.UtcNow - pending.SentAt > CodeLifetime || pending.Attempts >= MaxAttempts)
        {
            _pending.TryRemove(key, out _);
            return null;
        }

        code = code?.Trim();
        if (code is not { Length: 6 } || !CryptographicOperations.FixedTimeEquals(pending.Hash, Hash(key, code)))
        {
            pending.Attempts++;
            logger.LogWarning("Código de acesso errado (tentativa {Attempt})", pending.Attempts);
            return null;
        }

        _pending.TryRemove(key, out _);
        return pending;
    }

    public async Task<bool> TrySendEmailAsync(string to, string subject, string body, CancellationToken ct)
    {
        if (!email.IsConfigured) return false;
        try
        {
            await email.SendEmailAsync(to, subject, body, ct);
            return true;
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(ex, "E-mail de acesso não enviado");
            return false;
        }
    }

    // ─── Formas de entrar ─────────────────────────────────────────────────────

    /// <summary>Tira uma forma de entrar. A última não sai (a pessoa ficaria trancada para fora).</summary>
    public Account? RemoveIdentity(Account account, IdentityKind kind, string value)
    {
        if (account.Identities.Count <= 1) return null;
        return accounts.Update(account.Id, a =>
        {
            if (a.Identities.Count > 1) a.Identities.RemoveAll(i => i.Kind == kind && i.Value == value);
        });
    }

    /// <summary>Entrou com Google: acha a conta pelo Google ou pelo mesmo e-mail (e já guarda o Google nela).</summary>
    public Account? SignInWithGoogle(string sub, string googleEmail)
    {
        if (accounts.FindByIdentity(IdentityKind.Google, sub) is { } bySub)
            return accounts.Update(bySub.Id, a => a.LastLoginAt = DateTimeOffset.UtcNow);

        if (accounts.FindByIdentity(IdentityKind.Email, googleEmail) is not { } byEmail) return null;
        return accounts.Update(byEmail.Id, a =>
        {
            a.Identities.Add(new AccountIdentity { Kind = IdentityKind.Google, Value = sub, Label = googleEmail });
            a.LastLoginAt = DateTimeOffset.UtcNow;
        });
    }

    /// <summary>Liga uma conta Google à conta logada. False = esse Google já é de outra conta.</summary>
    public bool LinkGoogle(Account account, string sub, string googleEmail)
    {
        if (accounts.FindByIdentity(IdentityKind.Google, sub) is { } owner) return owner.Id == account.Id;
        accounts.Update(account.Id, a => a.Identities.Add(new AccountIdentity { Kind = IdentityKind.Google, Value = sub, Label = googleEmail }));
        return true;
    }

    // ─── Sessão ───────────────────────────────────────────────────────────────

    public string CreateSession(Account account) =>
        _sessions.Protect(JsonSerializer.Serialize(new SessionPayload(account.Id, account.SessionVersion,
            DateTimeOffset.UtcNow.Add(SessionLifetime).ToUnixTimeSeconds())));

    /// <summary>Conta da sessão, ou null se o cookie for inválido, expirado, da versão antiga ou a conta não existir mais.</summary>
    public Account? ReadSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<SessionPayload>(_sessions.Unprotect(token));
            if (payload is null || DateTimeOffset.FromUnixTimeSeconds(payload.E) <= DateTimeOffset.UtcNow) return null;
            return accounts.Find(payload.A) is { } account && account.SessionVersion == payload.V ? account : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    public static void SignIn(HttpContext http, string token, IConfiguration config) =>
        http.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            // Lax: o cookie precisa valer na volta do Google e no link do convite (navegação vinda de fora).
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps || config.GetValue("Portal:SecureCookies", false),
            Path = "/",
            MaxAge = SessionLifetime
        });

    public static void SignOut(HttpContext http) =>
        http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });

    public Account? SignOutEverywhere(Account account) => accounts.Update(account.Id, a => a.SessionVersion++);

    // ─── Convites ─────────────────────────────────────────────────────────────

    /// <summary>Novo link de convite (o anterior deixa de valer). Devolve o token, que só existe aqui.</summary>
    public string CreateInvite(string accountId)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        accounts.Update(accountId, a => a.Invite = new AccountInvite
        {
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            ExpiresAt = DateTimeOffset.UtcNow.Add(InviteLifetime)
        });
        return token;
    }

    /// <summary>Usa o convite (uma vez só). Devolve a conta, ou null se o link for inválido ou vencido.</summary>
    public Account? AcceptInvite(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (accounts.FindByInviteHash(hash) is not { } account || account.Invite!.ExpiresAt < DateTimeOffset.UtcNow) return null;
        return accounts.Update(account.Id, a =>
        {
            a.Invite = null;
            a.LastLoginAt = DateTimeOffset.UtcNow;
        });
    }

    /// <summary>Manda o convite pelo WhatsApp e/ou e-mail da conta. Devolve onde saiu.</summary>
    public async Task<List<string>> SendInviteAsync(Account account, string link, CancellationToken ct = default)
    {
        var sentTo = new List<string>();
        var botName = BotsFor(account).FirstOrDefault() is { } bot ? bot.DisplayName ?? bot.TenantId : null;
        var intro = account.IsStaff
            ? $"Olá, {account.Name}! Você foi convidado(a) para o painel da Agiliz."
            : botName is null
                ? $"Olá, {account.Name}! Seu portal Agiliz está pronto."
                : $"Olá, {account.Name}! O portal do seu atendente virtual {botName} está pronto.";
        var text = $"{intro}\n\nToque no link para entrar. Ele vale por 7 dias e só funciona uma vez:\n{link}\n\n" +
                   "Depois, para entrar de novo, use este número, seu e-mail ou sua conta Google.";

        foreach (var identity in account.Identities)
        {
            var ok = identity.Kind switch
            {
                IdentityKind.WhatsApp => await runtime.NotifyAsync(identity.Value, text, BotsFor(account).FirstOrDefault()?.TenantId, ct),
                IdentityKind.Email => await TrySendEmailAsync(identity.Value, "Seu acesso à Agiliz", text, ct),
                _ => false
            };
            if (ok) sentTo.Add(Display(identity));
        }
        return sentTo;
    }

    // ─── Auxiliares ───────────────────────────────────────────────────────────

    /// <summary>Só caminhos locais ("/portal"), nunca outro site ("//x.com", "https://...").</summary>
    public static string? SafeReturn(string? path) =>
        path is { Length: > 0 and < 300 } && path[0] == '/' && !path.StartsWith("//") && !path.StartsWith("/\\") ? path : null;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Mask(string value) => value.Length <= 4 ? "••••" : new string('•', value.Length - 4) + value[^4..];

    private static byte[] Hash(string key, string code) => SHA256.HashData(Encoding.UTF8.GetBytes($"{key}:{code}"));
}
