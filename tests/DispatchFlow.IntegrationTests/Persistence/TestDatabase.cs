using DispatchFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DispatchFlow.IntegrationTests.Persistence;

public sealed class TestDatabase : IAsyncDisposable {
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private TestDatabase(string adminConnectionString, string databaseName) {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;
    }

    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(string adminConnectionString, bool applyMigrations) {
        var databaseName = $"test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        // The identifier is generated here, never supplied by a caller.
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync();

        var database = new TestDatabase(adminConnectionString, databaseName);
        try {
            if (applyMigrations) {
                await using var context = database.CreateContext();
                await context.Database.MigrateAsync();
            }

            return database;
        } catch {
            await database.DisposeAsync();
            throw;
        }
    }

    public AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<int> ExecuteAsync(string sql, params NpgsqlParameter[] parameters) {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}