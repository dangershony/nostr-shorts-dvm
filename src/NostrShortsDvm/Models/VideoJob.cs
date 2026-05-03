namespace NostrShortsDvm.Models;

public class VideoJob
{
    public string OriginalUrl { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? VideoId { get; set; }
    public string? LocalFilePath { get; set; }
    public string? MimeType { get; set; }
    public string? FileHash { get; set; }
    public long? FileSize { get; set; }
    public string? BlossomUrl { get; set; }
    public string? EventId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public bool IncludeDescription { get; set; }

    /// <summary>
    /// Optional creator npub/hex pubkey provided in the DM.
    /// </summary>
    public string? CreatorPubKey { get; set; }

    /// <summary>
    /// Creator's zap share percentage (0-100). Null means use default.
    /// </summary>
    public int? CreatorZapShare { get; set; }

    /// <summary>
    /// Platform-specific user/channel ID extracted from the URL or metadata.
    /// </summary>
    public string? PlatformUserId { get; set; }
}
