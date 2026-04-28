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
            foreach (var evt in args.events)
            {
                if (evt.Kind == 1059)
                {
                    _logger.LogDebug("Received gift wrap event: {Id}", evt.Id);
                    GiftWrapReceived?.Invoke(this, evt);
                }
            }
        };

        var filter = new NostrSubscriptionFilter
        {
            Kinds = [1059],
            ReferencedPublicKeys = [ourPubKey],
            Since = DateTimeOffset.UtcNow
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
