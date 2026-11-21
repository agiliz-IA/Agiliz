using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.Billing;
using Agiliz.Core.Models;

namespace Agiliz.Wizard.Services;

public sealed record RuntimeCallResult(bool Ok, string? Error = null);

/// <summary>
/// Chamadas do painel para o Runtime (reload de bots, billing, handoff).
/// Base em <c>Runtime:ApiUrl</c>; envia <c>Runtime:AdminToken</c> no header <see cref="AdminTokenHeader"/>.
/// </summary>
public sealed class RuntimeApiClient(IHttpClientFactory httpFactory, ILogger<RuntimeApiClient> logger)
{
    public const string HttpClientName = "runtime";
    public const string AdminTokenHeader = "X-Admin-Token";

    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Registra o HttpClient nomeado e o serviço.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            client.BaseAddress = new Uri((config["Runtime:ApiUrl"] ?? "http://localhost:5000").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);

            var token = config["Runtime:AdminToken"];
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Add(AdminTokenHeader, token);
        });
        services.AddSingleton<RuntimeApiClient>();
    }

    public HttpClient CreateClient() => httpFactory.CreateClient(HttpClientName);

    /// <summary>Pede ao Runtime para reler configs/bots. Não lança exceção.</summary>
    public async Task<RuntimeCallResult> ReloadAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await CreateClient().PostAsync("api/admin/reload", null, ct);
            if (response.IsSuccessStatusCode) return new(true);

            var error = $"HTTP {(int)response.StatusCode}: {Shorten(await response.Content.ReadAsStringAsync(ct))}";
            logger.LogWarning("Runtime recusou o reload: {Error}", error);
            return new(false, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Runtime inacessível para reload");
            return new(false, ex.Message);
        }
    }

    /// <summary>Relatório de custos do período. Lança exceção se o Runtime não responder.</summary>
    public async Task<BillingReport> GetBillingAsync(DateTimeOffset? from, DateTimeOffset? to, int limit = 500, CancellationToken ct = default)
    {
        var query = new List<string> { $"limit={limit}" };
        if (from is not null) query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to is not null) query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");

        using var response = await CreateClient().GetAsync($"api/admin/billing?{string.Join('&', query)}", ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Shorten(await response.Content.ReadAsStringAsync(ct))}");

        return await response.Content.ReadFromJsonAsync<BillingReport>(JsonOpts, ct) ?? new BillingReport();
    }

    /// <summary>Envia um custo do painel (ex.: meta-agente) para o billing do Runtime. Não lança exceção.</summary>
    public async Task RecordCostAsync(CostEntry entry, CancellationToken ct = default)
    {
        try
        {
            using var response = await CreateClient().PostAsJsonAsync("api/admin/billing", entry, JsonOpts, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Runtime recusou o custo '{Description}' (US$ {Amount}): HTTP {Status}",
                    entry.Description, entry.AmountUsd, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Custo '{Description}' (US$ {Amount}) não registrado: Runtime inacessível",
                entry.Description, entry.AmountUsd);
        }
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
