using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agiliz.Core.Config;

/// <summary>
/// Um arquivo JSON por item numa subpasta de configs (ex.: configs/plans/{id}.json).
/// Mesmo padrão dos bots: ids validados (sem caminho) e gravação atômica.
/// </summary>
public sealed class JsonFileStore<T> where T : class
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonFileStore(string configsDir, string folderName)
    {
        Dir = Path.Combine(configsDir, folderName);
    }

    public string Dir { get; }

    public bool Exists(string id) => BotConfigLoader.IsValidTenantId(id) && File.Exists(PathFor(id));

    public T? Load(string id)
    {
        if (!Exists(id)) return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(PathFor(id)), JsonOpts);
    }

    public void Save(string id, T item)
    {
        var path = PathFor(id);
        Directory.CreateDirectory(Dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(item, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    public bool Delete(string id)
    {
        if (!Exists(id)) return false;
        File.Delete(PathFor(id));
        return true;
    }

    /// <summary>Todos os itens legíveis (arquivo corrompido é ignorado).</summary>
    public IReadOnlyList<T> List()
    {
        if (!Directory.Exists(Dir)) return [];
        var items = new List<T>();
        foreach (var file in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOpts) is { } item)
                    items.Add(item);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return items;
    }

    private string PathFor(string id)
    {
        if (!BotConfigLoader.IsValidTenantId(id))
            throw new ArgumentException($"Id inválido: '{id}'. Use letras, números, '-' ou '_'.", nameof(id));
        return Path.Combine(Dir, $"{id}.json");
    }
}
