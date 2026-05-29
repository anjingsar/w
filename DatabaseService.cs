using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;

namespace XivWidget;

public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService()
    {
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "widget.db");
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var createTableQuery = @"
            CREATE TABLE IF NOT EXISTS Users (
                DiscordId INTEGER PRIMARY KEY,
                LodestoneId TEXT NOT NULL,
                MainJob TEXT,
                RaidType TEXT DEFAULT 'Savage'
            );";

        connection.Execute(createTableQuery);
    }

    public void AddOrUpdateUser(ulong discordId, string lodestoneId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var query = @"
            INSERT INTO Users (DiscordId, LodestoneId)
            VALUES (@DiscordId, @LodestoneId)
            ON CONFLICT(DiscordId) DO UPDATE SET LodestoneId = excluded.LodestoneId;";

        connection.Execute(query, new { DiscordId = discordId, LodestoneId = lodestoneId });
    }

    public string? GetLodestoneId(ulong discordId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var query = "SELECT LodestoneId FROM Users WHERE DiscordId = @DiscordId";
        return connection.QueryFirstOrDefault<string>(query, new { DiscordId = discordId });
    }

    public void SetMainJob(ulong discordId, string jobName)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var query = "UPDATE Users SET MainJob = @MainJob WHERE DiscordId = @DiscordId";
        connection.Execute(query, new { DiscordId = discordId, MainJob = jobName });
    }

    public string? GetMainJob(ulong discordId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var query = "SELECT MainJob FROM Users WHERE DiscordId = @DiscordId";
        return connection.QueryFirstOrDefault<string>(query, new { DiscordId = discordId });
    }

    public void SetRaidType(ulong discordId, string raidType)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var query = "UPDATE Users SET RaidType = @RaidType WHERE DiscordId = @DiscordId";
        connection.Execute(query, new { DiscordId = discordId, RaidType = raidType });
    }

    public string GetRaidType(ulong discordId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var query = "SELECT RaidType FROM Users WHERE DiscordId = @DiscordId";
        return connection.QueryFirstOrDefault<string>(query, new { DiscordId = discordId }) ?? "Savage";
    }
}
