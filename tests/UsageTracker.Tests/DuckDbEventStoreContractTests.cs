using UsageTracker.Contracts;
using UsageTracker.Storage.DuckDb;

namespace UsageTracker.Tests;

/// <summary>
/// Runs the FULL IEventStore contract suite against the embedded DuckDB store —
/// proving the columnar analytics store is a true peer of InMemory + SQLite (same
/// 6 assertions, no exceptions). This is the modularity mechanism doing its job:
/// a new backend is swappable iff it passes this suite. Each test gets its own
/// in-memory DuckDB instance for isolation.
/// </summary>
public sealed class DuckDbEventStoreContractTests : EventStoreContractTests
{
    protected override IEventStore CreateStore() => DuckDbEventStore.InMemory();
}
