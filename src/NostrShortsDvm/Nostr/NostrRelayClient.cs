using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NostrShortsDvm.Config;

namespace NostrShortsDvm.Nostr;

/// <summary>
/// Manages connections to nostr relays and subscribes to NIP-17 gift wrap events.
/// </summary>
public class NostrRelayClient : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ILogger<NostrRelayClient> _logger;
    private CompositeNostrClient? _client;
    private readonly HashSet<string> _processedEventIds = new();

    public event EventHandler<NostrEvent>? GiftWrapReceived;

    public NostrRelayClient(AppSettings settings, ILogger<NostrRelayClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public INostrClient Client => _client ?? throw new InvalidOperationException("Not connected");

    public async Task ConnectAsync(ECPrivKey privateKey, CancellationToken ct)
    {
        var relayUris = _settings.Nostr.Relays.Select(r => new Uri(r)).ToArray();
        _client = new CompositeNostrClient(relayUris);

        _logger.LogInformation("Connecting to {Count} relays...", relayUris.Length);

        await _client.ConnectAndWaitUntilConnected(ct);

        _logger.LogInformation("Connected to relays");

        // Subscribe to kind 1059 (gift wrap) events addressed to our pubkey
        var ourPubKey = privateKey.CreateXOnlyPubKey().ToHex();

        _client.EventsReceived += (sender, args) =>
        {
            _logger.LogInformation("Received {Count} events from subscription {Sub}",
                args.events.Length, args.subscriptionId);

            foreach (var evt in args.events)
            {
                if (evt.Kind == 1059)
                {
                    // Deduplicate: CompositeNostrClient delivers the same event from each relay
                    lock (_processedEventIds)
                    {
                        if (!_processedEventIds.Add(evt.Id!))
                        {
                            _logger.LogDebug("Skipping duplicate gift wrap event: {Id}", evt.Id);
                            continue;
                        }
                    }

                    _logger.LogInformation("Received gift wrap event: {Id} (created {CreatedAt})",
                        evt.Id, evt.CreatedAt);
                    GiftWrapReceived?.Invoke(this, evt);
                }
                else
                {
                    _logger.LogDebug("Ignoring non-gift-wrap event kind {Kind}: {Id}", evt.Kind, evt.Id);
                }
            }
        };

        // No Since filter — process all gift wraps including those sent while offline.
        // The DuplicateTracker prevents reprocessing of already-handled URLs.
        var filter = new NostrSubscriptionFilter
        {
            Kinds = [1059],
            ReferencedPublicKeys = [ourPubKey],
        };

        await _client.CreateSubscription("nip17-dms", [filter], ct);

        _logger.LogInformation("Subscribed to NIP-17 gift wraps for {PubKey}", ourPubKey[..8]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            _client.Dispose();
        }
    }
}
