using System.Text.RegularExpressions;
using NostrShortsDvm.Models;

namespace NostrShortsDvm.Services;

public class UrlExtractor
{
    private static readonly (string Platform, Regex Pattern, int VideoIdGroup, int? UserIdGroup)[] Patterns =
    [
        // YouTube
        ("youtube", new Regex(@"https?://(?:www\.)?youtube\.com/shorts/([\w\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        ("youtube", new Regex(@"https?://youtu\.be/([\w\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        ("youtube", new Regex(@"https?://(?:www\.)?youtube\.com/watch\?v=([\w\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        // TikTok
        ("tiktok", new Regex(@"https?://(?:www\.)?tiktok\.com/@([\w.]+)/video/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 2, 1),
        ("tiktok", new Regex(@"https?://vm\.tiktok\.com/([\w]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        // Instagram
        ("instagram", new Regex(@"https?://(?:www\.)?instagram\.com/reel/([\w\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        ("instagram", new Regex(@"https?://(?:www\.)?instagram\.com/p/([\w\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        // Facebook
        ("facebook", new Regex(@"https?://(?:www\.)?facebook\.com/reel/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        ("facebook", new Regex(@"https?://(?:www\.)?facebook\.com/share/r/([\w]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        ("facebook", new Regex(@"https?://(?:www\.)?facebook\.com/share/v/([\w]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 1, null),
        ("facebook", new Regex(@"https?://(?:www\.)?facebook\.com/(.+)/videos/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 2, 1),
        // X/Twitter
        ("twitter", new Regex(@"https?://(?:www\.)?(?:twitter\.com|x\.com)/(\w+)/status/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase), 2, 1),
    ];

    /// <summary>
    /// Extracts the first supported video URL from the message text.
    /// Returns the URL portion only. Use ParseDmMessage for full DM parsing with npub/split.
    /// </summary>
    public VideoJob? Extract(string messageText)
    {
        foreach (var (platform, pattern, videoIdGroup, userIdGroup) in Patterns)
        {
            var match = pattern.Match(messageText);
            if (match.Success)
            {
                var job = new VideoJob
                {
                    OriginalUrl = match.Value,
                    Platform = platform,
                    VideoId = match.Groups[videoIdGroup].Value
                };

                if (userIdGroup.HasValue && match.Groups[userIdGroup.Value].Success)
                    job.PlatformUserId = match.Groups[userIdGroup.Value].Value;

                return job;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a full DM message: extracts video URL, optional creator npub, and optional zap split.
    /// Format: &lt;url&gt; [npub] [split%]
    /// </summary>
    public VideoJob? ParseDmMessage(string messageText)
    {
        var job = Extract(messageText);
        if (job == null)
            return null;

        // Remove the URL from the message and parse remaining tokens
        var remaining = messageText.Replace(job.OriginalUrl, "").Trim();
        if (string.IsNullOrEmpty(remaining))
            return job;

        var tokens = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // First token after URL: npub or hex pubkey
        if (tokens.Length >= 1 && (tokens[0].StartsWith("npub") || IsHexPubKey(tokens[0])))
        {
            job.CreatorPubKey = tokens[0];
        }

        // Second token: zap split percentage for creator
        if (tokens.Length >= 2 && int.TryParse(tokens[1], out var split) && split >= 0 && split <= 100)
        {
            job.CreatorZapShare = split;
        }

        return job;
    }

    private static bool IsHexPubKey(string value)
    {
        return value.Length == 64 && value.All(c => "0123456789abcdefABCDEF".Contains(c));
    }
}
