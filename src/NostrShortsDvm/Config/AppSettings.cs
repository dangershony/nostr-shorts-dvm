namespace NostrShortsDvm.Config;

public class AppSettings
{
    public NostrSettings Nostr { get; set; } = new();
    public BlossomSettings Blossom { get; set; } = new();
    public YtDlpSettings YtDlp { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public OllamaSettings Ollama { get; set; } = new();
    public VideoEditSettings VideoEdit { get; set; } = new();
}

public class NostrSettings
{
    /// <summary>
    /// The DVM's private key (nsec or hex). Used for decrypting DMs and sending replies.
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional separate private key for publishing events (legacy single-account mode).
    /// If empty, the DVM's PrivateKey is used. Prefer using Accounts[] instead.
    /// </summary>
    public string PublishPrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// The npub (or hex pubkey) to listen for DMs from (legacy single-account mode).
    /// Prefer using Accounts[] instead.
    /// </summary>
    public string ListenFromNpub { get; set; } = string.Empty;

    /// <summary>
    /// List of account pairs. Each maps a listener (who sends DMs) to a publish key (who posts videos).
    /// If empty, falls back to PublishPrivateKey + ListenFromNpub.
    /// </summary>
    public AccountPair[] Accounts { get; set; } = [];

    /// <summary>
    /// Relay WebSocket URLs.
    /// </summary>
    public string[] Relays { get; set; } = [];

    /// <summary>
    /// Event kind to publish: 1 (note) or 34235 (NIP-71 video).
    /// </summary>
    public int EventKind { get; set; } = 1;

    /// <summary>
    /// Default zap share percentage for the creator when a creator pubkey is provided (0-100).
    /// </summary>
    public int DefaultCreatorZapShare { get; set; } = 50;

    /// <summary>
    /// Lightning address (lud16) to set on publish account profiles for receiving zaps.
    /// </summary>
    public string LightningAddress { get; set; } = string.Empty;
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

public class OllamaSettings
{
    public string BaseUrl { get; set; } = "http://ollama:11434";
    public string Model { get; set; } = "llama3.2:1b";

    /// <summary>
    /// Minimum description length (chars) to trigger summarization prompt.
    /// Shorter descriptions are used as-is.
    /// </summary>
    public int MinDescriptionLength { get; set; } = 100;
}

public class VideoEditSettings
{
    /// <summary>
    /// Replicate API token for video editing requests.
    /// </summary>
    public string ReplicateApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Replicate model version to use for video editing.
    /// Default: alibaba/happyhorse-1.0 (video-edit endpoint).
    /// </summary>
    public string Model { get; set; } = "luma/modify-video";

    /// <summary>
    /// Maximum time to wait for a video edit prediction to complete (seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Whether video editing is enabled. Requires ReplicateApiToken.
    /// </summary>
    public bool Enabled => !string.IsNullOrEmpty(ReplicateApiToken);
}

/// <summary>
/// Maps an authorized sender (ListenFromNpub) to a publishing identity (PublishPrivateKey).
/// </summary>
public class AccountPair
{
    /// <summary>
    /// Private key (nsec or hex) used to publish video events for this account.
    /// </summary>
    public string PublishPrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// The npub (or hex pubkey) authorized to send DMs that trigger publishing with this account's key.
    /// </summary>
    public string ListenFromNpub { get; set; } = string.Empty;
}
