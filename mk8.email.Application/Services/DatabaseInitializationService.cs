using System.Data;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Application.Services;

public sealed class DatabaseInitializationService(
    EmailDbContext db,
    ISeederService seeder) : IDatabaseInitializationService
{
    public async Task<AdministrationResult> InitializeEmptyDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
            """;
        var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (tableCount != 0)
        {
            return new AdministrationResult(
                false,
                "The database contains tables. Initialization stopped without changes.");
        }

        if (!await db.Database.EnsureCreatedAsync(cancellationToken))
            return new AdministrationResult(false, "The database schema was not created.");

        await seeder.SeedAsync(cancellationToken);
        return new AdministrationResult(true, "The empty database was initialized.");
    }
}
