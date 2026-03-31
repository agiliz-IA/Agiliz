using System.Text.Json;
using Agiliz.Core.Models;

namespace Agiliz.Core.Billing;

public static class BillingStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static void Record(string configsDir, CostEntry entry)
    {
        var dir = Path.GetFullPath(configsDir);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        
        var path = Path.Combine(dir, "billing.json");
        var list = new List<CostEntry>();
        
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    list = JsonSerializer.Deserialize<List<CostEntry>>(json, JsonOpts) ?? new List<CostEntry>();
            }
            catch { }
        }
        
        list.Add(entry);
        File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOpts));
    }

    public static List<CostEntry> GetEntries(string configsDir)
    {
        var path = Path.Combine(Path.GetFullPath(configsDir), "billing.json");
        if (!File.Exists(path)) return new List<CostEntry>();
        
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<CostEntry>>(json, JsonOpts) ?? new List<CostEntry>();
        }
        catch { return new List<CostEntry>(); }
    }
}
