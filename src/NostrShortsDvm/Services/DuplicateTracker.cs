using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NostrShortsDvm.Config;

namespace NostrShortsDvm.Services;

public class DuplicateTracker : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<DuplicateTracker> _logger;

    public DuplicateTracker(AppSettings settings, ILogger<DuplicateTracker> logger)
    {
        _logger = logger;

        var dir = Path.GetDirectoryName(settings.Database.Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={settings.Database.Path}");
        _connection.Open();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS processed_urls (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT UNIQUE NOT NULL,
                blossom_url TEXT NOT NULL,
                event_id TEXT,
                processed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS processed_events (
                event_id TEXT PRIMARY KEY NOT NULL,
                processed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS creator_earnings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                original_url TEXT NOT NULL,
                platform TEXT NOT NULL,
                platform_user_id TEXT,
                creator_npub TEXT,
                event_id TEXT,
                blossom_url TEXT,
                zap_share_percent INTEGER,
                total_zaps_sats INTEGER DEFAULT 0,
                claimed INTEGER DEFAULT 0,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("Database initialized");
    }

    /// <summary>
    /// Returns the existing blossom URL if the video URL was already processed, null otherwise.
    /// </summary>
    public string? GetExistingBlossomUrl(string url)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT blossom_url FROM processed_urls WHERE url = @url";
        cmd.Parameters.AddWithValue("@url", NormalizeUrl(url));
        return cmd.ExecuteScalar() as string;
    }

    public void MarkProcessed(string url, string blossomUrl, string? eventId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO processed_urls (url, blossom_url, event_id, processed_at)
            VALUES (@url, @blossomUrl, @eventId, @processedAt)
            """;
        cmd.Parameters.AddWithValue("@url", NormalizeUrl(url));
        cmd.Parameters.AddWithValue("@blossomUrl", blossomUrl);
        cmd.Parameters.AddWithValue("@eventId", eventId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@processedAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static string NormalizeUrl(string url)
    {
        // Strip trailing slashes and query params for dedup
        var uri = new Uri(url);
        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>
    /// Returns true if this gift wrap event was already processed.
    /// </summary>
    public bool IsEventProcessed(string eventId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM processed_events WHERE event_id = @eventId";
        cmd.Parameters.AddWithValue("@eventId", eventId);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Marks a gift wrap event as processed (regardless of outcome).
    /// </summary>
    public void MarkEventProcessed(string eventId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO processed_events (event_id, processed_at)
            VALUES (@eventId, @processedAt)
            """;
        cmd.Parameters.AddWithValue("@eventId", eventId);
        cmd.Parameters.AddWithValue("@processedAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Tracks creator earnings for a published video.
    /// </summary>
    public void TrackCreatorEarnings(string originalUrl, string platform, string? platformUserId,
        string? creatorNpub, string? eventId, string? blossomUrl, int? zapSharePercent)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO creator_earnings (original_url, platform, platform_user_id, creator_npub, event_id, blossom_url, zap_share_percent)
            VALUES (@originalUrl, @platform, @platformUserId, @creatorNpub, @eventId, @blossomUrl, @zapSharePercent)
            """;
        cmd.Parameters.AddWithValue("@originalUrl", originalUrl);
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@platformUserId", platformUserId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@creatorNpub", creatorNpub ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@eventId", eventId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@blossomUrl", blossomUrl ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@zapSharePercent", zapSharePercent ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
