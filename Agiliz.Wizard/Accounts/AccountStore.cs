using System.Text.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.Messaging;

namespace Agiliz.Wizard.Accounts;

/// <summary>
/// Contas em configs/accounts.json (configs/ não vai para o git). Poucas dezenas de contas:
/// tudo fica em memória e cada mudança regrava o arquivo inteiro, de forma atômica.
/// Quem lê recebe cópias; para mudar, use <see cref="Update"/>, <see cref="Add"/> ou <see cref="Remove"/>.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _path;
    private List<Account> _accounts;

    public AccountStore(string configsDir)
    {
        _path = Path.Combine(configsDir, "accounts.json");
        _accounts = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_path), Json) ?? []
            : [];
    }

    public IReadOnlyList<Account> All()
    {
        lock (_gate) return _accounts.Select(Clone).ToList();
    }

    public Account? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) return _accounts.FirstOrDefault(a => a.Id == id) is { } a ? Clone(a) : null;
    }

    public Account? FindByIdentity(IdentityKind kind, string value)
    {
        lock (_gate) return _accounts.FirstOrDefault(a => a.Identities.Any(i => Same(i, kind, value))) is { } a ? Clone(a) : null;
    }

    public Account? FindByInviteHash(string hash)
    {
        lock (_gate) return _accounts.FirstOrDefault(a => a.Invite?.TokenHash == hash) is { } a ? Clone(a) : null;
    }

    public bool AnyAdmin()
    {
        lock (_gate) return _accounts.Any(a => a.Role == StaffRole.Admin);
    }

    public Account Add(Account account)
    {
        lock (_gate)
        {
            _accounts.Add(Clone(account));
            Save();
            return Clone(account);
        }
    }

    /// <summary>Muda a conta e grava. Devolve a conta atualizada, ou null se ela não existe mais.</summary>
    public Account? Update(string id, Action<Account> change)
    {
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == id);
            if (account is null) return null;
            change(account);
            Save();
            return Clone(account);
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _accounts.RemoveAll(a => a.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>Mesma forma de entrar? WhatsApp aceita o número com e sem o nono dígito.</summary>
    public static bool Same(AccountIdentity identity, IdentityKind kind, string value) =>
        identity.Kind == kind && (kind == IdentityKind.WhatsApp
            ? PhoneNumbers.Matches(identity.Value, value)
            : string.Equals(identity.Value, value, StringComparison.OrdinalIgnoreCase));

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_accounts, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static Account Clone(Account a) => JsonSerializer.Deserialize<Account>(JsonSerializer.Serialize(a, Json), Json)!;
}
