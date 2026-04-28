using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
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
    private ECPrivKey _publishPrivKey = null!;
    private string _listenFromPubKeyHex = null!;

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

    public void Initialize(ECPrivKey dvmPrivKey, ECPrivKey publishPrivKey, string listenFromPubKeyHex)
    {
        _dvmPrivKey = dvmPrivKey;
        _publishPrivKey = publishPrivKey;
        _listenFromPubKeyHex = listenFromPubKeyHex;
    }

    public async Task ProcessGiftWrapAsync(NostrEvent giftWrap, INostrClient client, CancellationToken ct)
    {
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

        if (!string.Equals(senderPubKey, _listenFromPubKeyHex, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Ignoring DM from non-authorized pubkey: {PubKey}", senderPubKey[..8]);
            return;
        }

        _logger.LogInformation("Received DM from authorized user: {Content}",
            rumor.Content?.Substring(0, Math.Min(100, rumor.Content?.Length ?? 0)));

        // Step 3: Extract video URL
        var job = _urlExtractor.Extract(rumor.Content ?? string.Empty);
        if (job == null)
        {
            _logger.LogInformation("No supported video URL found in message");
            await _publisher.SendDmReplyAsync(senderPubKey,
                "No supported video URL found in your message. Supported: YouTube, TikTok, Instagram, Facebook, X/Twitter.",
                _dvmPrivKey, client, ct);
            return;
        }

        _logger.LogInformation("Found {Platform} URL: {Url}", job.Platform, job.OriginalUrl);

        // Step 4: Check for duplicates
        var existingUrl = _duplicateTracker.GetExistingBlossomUrl(job.OriginalUrl);
        if (existingUrl != null)
        {
            _logger.LogInformation("Duplicate URL, already processed: {BlossomUrl}", existingUrl);
            await _publisher.SendDmReplyAsync(senderPubKey,
                $"Already processed! {existingUrl}",
                _dvmPrivKey, client, ct);
            return;
        }

        // Step 5: Download video
        if (!await _downloader.DownloadAsync(job, ct))
        {
            await _publisher.SendDmReplyAsync(senderPubKey,
                $"Failed to download video from {job.OriginalUrl}",
                _dvmPrivKey, client, ct);
            return;
        }

        try
        {
            // Step 6: Upload to Blossom
            if (!await _uploader.UploadAsync(job, _publishPrivKey, ct))
            {
                await _publisher.SendDmReplyAsync(senderPubKey,
                    $"Failed to upload video to Blossom server",
                    _dvmPrivKey, client, ct);
                return;
            }

            // Step 7: Publish nostr event
            var eventId = await _publisher.PublishVideoEventAsync(job, _publishPrivKey, client, ct);

            // Step 8: Track as processed
            _duplicateTracker.MarkProcessed(job.OriginalUrl, job.BlossomUrl!, eventId);

            // Step 9: Reply with confirmation
            var replyMessage = $"Video uploaded and published!\n\nBlossom: {job.BlossomUrl}";
            if (eventId != null)
                replyMessage += $"\nEvent: {eventId}";

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
