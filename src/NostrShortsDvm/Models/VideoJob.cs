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
    /// If true, skip summarization and publish with no summary/description.
    /// </summary>
    public bool NoSummary { get; set; }

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

    /// <summary>
    /// Whether this job is a video editing request (triggered by !edit flag).
    /// </summary>
    public bool IsEditRequest { get; set; }

    /// <summary>
    /// The edit prompt describing what changes to make to the video.
    /// </summary>
    public string? EditPrompt { get; set; }

    /// <summary>
    /// Path to the edited video file (output from AI video processing).
    /// </summary>
    public string? EditedFilePath { get; set; }
}
