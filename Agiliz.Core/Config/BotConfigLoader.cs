using System.Text.Json;
using System.Text.RegularExpressions;
using Agiliz.Core.Models;
using Microsoft.Extensions.Configuration;

namespace Agiliz.Core.Config;

/// <summary>
/// Lê e grava os configs dos bots. Layout da pasta de configs:
/// <code>
/// configs/
///   bots/{tenantId}.json      ← um arquivo por bot (só bots ficam aqui)
///   google_credentials.json   ← credenciais e outros arquivos do sistema ficam na raiz
///   telemetry.json
/// </code>
/// Todos os métodos recebem a pasta raiz de configs; a subpasta <c>bots</c> é resolvida aqui.
/// </summary>
public static partial class BotConfigLoader
{
    public const string BotsFolderName = "bots";

    /// <summary>Arquivos da raiz que nunca são bots, mesmo no layout antigo.</summary>
    private static readonly HashSet<string> SystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "billing", "telemetry", "google_credentials"
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex TenantIdPattern();

    /// <summary>Pasta de configs a partir de <c>ConfigsDir</c>; sem valor, usa <c>configs/</c> na raiz da solução.</summary>
    public static string ResolveConfigsDir(IConfiguration config)
    {
        var dir = config["ConfigsDir"];
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs");
        return Path.GetFullPath(dir);
    }

    public static string BotsDir(string configsDir) => Path.Combine(configsDir, BotsFolderName);

    /// <summary>Letras, números, '-' e '_' (até 64). Impede que um id vire caminho fora de configs/bots.</summary>
    public static bool IsValidTenantId(string? tenantId) =>
        !string.IsNullOrEmpty(tenantId) && TenantIdPattern().IsMatch(tenantId);

    /// <summary>Carrega o config de um cliente pelo tenantId.</summary>
    public static BotConfig Load(string configsDir, string tenantId)
    {
        var path = PathFor(configsDir, tenantId);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Config não encontrado: {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BotConfig>(json, JsonOpts)
               ?? throw new InvalidOperationException($"Falha ao desserializar {path}");
    }

    /// <summary>
    /// Salva ou sobrescreve o config de um cliente. A gravação é atômica (arquivo temporário + rename)
    /// para o Runtime nunca ler um JSON pela metade durante um reload.
    /// </summary>
    public static void Save(string configsDir, BotConfig config)
    {
        var path = PathFor(configsDir, config.TenantId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    public static bool Exists(string configsDir, string tenantId) =>
        IsValidTenantId(tenantId) && File.Exists(PathFor(configsDir, tenantId));

    /// <summary>Apaga o config. Retorna false se não existia.</summary>
    public static bool Delete(string configsDir, string tenantId)
    {
        if (!Exists(configsDir, tenantId)) return false;
        File.Delete(PathFor(configsDir, tenantId));
        return true;
    }

    /// <summary>Lista todos os tenantIds com config salvo.</summary>
    public static IEnumerable<string> ListTenants(string configsDir)
    {
        var botsDir = BotsDir(configsDir);
        if (!Directory.Exists(botsDir))
            return [];

        return Directory.GetFiles(botsDir, "*.json")
                        .Select(f => Path.GetFileNameWithoutExtension(f)!)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToList();
    }

    /// <summary>
    /// Move bots do layout antigo (configs/*.json) para configs/bots/. Só move arquivos que são
    /// um objeto JSON com "tenantId"; billing, telemetria e credenciais ficam onde estão.
    /// Idempotente: pode rodar a cada startup, inclusive em dois serviços ao mesmo tempo.
    /// </summary>
    /// <returns>TenantIds movidos.</returns>
    public static IReadOnlyList<string> MigrateLegacyLayout(string configsDir)
    {
        if (!Directory.Exists(configsDir)) return [];

        var moved = new List<string>();
        foreach (var file in Directory.GetFiles(configsDir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (SystemFiles.Contains(id) || !IsValidTenantId(id) || !LooksLikeBotConfig(file))
                continue;

            var target = Path.Combine(BotsDir(configsDir), $"{id}.json");
            if (File.Exists(target))
                continue; // já migrado; não sobrescreve a versão nova

            try
            {
                Directory.CreateDirectory(BotsDir(configsDir));
                File.Move(file, target);
                moved.Add(id);
            }
            catch (IOException)
            {
                // Outro serviço migrou no mesmo instante.
            }
        }
        return moved;
    }

    private static bool LooksLikeBotConfig(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("tenantId", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
    }

    private static string PathFor(string configsDir, string tenantId)
    {
        if (!IsValidTenantId(tenantId))
            throw new ArgumentException($"TenantId inválido: '{tenantId}'. Use letras, números, '-' ou '_'.", nameof(tenantId));
        return Path.Combine(BotsDir(configsDir), $"{tenantId}.json");
    }
}
