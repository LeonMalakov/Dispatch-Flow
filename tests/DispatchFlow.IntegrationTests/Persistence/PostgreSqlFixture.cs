using Testcontainers.PostgreSql;

namespace DispatchFlow.IntegrationTests.Persistence;

[CollectionDefinition("PostgreSQL")]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture> { }

public sealed class PostgreSqlFixture : IAsyncLifetime {
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-bookworm")
        .Build();

    public async Task InitializeAsync() {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await _container.StartAsync(timeout.Token);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public Task<TestDatabase> CreateDatabaseAsync(bool applyMigrations = true) =>
        TestDatabase.CreateAsync(_container.GetConnectionString(), applyMigrations);
}