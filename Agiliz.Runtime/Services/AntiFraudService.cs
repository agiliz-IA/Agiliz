using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Services;

public sealed class AntiFraudService(AgilizDbContext db)
{
    public async Task<(bool IsAllowed, string Reason)> CheckEligibilityAsync(string phone, string tenantId)
    {
        var pendingCount = await db.Appointments
            .CountAsync(a => a.Phone == phone && a.TenantId == tenantId && a.Status == AppointmentStatus.Pending);

        if (pendingCount >= 2)
            return (false, "Você já possui dois agendamentos pendentes. Cancele um antes de marcar outro.");

        var noShowCount = await db.Appointments
            .CountAsync(a => a.Phone == phone && a.TenantId == tenantId && a.Status == AppointmentStatus.NoShow);

        if (noShowCount >= 3)
            return (false, "Sua conta está suspensa devido a múltiplas faltas. Entre em contato com o atendimento.");

        return (true, string.Empty);
    }
}
