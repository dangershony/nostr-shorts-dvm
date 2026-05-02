using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using NostrShortsDvm.Config;
using NostrShortsDvm.Nostr;

namespace NostrShortsDvm.Services;

/// <summary>
/// Orchestrates the full pipeline: decrypt DM -> extract URL -> check dupe -> download -> upload -> publish -> reply.
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
        ILogger<MessageProcessor> logger)
    {
        _settings = settings;
        _decryptor = decryptor;
        _urlExtractor = urlExtractor;
        _duplicateTracker = duplicateTracker;
        _downloader = downloader;
        _uploader = uploader;
        _publisher = publisher;
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

        _logger.LogInformation("Received DM from authorized user: {Content}",
            rumor.Content?.Substring(0, Math.Min(100, rumor.Content?.Length ?? 0)));

        // Step 3: Extract video URL, optional creator npub, and zap split
        var job = _urlExtractor.ParseDmMessage(rumor.Content ?? string.Empty);
        if (job == null)
        {
            _logger.LogInformation("No supported video URL found in message");
            await _publisher.SendDmReplyAsync(senderPubKey,
                "No supported video URL found in your message. Supported: YouTube, TikTok, Instagram, Facebook, X/Twitter.",
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

        _logger.LogInformation("Found {Platform} URL: {Url} (videoId={VideoId}, creator={Creator}, split={Split})",
            job.Platform, job.OriginalUrl, job.VideoId, job.CreatorPubKey?[..8] ?? "none", job.CreatorZapShare?.ToString() ?? "default");

        // Step 4: Check for duplicates (DB first, then relay fallback)
        var existingUrl = _duplicateTracker.GetExistingBlossomUrl(job.OriginalUrl);
        if (existingUrl == null)
        {
            // Fallback: check relays for an existing event with this URL tag
            existingUrl = await _publisher.FindExistingVideoByUrlAsync(
                job.OriginalUrl, publishPrivKey, client, ct);
            if (existingUrl != null)
            {
                // Re-populate the DB so we don't query relays again next time
                _duplicateTracker.MarkProcessed(job.OriginalUrl, existingUrl, null);
            }
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
            // Step 6: Upload to Blossom
            var uploadError = await _uploader.UploadAsync(job, publishPrivKey, ct);
            if (uploadError != null)
            {
                await _publisher.SendDmReplyAsync(senderPubKey,
                    $"Failed to upload video to Blossom:\n{job.OriginalUrl}\n\nReason: {uploadError}",
                    _dvmPrivKey, client, ct);
                return;
            }

            // Step 7: Publish nostr event
            var eventId = await _publisher.PublishVideoEventAsync(job, publishPrivKey, client, ct);

            // Step 8: Track as processed
            _duplicateTracker.MarkProcessed(job.OriginalUrl, job.BlossomUrl!, eventId);

            // Step 8b: Track creator earnings
            _duplicateTracker.TrackCreatorEarnings(
                job.OriginalUrl, job.Platform, job.PlatformUserId,
                job.CreatorPubKey, eventId, job.BlossomUrl,
                job.CreatorZapShare ?? _settings.Nostr.DefaultCreatorZapShare);

            // Step 9: Reply with confirmation
            var replyMessage = $"Video uploaded and published!\n\nSource: {job.OriginalUrl}\nBlossom: {job.BlossomUrl}";
            if (eventId != null)
                replyMessage += $"\nEvent: {eventId}";
            if (!string.IsNullOrEmpty(job.CreatorPubKey))
                replyMessage += $"\nCreator zap split: {job.CreatorZapShare ?? _settings.Nostr.DefaultCreatorZapShare}%";

            await _publisher.SendDmReplyAsync(senderPubKey, replyMessage, _dvmPrivKey, client, ct);

            _logger.LogInformation("Successfully processed {Url} -> {BlossomUrl}", job.OriginalUrl, job.BlossomUrl);
        }
        finally
        {
            // Cleanup temp file
            _downloader.Cleanup(job);
        }
    }
}
