using System.Text.Json;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;

// Config - using the test key
var nsec = "nsec1sl8m6zp6hhuv52kl3p7989v6tc8cf3qhn5ucq6e9j5rc8rucd9js05a5zp";
var relays = new[]
{
    new Uri("wss://relay.damus.io"),
    new Uri("wss://nos.lol"),
};

// Parse key
var privKey = nsec.FromNIP19Nsec();
var pubKeyHex = privKey.CreateXOnlyPubKey().ToHex();
Console.WriteLine($"DVM pubkey: {pubKeyHex}");

// Connect
var client = new CompositeNostrClient(relays);
Console.WriteLine($"Connecting to {relays.Length} relays...");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await client.ConnectAndWaitUntilConnected(cts.Token);
Console.WriteLine("Connected!");

// Listen for ALL events
int eventCount = 0;
client.EventsReceived += (sender, args) =>
{
    foreach (var evt in args.events)
    {
        eventCount++;
        Console.WriteLine($"\n=== EVENT RECEIVED ===");
        Console.WriteLine($"  Subscription: {args.subscriptionId}");
        Console.WriteLine($"  Kind: {evt.Kind}");
        Console.WriteLine($"  Id: {evt.Id}");
        Console.WriteLine($"  PubKey: {evt.PublicKey}");
        Console.WriteLine($"  CreatedAt: {evt.CreatedAt}");
        Console.WriteLine($"  Tags: {JsonSerializer.Serialize(evt.Tags)}");
        Console.WriteLine($"  Content length: {evt.Content?.Length ?? 0}");

        if (evt.Kind == 1059)
        {
            Console.WriteLine($"\n  >>> GIFT WRAP DETECTED! Attempting decryption...");
            try
            {
                var giftWrapPubKey = NostrExtensions.ParsePubKey(evt.PublicKey);
                var sealJson = NIP44.Decrypt(privKey, giftWrapPubKey, evt.Content);
                Console.WriteLine($"  Seal JSON length: {sealJson?.Length ?? 0}");

                if (!string.IsNullOrEmpty(sealJson))
                {
                    var seal = JsonSerializer.Deserialize<NostrEvent>(sealJson);
                    Console.WriteLine($"  Seal kind: {seal?.Kind}");
                    Console.WriteLine($"  Seal pubkey: {seal?.PublicKey}");

                    if (seal != null)
                    {
                        var sealPubKey = NostrExtensions.ParsePubKey(seal.PublicKey);
                        var rumorJson = NIP44.Decrypt(privKey, sealPubKey, seal.Content);
                        Console.WriteLine($"  Rumor JSON length: {rumorJson?.Length ?? 0}");

                        if (!string.IsNullOrEmpty(rumorJson))
                        {
                            var rumor = JsonSerializer.Deserialize<NostrEvent>(rumorJson);
                            Console.WriteLine($"  Rumor kind: {rumor?.Kind}");
                            Console.WriteLine($"  Rumor content: {rumor?.Content}");
                            Console.WriteLine($"  Rumor pubkey: {rumor?.PublicKey}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Decryption error: {ex.Message}");
            }
        }
    }
};

// Subscribe with NO Since filter to catch historical events too
Console.WriteLine("\n--- Test 1: Subscribing for historical gift wraps (no Since filter) ---");
var filterAll = new NostrSubscriptionFilter
{
    Kinds = [1059],
    ReferencedPublicKeys = [pubKeyHex],
};
await client.CreateSubscription("test-all", [filterAll], cts.Token);

// Also subscribe to ALL kinds addressed to us (in case gift wraps have different tags)
Console.WriteLine("--- Test 2: Subscribing for ALL event kinds addressed to us ---");
var filterAnyKind = new NostrSubscriptionFilter
{
    ReferencedPublicKeys = [pubKeyHex],
    Limit = 20,
};
await client.CreateSubscription("test-any-kind", [filterAnyKind], cts.Token);

Console.WriteLine($"\nListening... (press Ctrl+C to stop)");
Console.WriteLine($"Send a DM to npub for pubkey: {pubKeyHex}");
Console.WriteLine();

// Wait and periodically report
try
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(10000, cts.Token);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Events received so far: {eventCount}");
    }
}
catch (OperationCanceledException) { }

Console.WriteLine($"\nTotal events received: {eventCount}");
client.Dispose();
