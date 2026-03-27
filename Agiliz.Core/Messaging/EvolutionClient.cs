using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Agiliz.Core.Messaging;

/// <summary>
/// Cliente for Evolution API.
/// Lê Evolution:ApiUrl e Evolution:ApiToken do appsettings.
/// </summary>
public sealed class EvolutionClient : IMessageProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;

    public EvolutionClient(IConfiguration configuration)
    {
        var apiUrl = configuration["Evolution:ApiUrl"]
                     ?? throw new InvalidOperationException("Evolution:ApiUrl não encontrada.");

        var apiToken = configuration["Evolution:ApiToken"]
                       ?? throw new InvalidOperationException("Evolution:ApiToken não encontrada.");

        _apiUrl = apiUrl.TrimEnd('/');
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
    }

    public async Task SendAsync(string toNumber, string fromNumber, string body, CancellationToken ct = default)
    {
        var normalizedTo = NormalizeNumber(toNumber);
        var normalizedFrom = NormalizeNumber(fromNumber);

        var payload = new
        {
            number = normalizedTo, // destination
            textMessage = new { text = body }
        };

        var endpoint = $"{_apiUrl}/message/sendText/{normalizedFrom}";
        var response = await _httpClient.PostAsJsonAsync(endpoint, payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Evolution API error: {response.StatusCode}. Response: {content}");
        }
    }

    public string NormalizeNumber(string number)
    {
        // Remove prefixos comuns (whatsapp:, etc) e símbolos
        var cleaned = number
            .Replace("whatsapp:", "")
            .Replace("@s.whatsapp.net", "")
            .Replace("@g.us", "");

        return Regex.Replace(cleaned, @"\D", "");
    }
}
