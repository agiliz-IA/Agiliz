using System.Security.Cryptography;
using System.Text;
using Agiliz.Core.Billing;
using Agiliz.Core.Config;
using Agiliz.Core.Models;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Agiliz.Runtime.Services;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Endpoints;

/// <summary>
/// Rotas que o painel (Wizard) chama no Runtime. Todas exigem o header <see cref="AdminTokenFilter.HeaderName"/>.
/// </summary>
public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter<AdminTokenFilter>();

        // Relê configs/bots sem reiniciar (bot novo, editado ou apagado no painel).
        admin.MapPost("/reload", (TenantRegistry registry) => Results.Ok(registry.Reload()));

        admin.MapGet("/billing", GetBillingAsync);
        admin.MapPost("/billing", RecordCost).DisableAntiforgery();

        // Botão "Testar conexão" do painel e dados que o painel mostra (ex.: e-mail para compartilhar a agenda).
        admin.MapGet("/integrations/info", (IntegrationTester tester) => Results.Ok(tester.Info()));
        admin.MapPost("/integrations/{integrationId}/test", TestIntegrationAsync).DisableAntiforgery();
    }

    private static async Task<IResult> TestIntegrationAsync(
        string integrationId,
        Agiliz.Core.Integrations.IntegrationTestRequest request,
        TenantRegistry registry,
        IntegrationTester tester,
        IConfiguration configuration,
        CancellationToken ct)
    {
        // Bot recém-salvo ainda pode não ter sido recarregado: cai para o arquivo.
        var config = registry.Get(request.TenantId)?.Config;
        if (config is null)
        {
            var configsDir = BotConfigLoader.ResolveConfigsDir(configuration);
            if (!BotConfigLoader.Exists(configsDir, request.TenantId))
                return Results.NotFound(new { error = "Bot não encontrado." });
            config = BotConfigLoader.Load(configsDir, request.TenantId);
        }

        return Results.Ok(await tester.TestAsync(integrationId, config, request.Fields, ct));
    }

    /// <summary>Totais por cliente do período inteiro + os lançamentos mais recentes (até <paramref name="limit"/>).</summary>
    private static async Task<IResult> GetBillingAsync(
        AgilizDbContext db,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? tenantId,
        int? limit,
        CancellationToken ct)
    {
        var query = FilterCosts(db.CostRecords.AsNoTracking(), from, to, tenantId);
        var totals = await TotalsByTenantAndType(query).ToListAsync(ct);

        var tenants = totals
            .GroupBy(t => t.TenantId)
            .Select(t => new TenantCostSummary
            {
                TenantId = t.Key,
                TotalUsd = t.Sum(x => x.Total),
                LlmUsd = t.Where(x => x.Type == CostType.TokensLLM).Sum(x => x.Total),
                ToolsUsd = t.Where(x => x.Type == CostType.ToolExecution).Sum(x => x.Total),
                OtherUsd = t.Where(x => x.Type is not (CostType.TokensLLM or CostType.ToolExecution)).Sum(x => x.Total),
                Count = t.Sum(x => x.Count),
                ByIntegration = t.GroupBy(x => x.IntegrationId)
                    .Select(i => new IntegrationCostSummary { IntegrationId = i.Key, TotalUsd = i.Sum(x => x.Total), Count = i.Sum(x => x.Count) })
                    .OrderByDescending(i => i.TotalUsd)
                    .ToList()
            })
            .OrderByDescending(t => t.TotalUsd)
            .ToList();

        var entries = await query
            .OrderByDescending(c => c.Timestamp)
            .Take(Math.Clamp(limit ?? 500, 1, 5000))
            .ToListAsync(ct);

        return Results.Ok(new BillingReport
        {
            From = from,
            To = to,
            TotalUsd = tenants.Sum(t => t.TotalUsd),
            EntryCount = tenants.Sum(t => t.Count),
            Tenants = tenants,
            Integrations = totals.GroupBy(t => t.IntegrationId)
                .Select(i => new IntegrationCostSummary
                {
                    IntegrationId = i.Key,
                    TotalUsd = i.Sum(x => x.Total),
                    Count = i.Sum(x => x.Count),
                    TenantCount = i.Select(x => x.TenantId).Distinct().Count()
                })
                .OrderByDescending(i => i.TotalUsd)
                .ToList(),
            Entries = entries.Select(e => e.ToEntry()).ToList()
        });
    }

    public static IQueryable<CostRecord> FilterCosts(IQueryable<CostRecord> query, DateTimeOffset? from, DateTimeOffset? to, string? tenantId)
    {
        if (from is not null)
        {
            var fromUtc = from.Value.ToUniversalTime();
            query = query.Where(c => c.Timestamp >= fromUtc);
        }
        if (to is not null)
        {
            var toUtc = to.Value.ToUniversalTime();
            query = query.Where(c => c.Timestamp < toUtc);
        }
        if (!string.IsNullOrWhiteSpace(tenantId))
            query = query.Where(c => c.TenantId == tenantId);
        return query;
    }

    /// <summary>Soma no banco (GROUP BY tenant, tipo, integração); o resto da agregação é em memória.</summary>
    public static IQueryable<CostTotal> TotalsByTenantAndType(IQueryable<CostRecord> query) =>
        query.GroupBy(c => new { c.TenantId, c.Type, c.IntegrationId })
             .Select(g => new CostTotal(g.Key.TenantId, g.Key.Type, g.Key.IntegrationId, g.Sum(c => c.AmountUsd), g.Count()));

    /// <summary>Custos gerados fora do Runtime (ex.: meta-agente do painel). Id e horário são definidos aqui.</summary>
    private static IResult RecordCost(CostEntry entry, ICostRecorder costs)
    {
        if (!BotConfigLoader.IsValidTenantId(entry.TenantId))
            return Results.BadRequest(new { error = "tenantId inválido." });
        if (string.IsNullOrWhiteSpace(entry.Description))
            return Results.BadRequest(new { error = "description obrigatória." });
        if (entry.AmountUsd is < 0 or > 1000)
            return Results.BadRequest(new { error = "amountUsd fora do intervalo esperado (0 a 1000)." });
        if (entry.IntegrationId is not null && Agiliz.Core.Integrations.IntegrationCatalog.Find(entry.IntegrationId) is null)
            return Results.BadRequest(new { error = "integrationId fora do catálogo." });

        costs.Record(entry with { Id = Guid.NewGuid(), Timestamp = DateTimeOffset.UtcNow });
        return Results.Accepted();
    }
}

public sealed record CostTotal(string TenantId, CostType Type, string? IntegrationId, decimal Total, int Count);

/// <summary>
/// Exige <c>Runtime:AdminToken</c> no header <see cref="HeaderName"/>. Sem token configurado, as rotas
/// ficam abertas só em Development; em produção respondem 503 até o token ser definido.
/// </summary>
public sealed class AdminTokenFilter(IConfiguration config, IHostEnvironment env) : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Token";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = config["Runtime:AdminToken"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return env.IsDevelopment()
                ? await next(context)
                : Results.Problem("Runtime:AdminToken não configurado; rotas do painel desligadas.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
        return ok ? await next(context) : Results.Unauthorized();
    }
}
