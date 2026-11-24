namespace Agiliz.Core.Messaging;

/// <summary>Comparação de telefones do WhatsApp no formato brasileiro.</summary>
public static class PhoneNumbers
{
    /// <summary>Só dígitos; número brasileiro sem DDI (10 ou 11 dígitos) ganha 55.</summary>
    public static string Normalize(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length is 10 or 11 ? "55" + digits : digits;
    }

    /// <summary>
    /// Mesmo número? Contas antigas de celular brasileiro chegam do WhatsApp sem o nono dígito
    /// (55 21 8888-7777 em vez de 55 21 98888-7777), então as duas formas valem.
    /// </summary>
    public static bool Matches(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y || WithoutNinthDigit(x) == WithoutNinthDigit(y);
    }

    private static string WithoutNinthDigit(string n) =>
        n.Length == 13 && n.StartsWith("55") && n[4] == '9' ? n.Remove(4, 1) : n;
}
