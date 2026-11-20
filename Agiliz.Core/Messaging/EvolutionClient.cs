using Agiliz.Core.Media;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agiliz.Core.Messaging;

/// <summary>
/// Cliente da Evolution API v2.
/// Lê Evolution:ApiUrl (raiz do servidor, sem "/api"), Evolution:ApiToken (header "apikey")
/// e Evolution:UseNativeButtons (padrão false).
/// </summary>
public sealed class EvolutionClient : IMessageProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly bool _useNativeButtons;

    public EvolutionClient(IConfiguration configuration) : this(configuration, null) { }

    public EvolutionClient(IConfiguration configuration, HttpClient? httpClient)
    {
        var apiUrl = configuration["Evolution:ApiUrl"]
                     ?? throw new InvalidOperationException("Evolution:ApiUrl não encontrada.");

        var apiToken = configuration["Evolution:ApiToken"]
                       ?? throw new InvalidOperationException("Evolution:ApiToken não encontrada.");

        _apiUrl = apiUrl.TrimEnd('/');
        _useNativeButtons = bool.TryParse(configuration["Evolution:UseNativeButtons"], out var nb) && nb;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // A Evolution API autentica pelo header "apikey" (não Bearer).
        _httpClient.DefaultRequestHeaders.Remove("apikey");
        _httpClient.DefaultRequestHeaders.Add("apikey", apiToken);
    }

    public async Task SendAsync(string toNumber, string fromNumber, string body, CancellationToken ct = default)
    {
        var payload = new
        {
            number = NormalizeNumber(toNumber),
            text = body
        };

        await PostAsync($"/message/sendText/{NormalizeNumber(fromNumber)}", payload, ct);
    }

    public async Task SendButtonsAsync(string toNumber, string fromNumber, string body, string[] buttons, CancellationToken ct = default)
    {
        // Botões nativos não aparecem de forma confiável em conexões não oficiais (Baileys):
        // a API responde 200 mas o cliente não vê as opções. Por padrão mandamos as opções em texto.
        if (_useNativeButtons)
        {
            var payload = new
            {
                number = NormalizeNumber(toNumber),
                title = "",
                description = body,
                footer = "",
                buttons = buttons.Select((b, index) => new
                {
                    type = "reply",
                    displayText = b,
                    id = (index + 1).ToString()
                }).ToArray()
            };

            try
            {
                await PostAsync($"/message/sendButtons/{NormalizeNumber(fromNumber)}", payload, ct);
                return;
            }
            catch (InvalidOperationException)
            {
                // cai para o texto abaixo
            }
        }

        await SendAsync(toNumber, fromNumber, FormatOptionsAsText(body, buttons), ct);
    }

    /// <summary>"Texto\n\n1 - Confirmar\n2 - Remarcar" — o cliente responde com o número ou a palavra.</summary>
    public static string FormatOptionsAsText(string body, IReadOnlyList<string> options)
    {
        if (options.Count == 0) return body;
        var lines = options.Select((o, i) => $"{i + 1} - {o}");
        return $"{body}\n\nResponda com o número:\n{string.Join("\n", lines)}";
    }

    public async Task<MediaContent?> DownloadMediaAsync(string instance, string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return null;

        var payload = new { message = new { key = new { id = messageId } }, convertToMp4 = false };
        var response = await _httpClient.PostAsJsonAsync($"{_apiUrl}/chat/getBase64FromMediaMessage/{instance}", payload, ct);
        if (!response.IsSuccessStatusCode) return null;

        var media = await response.Content.ReadFromJsonAsync<Base64MediaResponse>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(media?.Base64)) return null;

        return new MediaContent(Convert.FromBase64String(media.Base64), media.Mimetype ?? "audio/ogg");
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

    private async Task PostAsync(string path, object payload, CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync($"{_apiUrl}{path}", payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Evolution API error: {response.StatusCode}. Response: {content}");
        }
    }

    private sealed record Base64MediaResponse(
        [property: JsonPropertyName("base64")] string? Base64,
        [property: JsonPropertyName("mimetype")] string? Mimetype);
}
