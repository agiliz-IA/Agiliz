using Agiliz.Core.Messaging;
using Agiliz.Wizard.Services;

namespace Agiliz.Wizard.Accounts;

public sealed record InviteRequest(string? Name, StaffRole? Role, string? Identifiers, IReadOnlyCollection<string> Bots);

public sealed record InviteOutcome(bool Ok, string? Error = null, Account? Account = null, string? Link = null, IReadOnlyList<string>? SentTo = null)
{
    public static InviteOutcome Fail(string error) => new(false, error);
}

/// <summary>
/// Tela "Acessos" do painel: convidar, mudar acesso, reenviar convite e tirar acesso.
/// Vendedor só mexe em clientes; equipe (Admin e Vendedor) só um Admin cria ou muda.
/// </summary>
public sealed class AccessService(AccountStore accounts, AuthService auth, BotIntegrationService bots, IConfiguration config)
{
    /// <summary>Quem só estava em "Telefones do portal" aparece na lista como conta (sem mexer nos bots).</summary>
    public void ImportLegacyPortalPhones()
    {
        foreach (var phone in bots.Bots().Values.SelectMany(b => b.PortalPhones).Select(PhoneNumbers.Normalize).Distinct())
            auth.FindForLogin(IdentityKind.WhatsApp, phone);
    }

    public string InviteLink(string baseUrl, string token)
    {
        var root = config["Auth:PublicUrl"] is { Length: > 0 } configured ? configured : baseUrl;
        return root.TrimEnd('/') + "/entrar/convite/" + token;
    }

    public async Task<InviteOutcome> InviteAsync(InviteRequest request, PainelUser actor, string baseUrl, CancellationToken ct = default)
    {
        if (request.Role is not null && !actor.IsAdmin) return InviteOutcome.Fail("Só um Admin convida alguém para a equipe.");
        if (string.IsNullOrWhiteSpace(request.Name)) return InviteOutcome.Fail("Informe o nome.");

        var identities = new List<AccountIdentity>();
        foreach (var raw in (request.Identifiers ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AuthService.Parse(raw) is not { } parsed) return InviteOutcome.Fail($"\"{raw}\" não é um WhatsApp com DDD nem um e-mail.");
            if (accounts.FindByIdentity(parsed.Kind, parsed.Value) is { } owner)
                return InviteOutcome.Fail($"{AuthService.Display(parsed.Kind, parsed.Value)} já é de {owner.Name}. Edite o acesso dessa pessoa em vez de convidar de novo.");
            if (identities.All(i => !AccountStore.Same(i, parsed.Kind, parsed.Value)))
                identities.Add(new AccountIdentity { Kind = parsed.Kind, Value = parsed.Value });
        }
        if (identities.Count == 0) return InviteOutcome.Fail("Informe pelo menos um WhatsApp ou e-mail.");

        var known = bots.Bots();
        var selected = request.Bots.Where(known.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (request.Role is null && selected.Count == 0) return InviteOutcome.Fail("Escolha pelo menos um bot para o cliente ver no portal.");

        var account = accounts.Add(new Account
        {
            Name = request.Name.Trim(),
            Role = request.Role,
            Bots = selected,
            Identities = identities
        });
        return await SendInviteAsync(account.Id, baseUrl, ct);
    }

    /// <summary>Gera um link novo (o anterior deixa de valer) e manda pelo WhatsApp e/ou e-mail da pessoa.</summary>
    public async Task<InviteOutcome> SendInviteAsync(string accountId, string baseUrl, CancellationToken ct = default)
    {
        if (accounts.Find(accountId) is null) return InviteOutcome.Fail("Conta não encontrada.");
        var link = InviteLink(baseUrl, auth.CreateInvite(accountId));
        var account = accounts.Find(accountId)!;
        var sentTo = await auth.SendInviteAsync(account, link, ct);
        return new InviteOutcome(true, Account: account, Link: link, SentTo: sentTo);
    }

    public async Task<string?> UpdateAsync(string accountId, string? name, StaffRole? role, IReadOnlyCollection<string> selectedBots, PainelUser actor, CancellationToken ct = default)
    {
        if (accounts.Find(accountId) is not { } account) return "Conta não encontrada.";
        if (!actor.IsAdmin && (account.IsStaff || role is not null)) return "Só um Admin muda o acesso da equipe.";
        if (string.IsNullOrWhiteSpace(name)) return "Informe o nome.";
        if (account.Role == StaffRole.Admin && role != StaffRole.Admin && accounts.All().Count(a => a.Role == StaffRole.Admin) == 1)
            return "Esta é a única conta Admin. Promova outra pessoa antes.";

        var known = bots.Bots();
        var keep = selectedBots.Where(known.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        accounts.Update(accountId, a =>
        {
            a.Name = name.Trim();
            a.Role = role;
            a.Bots = keep;
        });
        return await RemoveLegacyPhonesAsync(account, keep, ct);
    }

    public async Task<string?> RevokeAsync(string accountId, PainelUser actor, CancellationToken ct = default)
    {
        if (accounts.Find(accountId) is not { } account) return null;
        if (account.IsStaff && !actor.IsAdmin) return "Só um Admin tira o acesso da equipe.";
        if (account.Id == actor.Account?.Id) return "Você não pode tirar o próprio acesso.";
        if (account.Role == StaffRole.Admin && accounts.All().Count(a => a.Role == StaffRole.Admin) == 1)
            return "Esta é a única conta Admin.";

        accounts.Remove(accountId);
        // Sem isso, o telefone ainda listado em "Telefones do portal" ganharia uma conta nova na próxima entrada.
        return await RemoveLegacyPhonesAsync(account, [], ct);
    }

    /// <summary>Tira os telefones da conta de "Telefones do portal" dos bots que ela não deve mais ver.</summary>
    private async Task<string?> RemoveLegacyPhonesAsync(Account account, IReadOnlyCollection<string> keepBots, CancellationToken ct)
    {
        foreach (var bot in bots.Bots().Values)
        {
            if (keepBots.Contains(bot.TenantId, StringComparer.OrdinalIgnoreCase)) continue;
            var remaining = bot.PortalPhones.Where(p => !account.Phones.Any(phone => PhoneNumbers.Matches(p, phone))).ToList();
            if (remaining.Count == bot.PortalPhones.Count) continue;

            var outcome = await bots.SetPortalSettingsAsync(bot.TenantId, bot.DisplayName, remaining, bot.IsDemo, null, ct);
            if (!outcome.Ok) return $"Acesso salvo, mas não consegui atualizar o bot {bot.TenantId}: {outcome.Error}";
        }
        return null;
    }
}
