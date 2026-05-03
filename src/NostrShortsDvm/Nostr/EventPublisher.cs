using System.Text.Json;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using NostrShortsDvm.Config;
using NostrShortsDvm.Models;

namespace NostrShortsDvm.Nostr;

public class EventPublisher
{
    private readonly AppSettings _settings;
    private readonly ILogger<EventPublisher> _logger;

    public EventPublisher(AppSettings settings, ILogger<EventPublisher> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Checks relays for an existing published event with the given original URL in an "r" tag.
    /// Returns the blossom URL if found, null otherwise.
    /// </summary>
    public async Task<string?> FindExistingVideoByUrlAsync(
        string originalUrl,
        ECPrivKey publishKey,
        INostrClient client,
        CancellationToken ct)
    {
        try
        {
            var publishPubKey = publishKey.CreateXOnlyPubKey().ToHex();
            var eventKind = _settings.Nostr.EventKind;

            var filter = new NostrSubscriptionFilter
            {
                Kinds = [eventKind],
                Authors = [publishPubKey],
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["#r"] = JsonSerializer.SerializeToElement(new[] { originalUrl })
                }
            };

            var subId = $"check-{Guid.NewGuid().ToString()[..8]}";
            var events = new List<NostrEvent>();

            void OnEventsReceived(object? sender, (string subscriptionId, NostrEvent[] events) args)
            {
                if (args.subscriptionId == subId)
                    events.AddRange(args.events);
            }

            client.EventsReceived += OnEventsReceived;

            try
            {
                await client.CreateSubscription(subId, [filter], ct);

                // Give relays a moment to respond
                await Task.Delay(2000, ct);

                await client.CloseSubscription(subId, ct);
            }
            finally
            {
                client.EventsReceived -= OnEventsReceived;
            }

            if (events.Count > 0)
            {
                var evt = events.First();
                // Find the blossom URL from the "url" tag (NIP-71) or first "r" tag that's not the original
                var urlTag = evt.Tags?.FirstOrDefault(t => t.TagIdentifier == "url")?.Data?.FirstOrDefault();
                if (urlTag != null)
                {
                    _logger.LogInformation("Found existing event on relay for {Url}: {BlossomUrl}", originalUrl, urlTag);
                    return urlTag;
                }

                var rTags = evt.Tags?.Where(t => t.TagIdentifier == "r").ToList();
                var blossomTag = rTags?.FirstOrDefault(t => t.Data?.FirstOrDefault() != originalUrl)?.Data?.FirstOrDefault();
                if (blossomTag != null)
                {
                    _logger.LogInformation("Found existing event on relay for {Url}: {BlossomUrl}", originalUrl, blossomTag);
                    return blossomTag;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check relays for existing video");
        }

        return null;
    }

    /// <summary>
    /// Publishes a nostr event (kind 1 or kind 34235) pointing to the blossom video.
    /// If kind 34235, also publishes a kind 1 quote-repost so clients that don't support NIP-71 still show it.
    /// </summary>
    public async Task<string?> PublishVideoEventAsync(
        VideoJob job,
        ECPrivKey publishKey,
        INostrClient client,
        CancellationToken ct)
    {
        var publishPubKeyHex = publishKey.CreateXOnlyPubKey().ToHex();

        var evt = _settings.Nostr.EventKind == 34235
            ? CreateNip71Event(job, publishPubKeyHex)
            : CreateKind1Event(job, publishPubKeyHex);

        await evt.ComputeIdAndSignAsync(publishKey);

        _logger.LogInformation("Publishing kind {Kind} event: {Id}", evt.Kind, evt.Id);

        await client.PublishEvent(evt, ct);

        job.EventId = evt.Id;

        // If NIP-71, also publish a kind 1 quote-repost for client compatibility
        if (_settings.Nostr.EventKind == 34235 && evt.Id != null)
        {
            var quoteNote = CreateQuoteRepost(evt.Id, publishPubKeyHex, job);
            await quoteNote.ComputeIdAndSignAsync(publishKey);

            _logger.LogInformation("Publishing kind 1 quote-repost: {Id}", quoteNote.Id);
            await client.PublishEvent(quoteNote, ct);
        }

        return evt.Id;
    }

    /// <summary>
    /// Sends a NIP-17 DM reply back to the sender confirming the upload.
    /// </summary>
    public async Task SendDmReplyAsync(
        string recipientPubKeyHex,
        string message,
        ECPrivKey senderPrivKey,
        INostrClient client,
        CancellationToken ct)
    {
        try
        {
            var recipientPubKey = NostrExtensions.ParsePubKey(recipientPubKeyHex);
            var senderPubKey = senderPrivKey.CreateXOnlyPubKey();

            // Create the rumor (kind 14)
            var rumor = new NostrEvent
            {
                Kind = 14,
                Content = message,
                CreatedAt = DateTimeOffset.UtcNow
            };
            rumor.SetTag("p", recipientPubKeyHex);

            var rumorJson = JsonSerializer.Serialize(rumor);

            // Create the seal (kind 13)
            var seal = new NostrEvent
            {
                Kind = 13,
                Content = NIP44.Encrypt(senderPrivKey, recipientPubKey, rumorJson),
                CreatedAt = RandomizeTimestamp()
            };
            await seal.ComputeIdAndSignAsync(senderPrivKey);
            var sealJson = JsonSerializer.Serialize(seal);

            // Create the gift wrap (kind 1059) using a random ephemeral key
            var ephemeralKey = ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var giftWrap = new NostrEvent
            {
                Kind = 1059,
                Content = NIP44.Encrypt(ephemeralKey, recipientPubKey, sealJson),
                CreatedAt = RandomizeTimestamp()
            };
            giftWrap.SetTag("p", recipientPubKeyHex);
            await giftWrap.ComputeIdAndSignAsync(ephemeralKey);

            await client.PublishEvent(giftWrap, ct);

            _logger.LogInformation("Sent DM reply to {Recipient}", recipientPubKeyHex[..8]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send DM reply");
        }
    }

    private NostrEvent CreateKind1Event(VideoJob job, string publishPubKeyHex)
    {
        var contentParts = new List<string>();
        if (!string.IsNullOrEmpty(job.Title))
            contentParts.Add("> " + job.Title.Replace("\n", "\n> "));
        if (job.IncludeDescription && !string.IsNullOrEmpty(job.Description))
            contentParts.Add("> " + job.Description.Replace("\n", "\n> "));
        contentParts.Add(job.BlossomUrl!);

        var content = string.Join("\n\n", contentParts);

        var evt = new NostrEvent
        {
            Kind = 1,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow
        };

        evt.Tags ??= [];
        evt.Tags.Add(new NostrEventTag { TagIdentifier = "r", Data = [job.BlossomUrl!] });
        evt.Tags.Add(new NostrEventTag { TagIdentifier = "r", Data = [job.OriginalUrl!] });
        evt.Tags.Add(new NostrEventTag { TagIdentifier = "t", Data = ["shorts"] });

        // Attribution: origin tag
        if (!string.IsNullOrEmpty(job.VideoId))
            evt.Tags.Add(new NostrEventTag { TagIdentifier = "origin", Data = [job.Platform, job.VideoId, job.OriginalUrl] });

        // Attribution: creator p tag
        if (!string.IsNullOrEmpty(job.CreatorPubKey))
            evt.Tags.Add(new NostrEventTag { TagIdentifier = "p", Data = [job.CreatorPubKey] });

        // Zap splits
        AddZapTags(evt, job, publishPubKeyHex);

        return evt;
    }

    private NostrEvent CreateNip71Event(VideoJob job, string publishPubKeyHex)
    {
        var evt = new NostrEvent
        {
            Kind = 34235,
            Content = job.Title ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // NIP-71 tags
        evt.Tags ??= [];
        evt.Tags.Add(new NostrEventTag { TagIdentifier = "url", Data = [job.BlossomUrl!] });
        evt.Tags.Add(new NostrEventTag { TagIdentifier = "m", Data = [job.MimeType ?? "video/mp4"] });
        if (job.FileHash != null)
            evt.Tags.Add(new NostrEventTag { TagIdentifier = "x", Data = [job.FileHash] });
        if (job.FileSize.HasValue)
            evt.Tags.Add(new NostrEventTag { TagIdentifier = "size", Data = [job.FileSize.Value.ToString()] });
        evt.Tags.Add(new NostrEventTag { TagIdentifier = "t", Data = ["shorts"] });
        evt.Tags.Add(new NostrEventTag { TagIdentifier = "r", Data = [job.OriginalUrl!] });

        // d-tag for addressable event
        evt.SetTag("d", job.FileHash ?? Guid.NewGuid().ToString());

        // Attribution: origin tag
        if (!string.IsNullOrEmpty(job.VideoId))
            evt.Tags.Add(new NostrEventTag { TagIdentifier = "origin", Data = [job.Platform, job.VideoId, job.OriginalUrl] });

        // Attribution: creator p tag
        if (!string.IsNullOrEmpty(job.CreatorPubKey))
            evt.Tags.Add(new NostrEventTag { TagIdentifier = "p", Data = [job.CreatorPubKey] });

        // Zap splits
        AddZapTags(evt, job, publishPubKeyHex);

        return evt;
    }

    private void AddZapTags(NostrEvent evt, VideoJob job, string publishPubKeyHex)
    {
        var defaultRelay = _settings.Nostr.Relays.FirstOrDefault() ?? "wss://relay.damus.io";
        var defaultShare = _settings.Nostr.DefaultCreatorZapShare;

        if (!string.IsNullOrEmpty(job.CreatorPubKey))
        {
            var creatorShare = job.CreatorZapShare ?? defaultShare;
            var publisherShare = 100 - creatorShare;

            evt.Tags.Add(new NostrEventTag { TagIdentifier = "zap", Data = [publishPubKeyHex, defaultRelay, publisherShare.ToString()] });
            evt.Tags.Add(new NostrEventTag { TagIdentifier = "zap", Data = [job.CreatorPubKey, defaultRelay, creatorShare.ToString()] });
        }
        else
        {
            // 100% to publisher
            evt.Tags.Add(new NostrEventTag { TagIdentifier = "zap", Data = [publishPubKeyHex, defaultRelay, "100"] });
        }
    }

    /// <summary>
    /// Creates a kind 1 note that quotes the NIP-71 video event, so clients that don't
    /// support kind 34235 will still display the post in the feed.
    /// Content includes description text + Blossom video URL for universal playback.
    /// </summary>
    private NostrEvent CreateQuoteRepost(string videoEventId, string publishPubKeyHex, VideoJob job)
    {
        var nevent = EncodeNevent(videoEventId, publishPubKeyHex, _settings.Nostr.Relays.FirstOrDefault());

        // Build content: optional title/description, then blossom URL, then nevent reference
        var contentParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.Title))
            contentParts.Add(job.Title);
        if (!string.IsNullOrWhiteSpace(job.BlossomUrl))
            contentParts.Add(job.BlossomUrl);
        contentParts.Add($"nostr:{nevent}");

        var evt = new NostrEvent
        {
            Kind = 1,
            Content = string.Join("\n", contentParts),
            CreatedAt = DateTimeOffset.UtcNow
        };

        evt.Tags ??= [];
        evt.Tags.Add(new NostrEventTag { TagIdentifier = "q", Data = [videoEventId, "", publishPubKeyHex] });

        return evt;
    }

    /// <summary>
    /// Encodes an event ID + optional relay + pubkey into NIP-19 nevent bech32 format.
    /// TLV: type 0 = event id (32 bytes), type 1 = relay (utf8), type 2 = author pubkey (32 bytes)
    /// </summary>
    private static string EncodeNevent(string eventIdHex, string pubkeyHex, string? relay)
    {
        var tlv = new List<byte>();

        // Type 0: event id
        var eventIdBytes = Convert.FromHexString(eventIdHex);
        tlv.Add(0);
        tlv.Add((byte)eventIdBytes.Length);
        tlv.AddRange(eventIdBytes);

        // Type 1: relay (optional)
        if (!string.IsNullOrEmpty(relay))
        {
            var relayBytes = System.Text.Encoding.UTF8.GetBytes(relay);
            tlv.Add(1);
            tlv.Add((byte)relayBytes.Length);
            tlv.AddRange(relayBytes);
        }

        // Type 2: author pubkey
        var pubkeyBytes = Convert.FromHexString(pubkeyHex);
        tlv.Add(2);
        tlv.Add((byte)pubkeyBytes.Length);
        tlv.AddRange(pubkeyBytes);

        return Bech32.Encode("nevent", tlv.ToArray());
    }

    private static DateTimeOffset RandomizeTimestamp()
    {
        // Randomize timestamp within the last 2 days for privacy (NIP-59)
        var random = new Random();
        var secondsOffset = random.Next(0, 172800); // 0 to 48 hours
        return DateTimeOffset.UtcNow.AddSeconds(-secondsOffset);
    }
}
