using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ArmaAiBridge.App.Services;

public sealed class SqliteMapIntelligenceRepository : IDisposable
{
    public const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly string _databasePath;
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public SqliteMapIntelligenceRepository(string? databasePath = null)
    {
        _databasePath = databasePath ?? AppPaths.MapIntelligenceDatabase;
        if (_databasePath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
        _connection = new SqliteConnection(_databasePath == ":memory:"
            ? "Data Source=:memory:"
            : new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());
        _connection.Open();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS map_intelligence(
                record_id INTEGER PRIMARY KEY AUTOINCREMENT,
                world_name TEXT NOT NULL,
                category TEXT NOT NULL,
                scope TEXT NOT NULL,
                title TEXT NOT NULL,
                summary TEXT NOT NULL,
                provenance TEXT NOT NULL,
                confidence TEXT NOT NULL,
                observed_utc TEXT NOT NULL,
                retired_utc TEXT);
            CREATE INDEX IF NOT EXISTS ix_map_intelligence_query
                ON map_intelligence(world_name,category,scope,retired_utc,observed_utc);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    public string DatabasePath => _databasePath;

    public long Store(
        string worldName,
        string category,
        string scope,
        string title,
        string summary,
        string provenance = "player-authored",
        string confidence = "reported")
    {
        string world = Bounded(worldName, 80, "World");
        string normalizedCategory = HierarchicalContextCatalogue.NormalizeCategory(category);
        if (!HierarchicalContextCatalogue.Contains("long_term_map_intelligence", normalizedCategory))
            throw new InvalidOperationException("Unsupported long-term category.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO map_intelligence(world_name,category,scope,title,summary,provenance,confidence,observed_utc)
                VALUES($world,$category,$scope,$title,$summary,$provenance,$confidence,$observed);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$world", world);
            command.Parameters.AddWithValue("$category", normalizedCategory);
            command.Parameters.AddWithValue("$scope", Bounded(scope, 160, "Scope"));
            command.Parameters.AddWithValue("$title", Bounded(title, 160, "Title"));
            command.Parameters.AddWithValue("$summary", Bounded(summary, 2000, "Summary"));
            command.Parameters.AddWithValue("$provenance", Bounded(provenance, 40, "Provenance"));
            command.Parameters.AddWithValue("$confidence", Bounded(confidence, 40, "Confidence"));
            command.Parameters.AddWithValue("$observed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public string Query(string worldName, string category, string scope, int limit)
    {
        string world = Bounded(worldName, 80, "World");
        string normalizedCategory = HierarchicalContextCatalogue.NormalizeCategory(category);
        string normalizedScope = Bounded(scope, 160, "Scope");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                SELECT title,summary,provenance,confidence,observed_utc
                FROM map_intelligence
                WHERE world_name=$world AND category=$category AND retired_utc IS NULL
                  AND (scope=$scope OR scope='world')
                ORDER BY observed_utc DESC,record_id DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$world", world);
            command.Parameters.AddWithValue("$category", normalizedCategory);
            command.Parameters.AddWithValue("$scope", normalizedScope);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20));
            using SqliteDataReader reader = command.ExecuteReader();
            List<object> records = new();
            while (reader.Read())
                records.Add(new
                {
                    title = reader.GetString(0),
                    summary = reader.GetString(1),
                    source = ArchiveSource(reader.GetString(2)),
                    confidence = reader.GetString(3),
                    recordedAtUtc = reader.GetString(4)
                });
            return JsonSerializer.Serialize(new
            {
                schema = "arma-ai-bridge/context-result-v1",
                group = "long_term_map_intelligence",
                category = normalizedCategory,
                detailLevel = "summary",
                records,
                returnedCount = records.Count,
                truncated = false,
                archivePresentation = true
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    private static string ArchiveSource(string value) => value switch
    {
        "engineering" => "archived engineering plans",
        "topographic" => "topographic records",
        "satellite" => "archived satellite imagery",
        _ => "historical reconnaissance"
    };

    private static string Bounded(string value, int maximum, string name)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is 0 || normalized.Length > maximum || normalized.Any(char.IsControl))
            throw new InvalidOperationException($"{name} is invalid.");
        return normalized;
    }
}
