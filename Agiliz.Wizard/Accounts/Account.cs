namespace Agiliz.Wizard.Accounts;

/// <summary>Papel na equipe Agiliz. Sem papel = só cliente (portal).</summary>
public enum StaffRole { Admin, Vendedor }

public enum IdentityKind { WhatsApp, Email, Google }

/// <summary>Uma forma de entrar na conta. <see cref="Value"/> já normalizado (telefone só dígitos, e-mail minúsculo, Google = "sub").</summary>
public sealed class AccountIdentity
{
    public IdentityKind Kind { get; set; }
    public string Value { get; set; } = "";
    /// <summary>O que mostrar na tela (o e-mail da conta Google, por exemplo).</summary>
    public string? Label { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountInvite
{
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Uma pessoa: funcionário (painel), cliente (portal) ou os dois. Sem senha: entra por qualquer
/// uma das <see cref="Identities"/>. Ninguém cria conta sozinho; a equipe convida.
/// </summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public StaffRole? Role { get; set; }
    /// <summary>Bots que a pessoa vê no portal (além dos que listam o WhatsApp dela em "Telefones do portal").</summary>
    public List<string> Bots { get; set; } = [];
    public List<AccountIdentity> Identities { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>Sobe a cada "Sair de todos os aparelhos": invalida as sessões antigas.</summary>
    public int SessionVersion { get; set; }
    /// <summary>Já viu a tela "Proteja sua conta" (só aparece uma vez).</summary>
    public bool ProtectPromptSeen { get; set; }
    public AccountInvite? Invite { get; set; }

    public bool IsStaff => Role is not null;

    public IEnumerable<string> Phones => Identities.Where(i => i.Kind == IdentityKind.WhatsApp).Select(i => i.Value);
}
