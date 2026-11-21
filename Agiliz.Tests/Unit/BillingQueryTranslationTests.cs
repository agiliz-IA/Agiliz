using Agiliz.Runtime.Data;
using Agiliz.Runtime.Endpoints;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agiliz.Tests.Unit;

/// <summary>
/// Os testes de integração usam banco em memória, que aceita qualquer LINQ. Aqui o provider é o
/// Npgsql de verdade (sem conectar): se uma consulta do billing não virar SQL, falha aqui e não em produção.
/// </summary>
public sealed class BillingQueryTranslationTests
{
    private static AgilizDbContext NpgsqlContext() => new(
        new DbContextOptionsBuilder<AgilizDbContext>().UseNpgsql("Host=localhost;Database=nao-conecta").Options);

    [Fact]
    public void BillingQueries_TranslateToPostgresSql()
    {
        using var db = NpgsqlContext();
        var filtered = AdminEndpoints.FilterCosts(db.CostRecords, DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now, "clinica");

        var totalsSql = AdminEndpoints.TotalsByTenantAndType(filtered).ToQueryString().ToLowerInvariant();
        var entriesSql = filtered.OrderByDescending(c => c.Timestamp).Take(500).ToQueryString().ToLowerInvariant();

        totalsSql.Should().Contain("group by").And.Contain("sum(").And.Contain("count(");
        entriesSql.Should().Contain("order by").And.Contain("limit");
    }
}
