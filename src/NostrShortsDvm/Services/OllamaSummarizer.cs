using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NostrShortsDvm.Config;

namespace NostrShortsDvm.Services;

public class OllamaSummarizer
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaSummarizer> _logger;

    public OllamaSummarizer(AppSettings settings, HttpClient httpClient, ILogger<OllamaSummarizer> logger)
    {
        _settings = settings;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _logger = logger;
    }

    /// <summary>
    /// Summarizes a video description into a short caption suitable for a social post.
    /// Returns null if summarization fails.
    /// </summary>
    public async Task<string?> SummarizeAsync(string title, string description, bool shorter = false, CancellationToken ct = default)
    {
        var lengthInstruction = shorter
            ? "Make it very short — maximum 1 sentence, under 50 characters."
            : "Keep it to 1-2 sentences, under 150 characters.";

        var prompt = $"""
            Summarize this video description into a brief, engaging caption for a social media post.
            {lengthInstruction}
            Do NOT include hashtags, emojis, or quotation marks.
            Just return the summary text, nothing else.

            Title: {title}
            Description: {description}
            """;

        try
        {
            var requestBody = new
            {
                model = _settings.Ollama.Model,
                prompt = prompt,
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"{_settings.Ollama.BaseUrl.TrimEnd('/')}/api/generate", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Ollama request failed ({Status}): {Body}", response.StatusCode, errorBody);
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("response", out var responseProp))
            {
                var summary = responseProp.GetString()?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    _logger.LogInformation("Generated summary: {Summary}", summary);
                    return summary;
                }
            }

            _logger.LogWarning("Ollama returned empty response");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get summary from Ollama");
            return null;
        }
    }

    /// <summary>
    /// Checks if Ollama is available and the model is loaded.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{_settings.Ollama.BaseUrl.TrimEnd('/')}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a description for an AI-edited video, combining the original video details
    /// with the edit prompt into a natural, engaging caption.
    /// Falls back to a simple combined string if Ollama is unavailable.
    /// </summary>
    public async Task<string?> GenerateEditedVideoDescriptionAsync(
        string originalTitle, string originalDescription, string editPrompt, CancellationToken ct = default)
    {
        var prompt = $"""
            You are writing a short caption for a video that was created by AI-editing an original video.
            Combine the original video's context with the AI edit that was applied.
            Write a natural, engaging caption (1-2 sentences, under 200 characters).
            Do NOT mention "AI", "edited", or "modified". Write as if this is the video's own description.
            Do NOT include hashtags, emojis, or quotation marks.
            Just return the caption, nothing else.

            Original title: {originalTitle}
            Original description: {originalDescription}
            Edit applied: {editPrompt}
            """;

        try
        {
            var requestBody = new
            {
                model = _settings.Ollama.Model,
                prompt = prompt,
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"{_settings.Ollama.BaseUrl.TrimEnd('/')}/api/generate", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ollama request failed for edit description ({Status})", response.StatusCode);
                return $"{originalTitle} — {editPrompt}";
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("response", out var responseProp))
            {
                var caption = responseProp.GetString()?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    _logger.LogInformation("Generated edit description: {Caption}", caption);
                    return caption;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate edit description via Ollama");
        }

        // Fallback: simple combination
        return string.IsNullOrWhiteSpace(originalTitle)
            ? editPrompt
            : $"{originalTitle} — {editPrompt}";
    }

    /// <summary>
    /// Pulls the model if not already available.
    /// </summary>
    public async Task EnsureModelAsync(CancellationToken ct = default)
    {
        try
        {
            var requestBody = new { name = _settings.Ollama.Model, stream = false };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Ensuring Ollama model {Model} is available...", _settings.Ollama.Model);
            var response = await _httpClient.PostAsync(
                $"{_settings.Ollama.BaseUrl.TrimEnd('/')}/api/pull", content, ct);

            if (response.IsSuccessStatusCode)
                _logger.LogInformation("Ollama model {Model} is ready", _settings.Ollama.Model);
            else
                _logger.LogWarning("Failed to pull Ollama model: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure Ollama model");
        }
    }

    /// <summary>
    /// Formats a raw API error message into a friendly, readable message for the user.
    /// Falls back to the raw error if Ollama is unavailable.
    /// </summary>
    public async Task<string> FormatErrorAsync(string rawError, string context, CancellationToken ct = default)
    {
        var prompt = $"""
            You are a helpful assistant. A user sent a request to a video processing bot and it failed.
            Rewrite the following technical error into a short, friendly message (2-3 sentences max).
            Explain what went wrong and what the user can do to fix it, if anything.
            Do NOT include any code, JSON, or technical jargon.
            Just return the friendly message, nothing else.

            Context: {context}
            Error: {rawError}
            """;

        try
        {
            var requestBody = new
            {
                model = _settings.Ollama.Model,
                prompt = prompt,
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"{_settings.Ollama.BaseUrl.TrimEnd('/')}/api/generate", content, ct);

            if (!response.IsSuccessStatusCode)
                return rawError;

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("response", out var responseProp))
            {
                var formatted = responseProp.GetString()?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    _logger.LogDebug("Formatted error: {Formatted}", formatted);
                    return formatted;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to format error via Ollama, using raw error");
        }

        return rawError;
    }
}
