using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.LLM;

namespace Agiliz.Core.Media;

/// <summary>Arquivo de mídia baixado do WhatsApp.</summary>
public sealed record MediaContent(byte[] Data, string MimeType);

/// <summary>Transcreve áudios (mensagens de voz) para texto.</summary>
public interface IAudioTranscriber
{
    bool IsEnabled { get; }

    /// <returns>Texto transcrito, ou null se não foi possível transcrever.</returns>
    Task<string?> TranscribeAsync(MediaContent audio, CancellationToken ct = default);
}

/// <summary>
/// Transcrição via Whisper no Groq (whisper-large-v3-turbo, US$ 0,04 por hora de áudio).
/// Um áudio de 30 s custa ~US$ 0,0003. Lê a chave de GROQ_API_KEY.
/// </summary>
public sealed class GroqWhisperTranscriber : IAudioTranscriber
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly bool _enabled;

    public GroqWhisperTranscriber(HttpClient? http = null, string? apiKey = null, string? baseUrl = null, bool enabled = true)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        apiKey ??= Environment.GetEnvironmentVariable("GROQ_API_KEY");
        _enabled = enabled && !string.IsNullOrWhiteSpace(apiKey);
        if (_enabled)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _endpoint = $"{(baseUrl ?? "https://api.groq.com").TrimEnd('/')}/openai/v1/audio/transcriptions";
    }

    public bool IsEnabled => _enabled;

    public async Task<string?> TranscribeAsync(MediaContent audio, CancellationToken ct = default)
    {
        if (!_enabled || audio.Data.Length == 0) return null;

        var mime = string.IsNullOrWhiteSpace(audio.MimeType) ? "audio/ogg" : audio.MimeType.Split(';')[0].Trim();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio.Data);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", "audio" + ExtensionFor(mime));
        form.Add(new StringContent(ModelCatalog.WhisperTurboModel), "model");
        form.Add(new StringContent("pt"), "language");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");

        using var response = await _http.PostAsync(_endpoint, form, ct);
        if (!response.IsSuccessStatusCode) return null;

        var result = await response.Content.ReadFromJsonAsync<TranscriptionResponse>(cancellationToken: ct);
        var text = result?.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string ExtensionFor(string mime) => mime switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/webm" => ".webm",
        _ => ".ogg" // notas de voz do WhatsApp são ogg/opus
    };

    private sealed record TranscriptionResponse([property: JsonPropertyName("text")] string? Text);
}
