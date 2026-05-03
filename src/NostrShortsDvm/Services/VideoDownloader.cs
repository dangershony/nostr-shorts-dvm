using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NostrShortsDvm.Config;
using NostrShortsDvm.Models;

namespace NostrShortsDvm.Services;

public class VideoDownloader
{
    private readonly AppSettings _settings;
    private readonly ILogger<VideoDownloader> _logger;

    public VideoDownloader(AppSettings settings, ILogger<VideoDownloader> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Downloads the video using yt-dlp and populates the job with file path, mime type, etc.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public async Task<string?> DownloadAsync(VideoJob job, CancellationToken ct)
    {
        Directory.CreateDirectory(_settings.YtDlp.TempDir);

        var outputTemplate = Path.Combine(_settings.YtDlp.TempDir, "%(id)s.%(ext)s");

        var args = $"--no-playlist --no-warnings --max-filesize 100M -o \"{outputTemplate}\" --print before_dl:title --print before_dl:description --print after_move:filepath \"{job.OriginalUrl}\"";

        _logger.LogInformation("Downloading video: {Url}", job.OriginalUrl);

        var psi = new ProcessStartInfo
        {
            FileName = _settings.YtDlp.Path,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            _logger.LogError("Failed to start yt-dlp");
            return "Failed to start yt-dlp process";
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogError("yt-dlp failed with exit code {ExitCode}: {Stderr}", process.ExitCode, stderr);
            return $"yt-dlp failed (exit code {process.ExitCode}): {stderr.Trim()}";
        }

        var lines = stdout.Trim().Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToArray();

        // Output format: title (1 line), description (may be multi-line), filepath (last line)
        // The filepath is always the last line and is a valid file path
        if (lines.Length == 0)
        {
            _logger.LogError("yt-dlp produced no output");
            return "yt-dlp produced no output";
        }

        var filePath = lines.Last();

        if (!File.Exists(filePath))
        {
            _logger.LogError("yt-dlp output file not found: {FilePath}", filePath);
            return $"Downloaded file not found at: {filePath}";
        }

        // First line is title, everything between first and last is description
        if (lines.Length >= 2)
            job.Title = CleanTitle(lines[0]);
        if (lines.Length >= 3)
            job.Description = string.Join("\n", lines[1..^1]);

        job.LocalFilePath = filePath;
        job.MimeType = GetMimeType(filePath);
        job.FileSize = new FileInfo(filePath).Length;

        _logger.LogInformation("Downloaded: {FilePath} ({Size} bytes)", filePath, job.FileSize);
        return null;
    }

    /// <summary>
    /// Cleans up the downloaded temp file.
    /// </summary>
    public void Cleanup(VideoJob job)
    {
        if (job.LocalFilePath != null && File.Exists(job.LocalFilePath))
        {
            try
            {
                File.Delete(job.LocalFilePath);
                _logger.LogDebug("Cleaned up temp file: {FilePath}", job.LocalFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp file: {FilePath}", job.LocalFilePath);
            }
        }
    }

    private static string GetMimeType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".flv" => "video/x-flv",
            _ => "video/mp4"
        };
    }

    /// <summary>
    /// Cleans up the video title: strips view/reaction counts and social media metadata,
    /// and caps at a reasonable length. Facebook/Instagram titles often contain the full post text.
    /// </summary>
    private static string CleanTitle(string title)
    {
        // Remove common social media metadata patterns like "88K views · 1.6K reactions |"
        title = Regex.Replace(title, @"[\d.]+[KMB]?\s*views?\s*·?\s*", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"[\d.]+[KMB]?\s*reactions?\s*·?\s*", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"[\d.]+[KMB]?\s*likes?\s*·?\s*", "", RegexOptions.IgnoreCase);

        // Remove leading/trailing pipe separators and whitespace
        title = title.Trim(' ', '|', '·');

        // If there are pipe-separated segments, take the longest one (likely the actual content)
        if (title.Contains('|'))
        {
            var segments = title.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (segments.Length > 0)
                title = segments.OrderByDescending(s => s.Length).First();
        }

        // Cap at 200 characters
        if (title.Length > 200)
            title = title[..200].Trim() + "…";

        return title.Trim();
    }
}
