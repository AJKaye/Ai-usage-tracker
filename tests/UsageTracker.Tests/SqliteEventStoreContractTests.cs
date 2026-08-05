using UsageTracker.Contracts;
using UsageTracker.Storage.Sqlite;

namespace UsageTracker.Tests;

/// <summary>
/// Runs the FULL <see cref="EventStoreContractTests"/> suite against the SQLite
/// store. Green here = the embedded .exe backend is a real peer of the in-memory
/// (and future ClickHouse) store, not a lesser implementation. Each test gets a
/// fresh uniquely-named shared in-memory SQLite DB for isolation without touching
/// the filesystem.
/// </summary>
public sealed class SqliteEventStoreContractTests : EventStoreContractTests
{
    protected override IEventStore CreateStore()
        => SqliteEventStore.InMemoryShared("ut-" + Guid.NewGuid().ToString("n"));
}
