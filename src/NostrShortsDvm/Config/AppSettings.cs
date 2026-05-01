namespace NostrShortsDvm.Config;

public class AppSettings
{
    public NostrSettings Nostr { get; set; } = new();
    public BlossomSettings Blossom { get; set; } = new();
    public YtDlpSettings YtDlp { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
}

public class NostrSettings
{
    /// <summary>
    /// The DVM's private key (nsec or hex). Used for decrypting DMs and sending replies.
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional separate private key for publishing events.
    /// If empty, the DVM's PrivateKey is used.
    /// </summary>
    public string PublishPrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// The npub (or hex pubkey) to listen for DMs from.
    /// </summary>
    public string ListenFromNpub { get; set; } = string.Empty;

    /// <summary>
    /// Relay WebSocket URLs.
    /// </summary>
    public string[] Relays { get; set; } = [];

    /// <summary>
    /// Event kind to publish: 1 (note) or 34235 (NIP-71 video).
    /// </summary>
    public int EventKind { get; set; } = 34235;
}

public class BlossomSettings
{
    public string ServerUrl { get; set; } = string.Empty;
}

public class YtDlpSettings
{
    public string Path { get; set; } = "yt-dlp";
    public string TempDir { get; set; } = "/app/temp";
}

public class DatabaseSettings
{
    public string Path { get; set; } = "/app/data/processed.db";
}
