using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.Billing;
using Agiliz.Core.Models;

namespace Agiliz.Wizard.Services;

public sealed record RuntimeCallResult(bool Ok, string? Error = null);

public sealed record RuntimeConversation(string Phone, string? Name, bool IsPaused, DateTimeOffset? PausedAt, DateTimeOffset LastActivityAt, string? LastMessagePreview);

public sealed record RuntimeMessage(string Role, string Text);

public sealed record RuntimeMessages(bool IsPaused, List<RuntimeMessage> Messages);

public sealed record RuntimeAppointment(Guid Id, string Phone, string? Name, DateTimeOffset ScheduledTime, int DurationMinutes, string Status, DateTimeOffset? ConfirmedAt);

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

    /// <summary>Botão "Testar conexão": o teste roda no Runtime, que tem as credenciais. Não lança exceção.</summary>
    public async Task<Agiliz.Core.Integrations.IntegrationTestResult> TestIntegrationAsync(
        string integrationId, string tenantId, Dictionary<string, string>? fields, CancellationToken ct = default)
    {
        try
        {
            var request = new Agiliz.Core.Integrations.IntegrationTestRequest { TenantId = tenantId, Fields = fields };
            using var response = await CreateClient().PostAsJsonAsync($"api/admin/integrations/{Uri.EscapeDataString(integrationId)}/test", request, JsonOpts, ct);
            if (!response.IsSuccessStatusCode)
                return new(false, $"O Runtime respondeu HTTP {(int)response.StatusCode}: {Shorten(await response.Content.ReadAsStringAsync(ct))}");
            return await response.Content.ReadFromJsonAsync<Agiliz.Core.Integrations.IntegrationTestResult>(JsonOpts, ct)
                   ?? new(false, "Resposta vazia do Runtime.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, $"Runtime inacessível: {ex.Message}");
        }
    }

    /// <summary>Dados do servidor para a aba Integrações (ex.: e-mail da conta de serviço do Google). Null se o Runtime não responder.</summary>
    public async Task<Agiliz.Core.Integrations.IntegrationServerInfo?> GetIntegrationInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await CreateClient().GetAsync("api/admin/integrations/info", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<Agiliz.Core.Integrations.IntegrationServerInfo>(JsonOpts, ct)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Runtime inacessível para info de integrações");
            return null;
        }
    }

    /// <summary>Contatos por cliente no mês ("yyyy-MM"). Lança exceção se o Runtime não responder.</summary>
    public async Task<IReadOnlyList<Agiliz.Core.Plans.TenantUsage>> GetUsageAsync(string month, CancellationToken ct = default)
    {
        using var response = await CreateClient().GetAsync($"api/admin/usage?month={Uri.EscapeDataString(month)}", ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Shorten(await response.Content.ReadAsStringAsync(ct))}");

        var body = await response.Content.ReadFromJsonAsync<UsageResponse>(JsonOpts, ct);
        return body?.Tenants ?? [];
    }

    private sealed record UsageResponse(string Month, List<Agiliz.Core.Plans.TenantUsage> Tenants);

    // ─── Dados de um cliente (portal) ────────────────────────────────────────

    private static string TenantPath(string tenantId) => $"api/admin/tenants/{Uri.EscapeDataString(tenantId)}";

    /// <summary>Conversas recentes do cliente. Lança exceção se o Runtime não responder.</summary>
    public async Task<IReadOnlyList<RuntimeConversation>> GetConversationsAsync(string tenantId, int limit = 50, CancellationToken ct = default) =>
        await GetJsonAsync<List<RuntimeConversation>>($"{TenantPath(tenantId)}/conversations?limit={limit}", ct) ?? [];

    public Task<RuntimeMessages?> GetMessagesAsync(string tenantId, string phone, CancellationToken ct = default) =>
        GetJsonAsync<RuntimeMessages>($"{TenantPath(tenantId)}/conversations/{Uri.EscapeDataString(phone)}/messages", ct);

    public async Task<IReadOnlyList<RuntimeAppointment>> GetAppointmentsAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        await GetJsonAsync<List<RuntimeAppointment>>(
            $"{TenantPath(tenantId)}/appointments?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}", ct) ?? [];

    public Task<bool> PauseConversationAsync(string tenantId, string phone, CancellationToken ct = default) =>
        PostOkAsync($"{TenantPath(tenantId)}/conversations/{Uri.EscapeDataString(phone)}/pause", null, ct);

    public Task<bool> ResumeConversationAsync(string tenantId, string phone, CancellationToken ct = default) =>
        PostOkAsync($"{TenantPath(tenantId)}/conversations/{Uri.EscapeDataString(phone)}/resume", null, ct);

    public Task<bool> ReplyAsync(string tenantId, string phone, string body, CancellationToken ct = default) =>
        PostOkAsync($"{TenantPath(tenantId)}/conversations/{Uri.EscapeDataString(phone)}/reply", new { body }, ct);

    public Task<bool> SendPortalCodeAsync(string tenantId, string phone, string code, CancellationToken ct = default) =>
        PostOkAsync($"{TenantPath(tenantId)}/portal-code", new { phone, code }, ct);

    public async Task<Agiliz.Core.Messaging.WhatsAppPairingResult> RequestPairingAsync(string tenantId, CancellationToken ct = default)
    {
        try
        {
            using var response = await CreateClient().PostAsync($"{TenantPath(tenantId)}/whatsapp/pairing", null, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<Agiliz.Core.Messaging.WhatsAppPairingResult>(JsonOpts, ct) ?? new(false, null, "Resposta vazia.")
                : new(false, null, $"O servidor respondeu HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, null, $"Servidor indisponível: {ex.Message}");
        }
    }

    public Task<bool> ResetDemoAsync(string tenantId, CancellationToken ct = default) =>
        PostOkAsync($"{TenantPath(tenantId)}/demo/reset", null, ct);

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await CreateClient().GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Shorten(await response.Content.ReadAsStringAsync(ct))}");
        return await response.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    /// <summary>POST que só precisa saber se deu certo. Não lança exceção.</summary>
    private async Task<bool> PostOkAsync(string path, object? body, CancellationToken ct)
    {
        try
        {
            using var response = body is null
                ? await CreateClient().PostAsync(path, null, ct)
                : await CreateClient().PostAsJsonAsync(path, body, JsonOpts, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Runtime recusou {Path}: HTTP {Status}", path, (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Runtime inacessível em {Path}", path);
            return false;
        }
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
