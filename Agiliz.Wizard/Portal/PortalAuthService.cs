using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Agiliz.Wizard.Portal;

/// <summary>
/// Ajudantes do portal: id opaco de conversa (o telefone do contato não aparece na URL) e telefone formatado.
/// O login fica na entrada única (<see cref="Agiliz.Wizard.Accounts.AuthService"/>).
/// </summary>
public sealed class PortalAuthService(IDataProtectionProvider protection)
{
    private readonly IDataProtector _ids = protection.CreateProtector("agiliz.portal.conversation.v1");

    /// <summary>Só dígitos; número brasileiro sem DDI ganha 55.</summary>
    public static string NormalizePhone(string? phone) => Agiliz.Core.Messaging.PhoneNumbers.Normalize(phone);

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
}
