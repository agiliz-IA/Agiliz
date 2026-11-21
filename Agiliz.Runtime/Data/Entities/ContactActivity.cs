namespace Agiliz.Runtime.Data.Entities;

/// <summary>
/// Um contato que falou com o bot num mês (franquia do plano conta contatos diferentes por mês).
/// Chave: cliente + mês ("yyyy-MM", fuso de Brasília) + telefone.
/// </summary>
public class ContactActivity
{
    public string TenantId { get; set; } = "";
    public string Month { get; set; } = "";
    public string Phone { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int MessageCount { get; set; }
}
