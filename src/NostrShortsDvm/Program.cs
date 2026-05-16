using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using NostrShortsDvm.Config;
using NostrShortsDvm.Nostr;
using NostrShortsDvm.Services;

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var settings = new AppSettings();
configuration.Bind(settings);

// Validate required settings
if (string.IsNullOrEmpty(settings.Nostr.PrivateKey))
{
    Console.Error.WriteLine("ERROR: Nostr__PrivateKey is required");
    return 1;
}

if (settings.Nostr.Accounts.Length == 0 && string.IsNullOrEmpty(settings.Nostr.ListenFromNpub))
{
    Console.Error.WriteLine("ERROR: At least one account (Nostr__Accounts__0) or Nostr__ListenFromNpub is required");
    return 1;
}

if (string.IsNullOrEmpty(settings.Blossom.ServerUrl))
{
    Console.Error.WriteLine("ERROR: Blossom__ServerUrl is required");
    return 1;
}

// Set up DI
var services = new ServiceCollection();
services.AddLogging(builder =>
{
    builder.AddConfiguration(configuration.GetSection("Logging"));
    builder.AddConsole();
});
services.AddSingleton(settings);
services.AddSingleton<Nip17Decryptor>();
services.AddSingleton<UrlExtractor>();
services.AddSingleton<DuplicateTracker>();
services.AddSingleton<VideoDownloader>();
services.AddSingleton<BlossomUploader>();
services.AddSingleton<EventPublisher>();
services.AddSingleton<NostrRelayClient>();
services.AddSingleton<OllamaSummarizer>();
services.AddSingleton<VideoEditor>();
services.AddSingleton<PendingJobTracker>();
services.AddSingleton<MessageProcessor>();
services.AddSingleton<ProfileUpdater>();
services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

var sp = services.BuildServiceProvider();
var logger = sp.GetRequiredService<ILogger<Program>>();

// Parse keys
ECPrivKey dvmPrivKey = ParsePrivateKey(settings.Nostr.PrivateKey);

// Build account map: listenPubKeyHex -> publishPrivKey
var accountMap = new Dictionary<string, ECPrivKey>(StringComparer.OrdinalIgnoreCase);

if (settings.Nostr.Accounts.Length > 0)
{
    // New multi-account config
    foreach (var account in settings.Nostr.Accounts)
    {
        if (string.IsNullOrEmpty(account.ListenFromNpub) || string.IsNullOrEmpty(account.PublishPrivateKey))
        {
            Console.Error.WriteLine("ERROR: Each account must have both PublishPrivateKey and ListenFromNpub");
            return 1;
        }

        var listenHex = ParsePubKeyHex(account.ListenFromNpub);
        var pubKey = ParsePrivateKey(account.PublishPrivateKey);
        accountMap[listenHex] = pubKey;
        logger.LogInformation("Account: listen from {Listen}... -> publish as {Pub}...",
            listenHex[..16], pubKey.CreateXOnlyPubKey().ToHex()[..16]);
    }
}
else
{
    // Legacy single-account fallback
    ECPrivKey publishPrivKey = string.IsNullOrEmpty(settings.Nostr.PublishPrivateKey)
        ? dvmPrivKey
        : ParsePrivateKey(settings.Nostr.PublishPrivateKey);
    string listenFromPubKeyHex = ParsePubKeyHex(settings.Nostr.ListenFromNpub);
    accountMap[listenFromPubKeyHex] = publishPrivKey;
    logger.LogInformation("Account (legacy): listen from {Listen}... -> publish as {Pub}...",
        listenFromPubKeyHex[..16], publishPrivKey.CreateXOnlyPubKey().ToHex()[..16]);
}

logger.LogInformation("DVM npub: {Npub}", dvmPrivKey.CreateXOnlyPubKey().ToNIP19());
logger.LogInformation("Loaded {Count} account(s)", accountMap.Count);
logger.LogInformation("Blossom server: {Url}", settings.Blossom.ServerUrl);
logger.LogInformation("Event kind: {Kind}", settings.Nostr.EventKind);

// Initialize services
var relayClient = sp.GetRequiredService<NostrRelayClient>();
var processor = sp.GetRequiredService<MessageProcessor>();
processor.Initialize(dvmPrivKey, accountMap);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    logger.LogInformation("Shutting down...");
};

// Connect and subscribe
await relayClient.ConnectAsync(dvmPrivKey, cts.Token);

// Update publish account profiles with DVM npub
var profileUpdater = sp.GetRequiredService<ProfileUpdater>();
await profileUpdater.UpdateProfilesAsync(dvmPrivKey, accountMap, relayClient.Client, cts.Token);

// Send startup notification DM to all listening keys
var publisher = sp.GetRequiredService<EventPublisher>();
var dvmNpub = dvmPrivKey.CreateXOnlyPubKey().ToNIP19();
var startupMessage = $"DVM started (v{ProfileUpdater.BotVersion})\n\nDVM npub: {dvmNpub}\nAccounts: {accountMap.Count}\nRelays: {string.Join(", ", settings.Nostr.Relays)}\nEvent kind: {settings.Nostr.EventKind}";

foreach (var listenPubKeyHex in accountMap.Keys)
{
    try
    {
        await publisher.SendDmReplyAsync(listenPubKeyHex, startupMessage, dvmPrivKey, relayClient.Client, cts.Token);
        logger.LogInformation("Sent startup notification to {PubKey}", listenPubKeyHex[..16]);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to send startup notification to {PubKey}", listenPubKeyHex[..16]);
    }
}

relayClient.GiftWrapReceived += async (sender, giftWrap) =>
{
    try
    {
        await processor.ProcessGiftWrapAsync(giftWrap, relayClient.Client, cts.Token);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error processing gift wrap {Id}", giftWrap.Id);
    }
};

logger.LogInformation("Nostr Shorts DVM is running. Press Ctrl+C to stop.");

// Health check: write timestamp file periodically for Docker health check
var healthTimer = new System.Threading.Timer(_ =>
{
    try
    {
        var healthy = relayClient.IsHealthy();
        if (healthy)
            File.WriteAllText("/app/data/healthcheck", DateTimeOffset.UtcNow.ToString("O"));
        else
            File.Delete("/app/data/healthcheck");
    }
    catch { /* ignore */ }
}, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

// Keep running until cancelled
try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Expected
}

healthTimer.Dispose();

await relayClient.DisposeAsync();
sp.GetRequiredService<DuplicateTracker>().Dispose();

logger.LogInformation("DVM stopped.");
return 0;

// --- Helper functions ---

static ECPrivKey ParsePrivateKey(string key)
{
    key = key.Trim();
    if (key.StartsWith("nsec"))
        return key.FromNIP19Nsec();

    return ECPrivKey.Create(Convert.FromHexString(key));
}

static string ParsePubKeyHex(string key)
{
    key = key.Trim();
    if (key.StartsWith("npub"))
    {
        var pubKey = key.FromNIP19Npub();
        return pubKey.ToHex();
    }

    return key;
}
