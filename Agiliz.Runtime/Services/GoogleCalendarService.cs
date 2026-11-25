using Agiliz.Core.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace Agiliz.Runtime.Services;

public sealed class GoogleCalendarService
{
    private readonly CalendarService? _service;
    private readonly ILogger<GoogleCalendarService> _logger;

    public GoogleCalendarService(IConfiguration config, ILogger<GoogleCalendarService> logger, IWebHostEnvironment? env = null)
    {
        _logger = logger;

        var configsDir = Agiliz.Core.Config.BotConfigLoader.ResolveConfigsDir(config);
        var keyPath = Path.Combine(configsDir, "google_credentials.json");

        if (!File.Exists(keyPath))
        {
            // Sem credencial o Runtime sobe normalmente; só a agenda fica desligada
            // ("Testar conexão" da Google Agenda explica o que falta).
            _logger.LogWarning("Arquivo google_credentials.json nao encontrado em {KeyPath}. Google Calendar offline.", keyPath);
            return;
        }

        try
        {
            using (var key = System.Text.Json.JsonDocument.Parse(File.ReadAllText(keyPath)))
                if (key.RootElement.TryGetProperty("client_email", out var email))
                    ServiceAccountEmail = email.GetString();

            var credential = CredentialFactory
                .FromFile<ServiceAccountCredential>(keyPath)
                .ToGoogleCredential()
                .CreateScoped(CalendarService.Scope.CalendarEvents);

            _service = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Agiliz.Runtime"
            });
            _logger.LogInformation("Google Calendar Service autenticado com sucesso.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar credenciais do Google Calendar. Google Calendar offline.");
        }
    }

    public bool IsConfigured => _service is not null;

    /// <summary>E-mail da conta de serviço: o cliente compartilha a agenda com ele.</summary>
    public string? ServiceAccountEmail { get; }

    /// <summary>
    /// Lista um evento futuro: prova que a agenda existe e foi compartilhada. A permissão de edição
    /// só aparece ao criar o primeiro evento (não criamos evento de teste na agenda do cliente).
    /// </summary>
    public async Task<Agiliz.Core.Integrations.IntegrationTestResult> CheckAccessAsync(string calendarId, CancellationToken ct = default)
    {
        if (_service is null)
            return new(false, "Credenciais do Google ausentes no servidor (configs/google_credentials.json).");

        var account = ServiceAccountEmail ?? "a conta de serviço do Agiliz";
        try
        {
            var request = _service.Events.List(calendarId);
            request.MaxResults = 1;
            request.SingleEvents = true;
            request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow;
            await request.ExecuteAsync(ct);
            return new(true, $"Agenda acessível por {account}. Confirme que o compartilhamento é \"Fazer alterações nos eventos\".");
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new(false, $"Agenda não encontrada. Confira o ID e se ela foi compartilhada com {account}.");
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            return new(false, $"Sem permissão na agenda. Compartilhe com {account} (\"Fazer alterações nos eventos\").");
        }
    }

    public async Task<List<string>> GetAvailableSlotsAsync(
        string calendarId,
        DateTimeOffset date,
        SchedulingSettings settings,
        CancellationToken ct = default)
    {
        if (_service is null)
        {
            _logger.LogWarning("Google Calendar nao configurado. Nenhum slot real sera retornado.");
            return [];
        }

        var offset = ResolveOffset(settings.TimeZone, date);
        var localDate = date.ToOffset(offset).Date;
        var startOfDay = new DateTimeOffset(localDate, offset);
        var endOfDay = startOfDay.AddDays(1);

        var request = _service.Events.List(calendarId);
        request.TimeMinDateTimeOffset = startOfDay;
        request.TimeMaxDateTimeOffset = endOfDay;
        request.SingleEvents = true;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        Events events;
        try
        {
            events = await request.ExecuteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao ler o calendario {CalendarId}", calendarId);
            return [];
        }

        var busy = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var ev in events.Items ?? [])
        {
            if (ev.Start.DateTimeDateTimeOffset.HasValue)
            {
                var evStart = ev.Start.DateTimeDateTimeOffset.Value.ToOffset(offset);
                var evEnd = ev.End.DateTimeDateTimeOffset?.ToOffset(offset)
                    ?? evStart.AddMinutes(settings.AppointmentDurationMinutes);
                busy.Add((evStart, evEnd));
            }
        }

        var rule = settings.BusinessHours.FirstOrDefault(r => r.Day == startOfDay.DayOfWeek);
        if (rule is null) return [];

        var slots = new List<string>();
        var cursor = startOfDay.Date.Add(ParseTime(rule.Start));
        var end = startOfDay.Date.Add(ParseTime(rule.End));

        while (cursor.AddMinutes(settings.AppointmentDurationMinutes) <= end)
        {
            var candidate = new DateTimeOffset(cursor, offset);
            var candidateEnd = candidate.AddMinutes(settings.AppointmentDurationMinutes);
            var overlaps = busy.Any(b => candidate < b.End && candidateEnd > b.Start);
            var tooSoon = candidate < DateTimeOffset.UtcNow.ToOffset(offset).AddMinutes(settings.MinimumNoticeMinutes);

            if (!overlaps && !tooSoon && !IsInsideBreak(candidate, candidateEnd, rule))
                slots.Add(candidate.ToString("HH:mm"));

            cursor = cursor.AddMinutes(settings.AppointmentDurationMinutes);
        }

        return slots;
    }

    public async Task<bool> IsSlotAvailableAsync(
        string calendarId,
        DateTimeOffset dateTime,
        SchedulingSettings settings,
        CancellationToken ct = default)
    {
        var offset = ResolveOffset(settings.TimeZone, dateTime);
        var slots = await GetAvailableSlotsAsync(calendarId, dateTime, settings, ct);
        return slots.Contains(dateTime.ToOffset(offset).ToString("HH:mm"));
    }

    public async Task<string?> CreateEventAsync(
        string calendarId,
        string patientName,
        DateTimeOffset dateTime,
        string phone,
        SchedulingSettings settings,
        CancellationToken ct = default)
    {
        if (_service is null) return null;

        var newEvent = new Event
        {
            Summary = $"Consulta - {patientName}",
            Description = $"Paciente: {patientName}\nWhatsApp: {phone}",
            Start = new EventDateTime { DateTimeDateTimeOffset = dateTime, TimeZone = settings.TimeZone },
            End = new EventDateTime { DateTimeDateTimeOffset = dateTime.AddMinutes(settings.AppointmentDurationMinutes), TimeZone = settings.TimeZone }
        };

        try
        {
            var created = await _service.Events.Insert(newEvent, calendarId).ExecuteAsync(ct);
            return created.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao criar evento no Google Calendar para o calendario {CalendarId}", calendarId);
            return null;
        }
    }

    public async Task<bool> DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default)
    {
        if (_service is null) return false;

        try
        {
            await _service.Events.Delete(calendarId, eventId).ExecuteAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao cancelar evento {EventId} no calendario {CalendarId}", eventId, calendarId);
            return false;
        }
    }

    public async Task<bool> MoveEventAsync(
        string calendarId,
        string eventId,
        DateTimeOffset newStart,
        SchedulingSettings settings,
        CancellationToken ct = default)
    {
        if (_service is null) return false;

        try
        {
            var ev = await _service.Events.Get(calendarId, eventId).ExecuteAsync(ct);
            ev.Start = new EventDateTime { DateTimeDateTimeOffset = newStart, TimeZone = settings.TimeZone };
            ev.End = new EventDateTime { DateTimeDateTimeOffset = newStart.AddMinutes(settings.AppointmentDurationMinutes), TimeZone = settings.TimeZone };
            await _service.Events.Update(ev, calendarId, eventId).ExecuteAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao reagendar evento {EventId} no calendario {CalendarId}", eventId, calendarId);
            return false;
        }
    }

    private static bool IsInsideBreak(DateTimeOffset start, DateTimeOffset end, BusinessHourRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.BreakStart) || string.IsNullOrWhiteSpace(rule.BreakEnd))
            return false;

        var breakStart = new DateTimeOffset(start.Date.Add(ParseTime(rule.BreakStart)), start.Offset);
        var breakEnd = new DateTimeOffset(start.Date.Add(ParseTime(rule.BreakEnd)), start.Offset);
        return start < breakEnd && end > breakStart;
    }

    private static TimeSpan ParseTime(string value) =>
        TimeSpan.TryParse(value, out var parsed) ? parsed : TimeSpan.Zero;

    private static TimeSpan ResolveOffset(string timeZoneId, DateTimeOffset instant)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(instant);
        }
        catch
        {
            return TimeSpan.FromHours(-3);
        }
    }
}
