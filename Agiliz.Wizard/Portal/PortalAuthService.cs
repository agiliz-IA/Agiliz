using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agiliz.Core.Models;
using Agiliz.Wizard.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Agiliz.Wizard.Portal;

public enum PortalLoginStart { Sent, UnknownPhone, TooSoon, SendFailed }

/// <summary>
/// Login do portal do cliente: código de 6 dígitos pelo WhatsApp, sem senha.
/// Quem pode entrar está em BotConfig.PortalPhones (o admin cadastra); tirar o telefone de lá corta o acesso
/// na hora, porque a sessão guarda só o telefone e as permissões são lidas a cada requisição.
/// </summary>
public sealed class PortalAuthService(BotIntegrationService bots, RuntimeApiClient runtime, IDataProtectionProvider protection, ILogger<PortalAuthService> logger)
{
    public const string CookieName = "agiliz_portal";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    public const int MaxAttempts = 5;

    private sealed class Pending
    {
        public required byte[] Hash { get; init; }
        public required DateTimeOffset SentAt { get; init; }
        public int Attempts { get; set; }
    }

    private sealed record SessionPayload(string P, long E);

    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly IDataProtector _sessions = protection.CreateProtector("agiliz.portal.session.v1");
    private readonly IDataProtector _ids = protection.CreateProtector("agiliz.portal.conversation.v1");

    /// <summary>Só dígitos; número brasileiro sem DDI ganha 55.</summary>
    public static string NormalizePhone(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length is 10 or 11 ? "55" + digits : digits;
    }

    /// <summary>Bots que o telefone pode ver no portal.</summary>
    public IReadOnlyList<BotConfig> BotsFor(string phone)
    {
        var normalized = NormalizePhone(phone);
        return bots.Bots().Values
            .Where(b => b.PortalPhones.Any(p => NormalizePhone(p) == normalized))
            .OrderBy(b => b.DisplayName ?? b.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public BotConfig? BotFor(string phone, string tenantId) =>
        BotsFor(phone).FirstOrDefault(b => string.Equals(b.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

    public async Task<PortalLoginStart> StartAsync(string? phoneInput, CancellationToken ct = default)
    {
        var phone = NormalizePhone(phoneInput);
        if (phone.Length < 12) return PortalLoginStart.UnknownPhone;

        var allowed = BotsFor(phone);
        if (allowed.Count == 0) return PortalLoginStart.UnknownPhone;

        if (_pending.TryGetValue(phone, out var current) && DateTimeOffset.UtcNow - current.SentAt < ResendInterval)
            return PortalLoginStart.TooSoon;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _pending[phone] = new Pending { Hash = Hash(phone, code), SentAt = DateTimeOffset.UtcNow };

        if (await runtime.SendPortalCodeAsync(allowed[0].TenantId, phone, code, ct))
            return PortalLoginStart.Sent;

        _pending.TryRemove(phone, out _);
        return PortalLoginStart.SendFailed;
    }

    /// <summary>Confere o código e devolve o token da sessão (null = código errado, expirado ou tentativas esgotadas).</summary>
    public string? Verify(string? phoneInput, string? code)
    {
        var phone = NormalizePhone(phoneInput);
        if (!_pending.TryGetValue(phone, out var pending)) return null;

        if (DateTimeOffset.UtcNow - pending.SentAt > CodeLifetime || pending.Attempts >= MaxAttempts)
        {
            _pending.TryRemove(phone, out _);
            return null;
        }

        var ok = code is { Length: 6 } && CryptographicOperations.FixedTimeEquals(pending.Hash, Hash(phone, code));
        if (!ok)
        {
            pending.Attempts++;
            logger.LogWarning("Código do portal errado para {Phone} (tentativa {Attempt})", Mask(phone), pending.Attempts);
            return null;
        }

        _pending.TryRemove(phone, out _);
        var payload = new SessionPayload(phone, DateTimeOffset.UtcNow.Add(SessionLifetime).ToUnixTimeSeconds());
        return _sessions.Protect(JsonSerializer.Serialize(payload));
    }

    /// <summary>Telefone da sessão, ou null se o cookie for inválido ou expirado.</summary>
    public string? ReadSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<SessionPayload>(_sessions.Unprotect(token));
            return payload is not null && DateTimeOffset.FromUnixTimeSeconds(payload.E) > DateTimeOffset.UtcNow ? payload.P : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Id opaco de conversa: o telefone do contato não aparece na URL.</summary>
    public string ConversationId(string tenantId, string contactPhone) => _ids.Protect($"{tenantId}|{contactPhone}");

    public string? ContactFromId(string tenantId, string id)
    {
        try
        {
            var parts = _ids.Unprotect(id).Split('|', 2);
            return parts.Length == 2 && string.Equals(parts[0], tenantId, StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>"+55 (21) 98888-7777"; no bot de demonstração, "+55 (21) •••••-•777".</summary>
    public static string DisplayPhone(string phone, bool mask)
    {
        var d = new string(phone.Where(char.IsDigit).ToArray());
        if (d.Length is not (12 or 13) || !d.StartsWith("55")) return mask ? Mask(d) : d;
        var ddd = d[2..4];
        var number = d[4..];
        var head = number[..^4];
        var tail = number[^4..];
        return mask
            ? $"+55 ({ddd}) {new string('•', head.Length)}-•{tail[1..]}"
            : $"+55 ({ddd}) {head}-{tail}";
    }

    private static string Mask(string phone) => phone.Length <= 4 ? "••••" : new string('•', phone.Length - 4) + phone[^4..];

    private static byte[] Hash(string phone, string code) => SHA256.HashData(Encoding.UTF8.GetBytes($"{phone}:{code}"));
}
