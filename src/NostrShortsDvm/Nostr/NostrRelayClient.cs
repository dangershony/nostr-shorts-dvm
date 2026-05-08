using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NostrShortsDvm.Config;

namespace NostrShortsDvm.Nostr;

/// <summary>
/// Manages connections to nostr relays and subscribes to NIP-17 gift wrap events.
/// Includes periodic reconnection to handle silent WebSocket disconnects.
/// </summary>
public class NostrRelayClient : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ILogger<NostrRelayClient> _logger;
    private CompositeNostrClient? _client;
    private readonly HashSet<string> _processedEventIds = new();
    private readonly object _lock = new();

    private ECPrivKey _privateKey;
    private CancellationToken _ct;
    private Timer? _reconnectTimer;
    private bool _isReconnecting;
    private DateTimeOffset _lastEventReceived = DateTimeOffset.UtcNow;

    /// <summary>
    /// How often to check connection health and force reconnect (default: 10 minutes).
    /// </summary>
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// If no events (including EOSE) received within this window, force reconnect.
    /// </summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(15);

    public event EventHandler<NostrEvent>? GiftWrapReceived;

    public NostrRelayClient(AppSettings settings, ILogger<NostrRelayClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public INostrClient Client
    {
        get
        {
            lock (_lock)
            {
                return _client ?? throw new InvalidOperationException("Not connected");
            }
        }
    }

    public async Task ConnectAsync(ECPrivKey privateKey, CancellationToken ct)
    {
        _privateKey = privateKey;
        _ct = ct;

        await ConnectInternalAsync();

        // Start periodic reconnect timer
        _reconnectTimer = new Timer(
            _ => _ = ReconnectIfNeededAsync(),
            null,
            ReconnectInterval,
            ReconnectInterval);
    }

    private async Task ConnectInternalAsync()
    {
        var relayUris = _settings.Nostr.Relays.Select(r => new Uri(r)).ToArray();
        var newClient = new CompositeNostrClient(relayUris);

        _logger.LogInformation("Connecting to {Count} relays...", relayUris.Length);

        await newClient.ConnectAndWaitUntilConnected(_ct);

        _logger.LogInformation("Connected to relays");

        var ourPubKey = _privateKey.CreateXOnlyPubKey().ToHex();

        newClient.EventsReceived += (sender, args) =>
        {
            _lastEventReceived = DateTimeOffset.UtcNow;

            foreach (var evt in args.events)
            {
                if (evt.Kind == 1059)
                {
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

        newClient.EoseReceived += (sender, sub) =>
        {
            _lastEventReceived = DateTimeOffset.UtcNow;
            _logger.LogDebug("EOSE received for subscription {Sub}", sub);
        };

        var filter = new NostrSubscriptionFilter
        {
            Kinds = [1059],
            ReferencedPublicKeys = [ourPubKey],
        };

        await newClient.CreateSubscription("nip17-dms", [filter], _ct);

        _logger.LogInformation("Subscribed to NIP-17 gift wraps for {PubKey}", ourPubKey[..8]);

        // Swap in the new client, dispose the old one
        CompositeNostrClient? oldClient;
        lock (_lock)
        {
            oldClient = _client;
            _client = newClient;
        }

        if (oldClient != null)
        {
            try { oldClient.Dispose(); }
            catch { /* ignore */ }
        }

        _lastEventReceived = DateTimeOffset.UtcNow;
    }

    private async Task ReconnectIfNeededAsync()
    {
        if (_isReconnecting)
            return;

        var timeSinceLastEvent = DateTimeOffset.UtcNow - _lastEventReceived;

        // Only force reconnect if the connection looks stale
        if (timeSinceLastEvent < StaleThreshold)
        {
            _logger.LogDebug("Relay connection healthy (last activity {Seconds:F0}s ago)", timeSinceLastEvent.TotalSeconds);
            return;
        }

        _isReconnecting = true;
        try
        {
            _logger.LogWarning("No relay activity for {Minutes:F1} minutes, reconnecting...",
                timeSinceLastEvent.TotalMinutes);
            await ConnectInternalAsync();
            _logger.LogInformation("Reconnected to relays successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconnect to relays, will retry in {Minutes} minutes",
                ReconnectInterval.TotalMinutes);
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    /// <summary>
    /// Returns true if the connection appears healthy (received activity recently).
    /// Used by Docker health checks.
    /// </summary>
    public bool IsHealthy()
    {
        var timeSinceLastEvent = DateTimeOffset.UtcNow - _lastEventReceived;
        return timeSinceLastEvent < StaleThreshold && _client != null;
    }

    public async ValueTask DisposeAsync()
    {
        _reconnectTimer?.Dispose();

        lock (_lock)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
