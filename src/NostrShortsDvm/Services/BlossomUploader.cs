using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NostrShortsDvm.Config;
using NostrShortsDvm.Models;

namespace NostrShortsDvm.Services;

public class BlossomUploader
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<BlossomUploader> _logger;

    public BlossomUploader(AppSettings settings, HttpClient httpClient, ILogger<BlossomUploader> logger)
    {
        _settings = settings;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Uploads the video file to the Blossom server per BUD-02.
    /// The file is uploaded via PUT /upload with a Nostr auth header.
    /// </summary>
    public async Task<bool> UploadAsync(VideoJob job, ECPrivKey signingKey, CancellationToken ct)
    {
        if (job.LocalFilePath == null || !File.Exists(job.LocalFilePath))
        {
            _logger.LogError("No file to upload");
            return false;
        }

        // Compute SHA-256 hash of the file
        var fileBytes = await File.ReadAllBytesAsync(job.LocalFilePath, ct);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(fileBytes);
        job.FileHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var uploadUrl = $"{_settings.Blossom.ServerUrl.TrimEnd('/')}/upload";

        _logger.LogInformation("Uploading to Blossom: {Url} (hash: {Hash})", uploadUrl, job.FileHash);

        // Create BUD-02 authorization event (kind 24242)
        var authEvent = new NostrEvent
        {
            Kind = 24242,
            Content = $"Upload {Path.GetFileName(job.LocalFilePath)}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        authEvent.SetTag("t", "upload");
        authEvent.SetTag("x", job.FileHash);
        authEvent.SetTag("expiration", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString());

        await authEvent.ComputeIdAndSignAsync(signingKey);

        var authJson = JsonSerializer.Serialize(authEvent);
        var authBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(authJson));

        using var content = new ByteArrayContent(fileBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(job.MimeType ?? "application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Nostr", authBase64);

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Blossom upload failed ({Status}): {Body}", response.StatusCode, body);
            return false;
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Blossom response: {Body}", responseBody);

        // Parse response to get the URL
        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("url", out var urlProp))
        {
            job.BlossomUrl = urlProp.GetString();
        }
        else
        {
            // Fallback: construct URL from hash
            job.BlossomUrl = $"{_settings.Blossom.ServerUrl.TrimEnd('/')}/{job.FileHash}";
        }

        _logger.LogInformation("Uploaded to Blossom: {BlossomUrl}", job.BlossomUrl);
        return true;
    }
}
