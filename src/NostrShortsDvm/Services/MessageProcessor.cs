using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using NostrShortsDvm.Config;
using NostrShortsDvm.Nostr;

namespace NostrShortsDvm.Services;

/// <summary>
/// Orchestrates the full pipeline: decrypt DM -> extract URL -> check dupe -> download -> upload -> publish -> reply.
/// Supports interactive summary approval flow when descriptions are long.
/// </summary>
public class MessageProcessor
{
    private readonly AppSettings _settings;
    private readonly Nip17Decryptor _decryptor;
    private readonly UrlExtractor _urlExtractor;
    private readonly DuplicateTracker _duplicateTracker;
    private readonly VideoDownloader _downloader;
    private readonly BlossomUploader _uploader;
    private readonly EventPublisher _publisher;
    private readonly OllamaSummarizer _summarizer;
    private readonly VideoEditor _videoEditor;
    private readonly PendingJobTracker _pendingJobs;
    private readonly ILogger<MessageProcessor> _logger;

    private ECPrivKey _dvmPrivKey = null!;
    private Dictionary<string, ECPrivKey> _accountMap = null!;

    public MessageProcessor(
        AppSettings settings,
        Nip17Decryptor decryptor,
        UrlExtractor urlExtractor,
        DuplicateTracker duplicateTracker,
        VideoDownloader downloader,
        BlossomUploader uploader,
        EventPublisher publisher,
        OllamaSummarizer summarizer,
        VideoEditor videoEditor,
        PendingJobTracker pendingJobs,
        ILogger<MessageProcessor> logger)
    {
        _settings = settings;
        _decryptor = decryptor;
        _urlExtractor = urlExtractor;
        _duplicateTracker = duplicateTracker;
        _downloader = downloader;
        _uploader = uploader;
        _publisher = publisher;
        _summarizer = summarizer;
        _videoEditor = videoEditor;
        _pendingJobs = pendingJobs;
        _logger = logger;
    }

    public void Initialize(ECPrivKey dvmPrivKey, Dictionary<string, ECPrivKey> accountMap)
    {
        _dvmPrivKey = dvmPrivKey;
        _accountMap = accountMap;
    }

    public async Task ProcessGiftWrapAsync(NostrEvent giftWrap, INostrClient client, CancellationToken ct)
    {
        // Skip events already processed in a previous run
        if (giftWrap.Id != null && _duplicateTracker.IsEventProcessed(giftWrap.Id))
        {
            _logger.LogDebug("Skipping already-processed event {Id}", giftWrap.Id);
            return;
        }

        // Mark event as processed immediately to prevent reprocessing on restart
        if (giftWrap.Id != null)
            _duplicateTracker.MarkEventProcessed(giftWrap.Id);

        // Step 1: Decrypt the gift wrap
        var rumor = _decryptor.Decrypt(giftWrap, _dvmPrivKey);
        if (rumor == null)
        {
            _logger.LogWarning("Could not decrypt gift wrap {Id}", giftWrap.Id);
            return;
        }

        // Step 2: Verify sender
        var senderPubKey = Nip17Decryptor.GetSenderPubKey(giftWrap, _dvmPrivKey);
        if (senderPubKey == null)
        {
            _logger.LogWarning("Could not determine sender pubkey");
            return;
        }

        if (!_accountMap.TryGetValue(senderPubKey!, out var publishPrivKey))
        {
            _logger.LogDebug("Ignoring DM from non-authorized pubkey: {PubKey}", senderPubKey![..8]);
            return;
        }

        // Skip messages older than 5 minutes to avoid reprocessing old DMs on restart
        var messageAge = DateTimeOffset.UtcNow - (rumor.CreatedAt ?? DateTimeOffset.MinValue);
        if (messageAge > TimeSpan.FromMinutes(5))
        {
            _logger.LogDebug("Skipping old message ({Age:F0}s old): {Content}",
                messageAge.TotalSeconds, rumor.Content?.Substring(0, Math.Min(50, rumor.Content?.Length ?? 0)));
            return;
        }

        var messageText = rumor.Content?.Trim() ?? string.Empty;
        _logger.LogInformation("Received DM from authorized user: {Content}",
            messageText.Substring(0, Math.Min(100, messageText.Length)));

        // Check if this is a reply to a pending summary approval
        if (await HandlePendingReplyAsync(senderPubKey, messageText, publishPrivKey, client, ct))
            return;

        // Step 3: Extract video URL, optional creator npub, and zap split
        var job = _urlExtractor.ParseDmMessage(messageText);
        if (job == null)
        {
            _logger.LogInformation("No supported video URL found in message");
            await _publisher.SendDmReplyAsync(senderPubKey,
                "No supported video URL found in your message.\n\nUsage: <url> [npub] [split%]\n\nOptions:\n• -d — include full description\n• -ns — no summary, publish with title only\n• !edit <prompt> — AI video editing (e.g. !edit make it a cartoon)\n\nSupported: YouTube, TikTok, Instagram, Facebook, X/Twitter",
                _dvmPrivKey, client, ct);
            return;
        }

        // Resolve creator pubkey to hex if provided as npub
        if (!string.IsNullOrEmpty(job.CreatorPubKey) && job.CreatorPubKey.StartsWith("npub"))
        {
            try
            {
                var creatorKey = job.CreatorPubKey.FromNIP19Npub();
                job.CreatorPubKey = creatorKey.ToHex();
            }
            catch
            {
                _logger.LogWarning("Invalid creator npub: {Npub}", job.CreatorPubKey);
                job.CreatorPubKey = null;
            }
        }

        _logger.LogInformation("Found {Platform} URL: {Url} (videoId={VideoId}, creator={Creator}, split={Split}, desc={Desc}, ns={Ns})",
            job.Platform, job.OriginalUrl, job.VideoId, job.CreatorPubKey?[..8] ?? "none",
            job.CreatorZapShare?.ToString() ?? "default", job.IncludeDescription, job.NoSummary);

        // Step 4: Check for duplicates (DB first, then relay fallback)
        var existingUrl = _duplicateTracker.GetExistingBlossomUrl(job.OriginalUrl);
        if (existingUrl == null)
        {
            existingUrl = await _publisher.FindExistingVideoByUrlAsync(
                job.OriginalUrl, publishPrivKey, client, ct);
            if (existingUrl != null)
                _duplicateTracker.MarkProcessed(job.OriginalUrl, existingUrl, null);
        }

        if (existingUrl != null)
        {
            _logger.LogInformation("Duplicate URL, already processed: {BlossomUrl}", existingUrl);
            await _publisher.SendDmReplyAsync(senderPubKey,
                $"This video was already uploaded!\n\n{job.OriginalUrl}\n→ {existingUrl}",
                _dvmPrivKey, client, ct);
            return;
        }

        // Step 5: Download video
        var downloadError = await _downloader.DownloadAsync(job, ct);
        if (downloadError != null)
        {
            await _publisher.SendDmReplyAsync(senderPubKey,
                $"Failed to download video:\n{job.OriginalUrl}\n\nReason: {downloadError}",
                _dvmPrivKey, client, ct);
            return;
        }

        try
        {
            // Step 6: If this is an edit request, process through AI video editor
            if (job.IsEditRequest)
            {
                await ProcessEditRequestAsync(job, senderPubKey, publishPrivKey, client, ct);
                return;
            }

            // Step 7: Upload to Blossom
            var uploadError = await _uploader.UploadAsync(job, publishPrivKey, ct);
            if (uploadError != null)
            {
                await _publisher.SendDmReplyAsync(senderPubKey,
                    $"Failed to upload video to Blossom:\n{job.OriginalUrl}\n\nReason: {uploadError}",
                    _dvmPrivKey, client, ct);
                return;
            }

            // Step 7: Summarization decision
            var description = job.Description ?? job.Title ?? string.Empty;
            var shouldSummarize = !job.NoSummary
                && !job.IncludeDescription
                && description.Length >= _settings.Ollama.MinDescriptionLength;

            if (shouldSummarize)
            {
                // Try to generate a summary and ask for approval
                var summary = await _summarizer.SummarizeAsync(
                    job.Title ?? "", job.Description ?? "", false, ct);

                if (summary != null)
                {
                    // Store the job and wait for user reply
                    _pendingJobs.SetPending(senderPubKey, job, summary);

                    await _publisher.SendDmReplyAsync(senderPubKey,
                        $"Video uploaded! Proposed summary:\n\n\"{summary}\"\n\nReply:\n• yes — publish with this summary\n• shorter — make it shorter\n• ns — publish with no summary (title only)",
                        _dvmPrivKey, client, ct);

                    _logger.LogInformation("Waiting for summary approval from {PubKey}", senderPubKey[..8]);
                    return;
                }

                // Ollama unavailable — fall back to truncated description with hashtags removed
                _logger.LogWarning("Ollama unavailable, falling back to truncated description");
                var cleaned = System.Text.RegularExpressions.Regex.Replace(description, @"#\S+", "").Trim();
                cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ");
                var fallback = cleaned.Length > 150 ? cleaned[..147] + "..." : cleaned;
                job.Title = fallback;
                job.Description = null;
            }

            // Step 8: Publish (no summary or -ns flag)
            await PublishAndConfirmAsync(job, senderPubKey, publishPrivKey, client, ct);
        }
        finally
        {
            // Only cleanup if not pending (pending jobs keep the file for potential re-publish)
            if (!_pendingJobs.HasPending(senderPubKey))
                _downloader.Cleanup(job);
        }
    }

    /// <summary>
    /// Handles replies to pending summary approvals.
    /// Returns true if the message was handled as a reply.
    /// </summary>
    private async Task<bool> HandlePendingReplyAsync(
        string senderPubKey, string message, ECPrivKey publishPrivKey, INostrClient client, CancellationToken ct)
    {
        if (!_pendingJobs.HasPending(senderPubKey))
            return false;

        var normalizedMessage = message.Trim().ToLowerInvariant();

        // If it's a new video URL, cancel the pending job and let it process normally
        var newJob = _urlExtractor.ParseDmMessage(message);
        if (newJob != null)
        {
            var cancelled = _pendingJobs.TakeJob(senderPubKey);
            if (cancelled != null)
            {
                _downloader.Cleanup(cancelled.Job);
                _logger.LogInformation("Cancelled pending job due to new URL from {PubKey}", senderPubKey[..8]);
            }
            return false; // Let the normal flow handle the new URL
        }

        // Handle known commands
        if (normalizedMessage == "yes" || normalizedMessage == "shorter" || normalizedMessage == "ns")
        {
            var pending = _pendingJobs.TakeJob(senderPubKey);
            if (pending == null)
            {
                await _publisher.SendDmReplyAsync(senderPubKey,
                    "That pending job has expired. Please send the video URL again.",
                    _dvmPrivKey, client, ct);
                return true;
            }

            var job = pending.Job;

            try
            {
                switch (normalizedMessage)
                {
                    case "yes":
                        job.Title = pending.ProposedSummary;
                        job.Description = null;
                        job.IncludeDescription = false;
                        await PublishAndConfirmAsync(job, senderPubKey, publishPrivKey, client, ct);
                        break;

                    case "shorter":
                        var shorterSummary = await _summarizer.SummarizeAsync(
                            pending.Job.Title ?? "", pending.Job.Description ?? "", shorter: true, ct);

                        if (shorterSummary != null)
                        {
                            _pendingJobs.SetPending(senderPubKey, job, shorterSummary);
                            await _publisher.SendDmReplyAsync(senderPubKey,
                                $"Shorter summary:\n\n\"{shorterSummary}\"\n\nReply: yes / shorter / ns",
                                _dvmPrivKey, client, ct);
                        }
                        else
                        {
                            await PublishAndConfirmAsync(job, senderPubKey, publishPrivKey, client, ct);
                        }
                        break;

                    case "ns":
                        job.Description = null;
                        job.IncludeDescription = false;
                        await PublishAndConfirmAsync(job, senderPubKey, publishPrivKey, client, ct);
                        break;
                }
            }
            finally
            {
                if (!_pendingJobs.HasPending(senderPubKey))
                    _downloader.Cleanup(job);
            }

            return true;
        }

        // Unrecognized reply while a job is pending — remind them
        await _publisher.SendDmReplyAsync(senderPubKey,
            "You have a pending video awaiting approval.\n\nReply:\n• yes — publish with proposed summary\n• shorter — make it shorter\n• ns — publish with no summary",
            _dvmPrivKey, client, ct);
        return true;
    }

    /// <summary>
    /// Processes a video editing request: upload source to Blossom for a public URL,
    /// send to AI for editing, upload edited result to Blossom, send preview link.
    /// The user can then reply "yes" to publish or provide feedback.
    /// </summary>
    private async Task ProcessEditRequestAsync(
        Models.VideoJob job, string senderPubKey, ECPrivKey publishPrivKey, INostrClient client, CancellationToken ct)
    {
        try
        {
            await _publisher.SendDmReplyAsync(senderPubKey,
                $"Processing video edit request...\n\nPrompt: \"{job.EditPrompt}\"\n\nThis may take several minutes.",
                _dvmPrivKey, client, ct);

            // Step 1: Upload source video to Blossom to get a public URL for Replicate
            var sourceUploadError = await _uploader.UploadAsync(job, publishPrivKey, ct);
            if (sourceUploadError != null)
            {
                var friendlyError = await _summarizer.FormatErrorAsync(sourceUploadError, "uploading source video to Blossom server", ct);
                await _publisher.SendDmReplyAsync(senderPubKey,
                    $"Video edit failed:\n\n{friendlyError}",
                    _dvmPrivKey, client, ct);
                return;
            }

            var sourceBlossomUrl = job.BlossomUrl!;
            _logger.LogInformation("Source video uploaded to Blossom: {Url}", sourceBlossomUrl);

            // Step 2: Edit the video via Replicate API using the Blossom URL
            var editError = await _videoEditor.EditAsync(job, sourceBlossomUrl, ct);
            if (editError != null)
            {
                var friendlyError = await _summarizer.FormatErrorAsync(editError, "AI video editing via Replicate", ct);
                await _publisher.SendDmReplyAsync(senderPubKey,
                    $"Video edit failed:\n\n{friendlyError}",
                    _dvmPrivKey, client, ct);
                return;
            }

            // Step 3: Upload the edited video to Blossom
            // Reset upload fields so BlossomUploader treats this as a new upload
            job.LocalFilePath = job.EditedFilePath;
            job.BlossomUrl = null;
            job.FileHash = null;
            job.FileSize = null;
            job.MimeType = "video/mp4";

            var editedUploadError = await _uploader.UploadAsync(job, publishPrivKey, ct);
            if (editedUploadError != null)
            {
                var friendlyError = await _summarizer.FormatErrorAsync(editedUploadError, "uploading edited video to Blossom server", ct);
                await _publisher.SendDmReplyAsync(senderPubKey,
                    $"Video edit failed:\n\n{friendlyError}",
                    _dvmPrivKey, client, ct);
                return;
            }

            // Generate a description combining original video info with the edit prompt
            var originalTitle = job.Title ?? "";
            var originalDesc = job.Description ?? "";
            var editedDescription = await _summarizer.GenerateEditedVideoDescriptionAsync(
                originalTitle, originalDesc, job.EditPrompt ?? "", ct);

            job.Title = editedDescription ?? originalTitle;
            job.Description = null;
            _pendingJobs.SetPending(senderPubKey, job, job.Title);

            await _publisher.SendDmReplyAsync(senderPubKey,
                $"Video edited and uploaded!\n\nPreview: {job.BlossomUrl}\n\nEdit prompt: \"{job.EditPrompt}\"\n\nReply:\n• yes — publish to Nostr\n• ns — discard (don't publish)",
                _dvmPrivKey, client, ct);

            _logger.LogInformation("Edit complete for {Url}, awaiting approval. Blossom: {BlossomUrl}",
                job.OriginalUrl, job.BlossomUrl);
        }
        finally
        {
            // Clean up edited file if not pending
            if (!_pendingJobs.HasPending(senderPubKey))
            {
                _videoEditor.CleanupEdited(job);
                _downloader.Cleanup(job);
            }
        }
    }

    /// <summary>
    /// Publishes the event and sends a confirmation DM.
    /// </summary>
    private async Task PublishAndConfirmAsync(
        Models.VideoJob job, string senderPubKey, ECPrivKey publishPrivKey, INostrClient client, CancellationToken ct)
    {
        var eventId = await _publisher.PublishVideoEventAsync(job, publishPrivKey, client, ct);

        _duplicateTracker.MarkProcessed(job.OriginalUrl, job.BlossomUrl!, eventId);
        _duplicateTracker.TrackCreatorEarnings(
            job.OriginalUrl, job.Platform, job.PlatformUserId,
            job.CreatorPubKey, eventId, job.BlossomUrl,
            job.CreatorZapShare ?? _settings.Nostr.DefaultCreatorZapShare);

        var replyMessage = $"Video published!\n\nSource: {job.OriginalUrl}\nBlossom: {job.BlossomUrl}";
        if (eventId != null)
            replyMessage += $"\nEvent: {eventId}";
        if (!string.IsNullOrEmpty(job.CreatorPubKey))
            replyMessage += $"\nCreator zap split: {job.CreatorZapShare ?? _settings.Nostr.DefaultCreatorZapShare}%";

        await _publisher.SendDmReplyAsync(senderPubKey, replyMessage, _dvmPrivKey, client, ct);

        _logger.LogInformation("Successfully processed {Url} -> {BlossomUrl}", job.OriginalUrl, job.BlossomUrl);
    }
}
