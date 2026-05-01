using System.Text.RegularExpressions;
using NostrShortsDvm.Models;

namespace NostrShortsDvm.Services;

public class UrlExtractor
{
    private static readonly (string Platform, Regex Pattern)[] Patterns =
    [
        ("youtube", new Regex(@"https?://(?:www\.)?youtube\.com/shorts/[\w\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("youtube", new Regex(@"https?://youtu\.be/[\w\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("youtube", new Regex(@"https?://(?:www\.)?youtube\.com/watch\?v=[\w\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("tiktok", new Regex(@"https?://(?:www\.)?tiktok\.com/@[\w.]+/video/\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("tiktok", new Regex(@"https?://vm\.tiktok\.com/[\w]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("instagram", new Regex(@"https?://(?:www\.)?instagram\.com/reel/[\w\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("instagram", new Regex(@"https?://(?:www\.)?instagram\.com/p/[\w\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("facebook", new Regex(@"https?://(?:www\.)?facebook\.com/reel/\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("facebook", new Regex(@"https?://(?:www\.)?facebook\.com/share/r/[\w]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("facebook", new Regex(@"https?://(?:www\.)?facebook\.com/share/v/[\w]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("facebook", new Regex(@"https?://(?:www\.)?facebook\.com/.+/videos/\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("twitter", new Regex(@"https?://(?:www\.)?(?:twitter\.com|x\.com)/\w+/status/\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>
    /// Extracts the first supported video URL from the message text.
    /// </summary>
    public VideoJob? Extract(string messageText)
    {
        foreach (var (platform, pattern) in Patterns)
        {
            var match = pattern.Match(messageText);
            if (match.Success)
            {
                return new VideoJob
                {
                    OriginalUrl = match.Value,
                    Platform = platform
                };
            }
        }

        return null;
    }
}
