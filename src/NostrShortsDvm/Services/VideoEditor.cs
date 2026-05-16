using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NostrShortsDvm.Config;
using NostrShortsDvm.Models;

namespace NostrShortsDvm.Services;

/// <summary>
/// Processes video editing requests via the Replicate API.
/// Downloads the source video, sends it to an AI model with edit instructions,
/// and saves the result locally for upload to Blossom.
/// </summary>
public class VideoEditor
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<VideoEditor> _logger;

    private const string ReplicateApiBase = "https://api.replicate.com/v1";

    public VideoEditor(AppSettings settings, HttpClient httpClient, ILogger<VideoEditor> logger)
    {
        _settings = settings;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Edits a video using the Replicate API. Requires a publicly accessible URL for the source video
    /// (e.g. a Blossom URL). On success, sets job.EditedFilePath to the path of the edited video.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public async Task<string?> EditAsync(VideoJob job, string sourceVideoUrl, CancellationToken ct)
    {
        if (!_settings.VideoEdit.Enabled)
            return "Video editing is not configured. Set VideoEdit__ReplicateApiToken.";

        if (string.IsNullOrEmpty(sourceVideoUrl))
            return "Source video URL is required for editing.";

        if (string.IsNullOrEmpty(job.EditPrompt))
            return "No edit prompt provided. Use: <url> !edit <description of changes>";

        try
        {
            _logger.LogInformation("Sending video to Replicate for editing: {Url}", sourceVideoUrl);

            // Step 1: Create a prediction with the public video URL
            var predictionId = await CreatePredictionAsync(sourceVideoUrl, job.EditPrompt, ct);
            if (predictionId == null)
                return "Failed to create video edit prediction on Replicate.";

            _logger.LogInformation("Created Replicate prediction: {Id}", predictionId);

            // Step 2: Poll until completion
            var outputUrl = await PollPredictionAsync(predictionId, ct);
            if (outputUrl == null)
                return "Video edit prediction failed or timed out on Replicate.";

            _logger.LogInformation("Prediction completed, output: {Url}", outputUrl);

            // Step 3: Download the edited video
            var editedPath = Path.Combine(
                _settings.YtDlp.TempDir,
                $"edited_{Path.GetFileNameWithoutExtension(job.LocalFilePath ?? "video")}.mp4");

            await DownloadFileAsync(outputUrl, editedPath, ct);
            job.EditedFilePath = editedPath;

            _logger.LogInformation("Edited video saved to {Path}", editedPath);
            return null; // success
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video editing failed for {Url}", job.OriginalUrl);
            return $"Video editing failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Creates a prediction on Replicate for the video edit model.
    /// </summary>
    private async Task<string?> CreatePredictionAsync(string videoUrl, string editPrompt, CancellationToken ct)
    {
        var url = $"{ReplicateApiBase}/predictions";

        var payload = new
        {
            model = _settings.VideoEdit.Model,
            input = new Dictionary<string, object>
            {
                ["video"] = videoUrl,
                ["prompt"] = editPrompt
            }
        };

        var jsonPayload = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.VideoEdit.ReplicateApiToken);
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Replicate prediction creation failed ({Status}): {Error}",
                response.StatusCode, responseJson);
            return null;
        }

        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    /// <summary>
    /// Polls a Replicate prediction until it reaches a terminal state.
    /// Returns the output URL on success, null on failure/timeout.
    /// </summary>
    private async Task<string?> PollPredictionAsync(string predictionId, CancellationToken ct)
    {
        var url = $"{ReplicateApiBase}/predictions/{predictionId}";
        var timeout = TimeSpan.FromSeconds(_settings.VideoEdit.TimeoutSeconds);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.VideoEdit.ReplicateApiToken);

            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString();

            _logger.LogDebug("Prediction {Id} status: {Status}", predictionId, status);

            switch (status)
            {
                case "succeeded":
                    var output = doc.RootElement.GetProperty("output");
                    // Output can be a string URL or an array of URLs
                    if (output.ValueKind == JsonValueKind.String)
                        return output.GetString();
                    if (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0)
                        return output[0].GetString();
                    _logger.LogError("Prediction succeeded but output format unexpected: {Json}", json);
                    return null;

                case "failed":
                    var error = doc.RootElement.TryGetProperty("error", out var errProp)
                        ? errProp.GetString() : "unknown error";
                    _logger.LogError("Prediction {Id} failed: {Error}", predictionId, error);
                    return null;

                case "canceled":
                    _logger.LogWarning("Prediction {Id} was canceled", predictionId);
                    return null;

                // "starting", "processing" — keep polling
            }
        }

        _logger.LogError("Prediction {Id} timed out after {Timeout}s", predictionId, timeout.TotalSeconds);
        return null;
    }

    /// <summary>
    /// Downloads a file from a URL to a local path.
    /// </summary>
    private async Task DownloadFileAsync(string url, string outputPath, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(outputPath);
        await response.Content.CopyToAsync(fileStream, ct);
    }

    /// <summary>
    /// Cleans up the edited video file.
    /// </summary>
    public void CleanupEdited(VideoJob job)
    {
        if (!string.IsNullOrEmpty(job.EditedFilePath) && File.Exists(job.EditedFilePath))
        {
            try
            {
                File.Delete(job.EditedFilePath);
                _logger.LogDebug("Cleaned up edited video: {Path}", job.EditedFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up edited video: {Path}", job.EditedFilePath);
            }
        }
    }
}
