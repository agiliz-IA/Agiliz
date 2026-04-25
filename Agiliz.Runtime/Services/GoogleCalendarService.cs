using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace Agiliz.Runtime.Services;

public sealed class GoogleCalendarService
{
    private readonly CalendarService? _service;
    private readonly ILogger<GoogleCalendarService> _logger;

    public GoogleCalendarService(IConfiguration config, ILogger<GoogleCalendarService> logger)
    {
        _logger = logger;
        
        var configsDir = config["ConfigsDir"] ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"));
        var keyPath = Path.Combine(configsDir, "google_credentials.json");

        if (!File.Exists(keyPath))
        {
            _logger.LogWarning("Arquivo google_credentials.json não encontrado no diretório {KeyPath}. O Google Calendar ficará offline.", keyPath);
            return;
        }

        try 
        {
            GoogleCredential credential;
            using (var stream = new FileStream(keyPath, FileMode.Open, FileAccess.Read))
            {
                credential = GoogleCredential.FromStream(stream).CreateScoped(CalendarService.Scope.CalendarEvents);
            }

            _service = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Agiliz.Runtime"
            });
            _logger.LogInformation("Google Calendar Service autenticado com sucesso.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar credenciais do Google Calendar.");
        }
    }

    public async Task<List<string>> GetAvailableSlotsAsync(string calendarId, DateTimeOffset data)
    {
        // Se a Service Account não foi carregada (ex: falta do arquivo json na maquina local)
        if (_service == null) 
        {
            _logger.LogWarning("Retornando slots mockados pois a Service Account não está configurada.");
            return ["09:00", "10:00", "14:00", "15:30"];
        }

        var startOfDay = data.Date;
        var endOfDay = startOfDay.AddDays(1).AddTicks(-1);

        var request = _service.Events.List(calendarId);
        request.TimeMinDateTimeOffset = new DateTimeOffset(startOfDay, TimeSpan.FromHours(-3));
        request.TimeMaxDateTimeOffset = new DateTimeOffset(endOfDay, TimeSpan.FromHours(-3));
        request.SingleEvents = true;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        Events events;
        try 
        {
            events = await request.ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao ler o calendário {CalendarId}", calendarId);
            return [];
        }

        var ocupados = new HashSet<string>();
        if (events.Items != null)
        {
            foreach (var ev in events.Items)
            {
                if (ev.Start.DateTimeDateTimeOffset.HasValue)
                {
                    var startBr = ev.Start.DateTimeDateTimeOffset.Value.ToOffset(TimeSpan.FromHours(-3));
                    ocupados.Add(startBr.ToString("HH:mm"));
                }
            }
        }

        var livres = new List<string>();
        // Grade simplificada das 08h as 18h (em produção, extrairíamos essa grade do BotConfig)
        for (int i = 8; i <= 18; i++)
        {
            if (i == 12 || i == 13) continue; // Hora de almoço

            var hr = $"{i:00}:00";
            if (!ocupados.Contains(hr))
            {
                livres.Add(hr);
            }
        }

        return livres;
    }

    public async Task<bool> CreateEventAsync(string calendarId, string nomePaciente, DateTimeOffset dataHora, string celular)
    {
        if (_service == null) return true; // mock fallback

        var newEvent = new Event
        {
            Summary = $"Consulta - {nomePaciente}",
            Description = $"Paciente: {nomePaciente}\nWhatsApp: {celular}",
            Start = new EventDateTime
            {
                DateTimeDateTimeOffset = dataHora,
                TimeZone = "America/Sao_Paulo"
            },
            End = new EventDateTime
            {
                DateTimeDateTimeOffset = dataHora.AddHours(1),
                TimeZone = "America/Sao_Paulo"
            }
        };

        try
        {
            var request = _service.Events.Insert(newEvent, calendarId);
            await request.ExecuteAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao criar evento no Google Calendar para o calendário {CalendarId}", calendarId);
            return false;
        }
    }
}
