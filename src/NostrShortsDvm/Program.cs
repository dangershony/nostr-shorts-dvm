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

if (string.IsNullOrEmpty(settings.Nostr.ListenFromNpub))
{
    Console.Error.WriteLine("ERROR: Nostr__ListenFromNpub is required");
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
services.AddSingleton<MessageProcessor>();
services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

var sp = services.BuildServiceProvider();
var logger = sp.GetRequiredService<ILogger<Program>>();

// Parse keys
ECPrivKey dvmPrivKey = ParsePrivateKey(settings.Nostr.PrivateKey);
ECPrivKey publishPrivKey = string.IsNullOrEmpty(settings.Nostr.PublishPrivateKey)
    ? dvmPrivKey
    : ParsePrivateKey(settings.Nostr.PublishPrivateKey);

// Resolve the "listen from" pubkey hex
string listenFromPubKeyHex = ParsePubKeyHex(settings.Nostr.ListenFromNpub);

logger.LogInformation("DVM pubkey (full): {PubKey}", dvmPrivKey.CreateXOnlyPubKey().ToHex());
logger.LogInformation("Publish pubkey: {PubKey}", publishPrivKey.CreateXOnlyPubKey().ToHex()[..16] + "...");
logger.LogInformation("Listening for DMs from: {PubKey}", listenFromPubKeyHex[..16] + "...");
logger.LogInformation("Blossom server: {Url}", settings.Blossom.ServerUrl);
logger.LogInformation("Event kind: {Kind}", settings.Nostr.EventKind);

// Initialize services
var relayClient = sp.GetRequiredService<NostrRelayClient>();
var processor = sp.GetRequiredService<MessageProcessor>();
processor.Initialize(dvmPrivKey, publishPrivKey, listenFromPubKeyHex);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    logger.LogInformation("Shutting down...");
};

// Connect and subscribe
await relayClient.ConnectAsync(dvmPrivKey, cts.Token);

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

// Keep running until cancelled
try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Expected
}

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
