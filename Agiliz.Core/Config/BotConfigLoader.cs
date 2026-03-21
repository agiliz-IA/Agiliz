using System.Text.Json;
using Agiliz.Core.Models;

namespace Agiliz.Core.Config;

public static class BotConfigLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Carrega o config de um cliente pelo tenantId.</summary>
    public static BotConfig Load(string configsDir, string tenantId)
    {
        var path = Path.Combine(configsDir, $"{tenantId}.json");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Config não encontrado: {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BotConfig>(json, JsonOpts)
               ?? throw new InvalidOperationException($"Falha ao desserializar {path}");
    }

    /// <summary>Salva ou sobrescreve o config de um cliente.</summary>
    public static void Save(string configsDir, BotConfig config)
    {
        Directory.CreateDirectory(configsDir);
        var path = Path.Combine(configsDir, $"{config.TenantId}.json");
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(path, json);
    }

    /// <summary>Lista todos os tenantIds com config salvo.</summary>
    public static IEnumerable<string> ListTenants(string configsDir)
    {
        if (!Directory.Exists(configsDir))
            return [];

        return Directory.GetFiles(configsDir, "*.json")
                        .Select(f => Path.GetFileNameWithoutExtension(f)!);
    }
}
