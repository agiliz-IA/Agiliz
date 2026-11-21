using System.Collections.Concurrent;
using Agiliz.Core.Billing;
using Agiliz.Core.Models;

namespace Agiliz.Tests.Fakes;

/// <summary>Guarda os custos em memória. Evita que testes gravem no Postgres de desenvolvimento.</summary>
public sealed class FakeCostRecorder : ICostRecorder
{
    private readonly ConcurrentQueue<CostEntry> _entries = new();

    public IReadOnlyList<CostEntry> Entries => _entries.ToList();

    public void Record(CostEntry entry) => _entries.Enqueue(entry);
}
