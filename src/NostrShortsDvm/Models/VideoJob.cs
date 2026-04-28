namespace NostrShortsDvm.Models;

public class VideoJob
{
    public string OriginalUrl { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? LocalFilePath { get; set; }
    public string? MimeType { get; set; }
    public string? FileHash { get; set; }
    public long? FileSize { get; set; }
    public string? BlossomUrl { get; set; }
    public string? EventId { get; set; }
    public string? Title { get; set; }
}
