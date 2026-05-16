using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using NostrShortsDvm.Config;

namespace NostrShortsDvm.Nostr;

/// <summary>
/// Updates the Nostr profile (kind 0) of each publish account on startup,
/// setting the description to include the DVM npub and bot version.
/// </summary>
public class ProfileUpdater
{
    public const string BotVersion = "0.0.4";

    private readonly AppSettings _settings;
    private readonly ILogger<ProfileUpdater> _logger;

    public ProfileUpdater(AppSettings settings, ILogger<ProfileUpdater> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Updates each publish account's kind 0 profile with the DVM npub and bot version.
    /// </summary>
    public async Task UpdateProfilesAsync(
        ECPrivKey dvmPrivKey,
        Dictionary<string, ECPrivKey> accountMap,
        INostrClient client,
        CancellationToken ct)
    {
        var dvmPubKeyHex = dvmPrivKey.CreateXOnlyPubKey().ToHex();
        var dvmNpub = dvmPrivKey.CreateXOnlyPubKey().ToNIP19();

        foreach (var (listenPubKeyHex, publishPrivKey) in accountMap)
        {
            try
            {
                await UpdateProfileAsync(dvmNpub, publishPrivKey, client, ct);
            }
            catch (Exception ex)
            {
                var pubHex = publishPrivKey.CreateXOnlyPubKey().ToHex();
                _logger.LogWarning(ex, "Failed to update profile for {PubKey}", pubHex[..16]);
            }
        }
    }

    private async Task UpdateProfileAsync(
        string dvmNpub,
        ECPrivKey publishPrivKey,
        INostrClient client,
        CancellationToken ct)
    {
        var publishPubKeyHex = publishPrivKey.CreateXOnlyPubKey().ToHex();

        // Fetch existing kind 0 for this pubkey
        var existingProfile = await FetchProfileAsync(publishPubKeyHex, client, ct);

        // Build the new about text
        var newAbout = $"Send video links via DM to {dvmNpub}\nbot-version={BotVersion}";

        // Parse existing profile content or start fresh
        JsonObject profileJson;
        if (existingProfile?.Content != null)
        {
            try
            {
                profileJson = JsonNode.Parse(existingProfile.Content)?.AsObject() ?? new JsonObject();
            }
            catch
            {
                profileJson = new JsonObject();
            }
        }
        else
        {
            profileJson = new JsonObject();
        }

        // Check if about and lud16 already match
        var currentAbout = profileJson["about"]?.GetValue<string>();
        var lightningAddress = _settings.Nostr.LightningAddress;
        var currentLud16 = profileJson["lud16"]?.GetValue<string>();

        var aboutMatches = currentAbout == newAbout;
        var lud16Matches = string.IsNullOrEmpty(lightningAddress) || currentLud16 == lightningAddress;

        if (aboutMatches && lud16Matches)
        {
            _logger.LogDebug("Profile for {PubKey} already up to date", publishPubKeyHex[..16]);
            return;
        }

        // Update the about field
        profileJson["about"] = newAbout;

        // Update lud16 (lightning address) if configured
        if (!string.IsNullOrEmpty(lightningAddress))
            profileJson["lud16"] = lightningAddress;

        // Publish updated kind 0
        var evt = new NostrEvent
        {
            Kind = 0,
            Content = profileJson.ToJsonString(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await evt.ComputeIdAndSignAsync(publishPrivKey);
        await client.PublishEvent(evt, ct);

        _logger.LogInformation("Updated profile for {PubKey}: {About}", publishPubKeyHex[..16], newAbout);
    }

    private async Task<NostrEvent?> FetchProfileAsync(
        string pubKeyHex,
        INostrClient client,
        CancellationToken ct)
    {
        var filter = new NostrSubscriptionFilter
        {
            Kinds = [0],
            Authors = [pubKeyHex],
            Limit = 1
        };

        var subId = $"profile-{Guid.NewGuid().ToString()[..8]}";
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
            await Task.Delay(2000, ct);
            await client.CloseSubscription(subId, ct);
        }
        finally
        {
            client.EventsReceived -= OnEventsReceived;
        }

        return events
            .Where(e => e != null)
            .OrderByDescending(e => e.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }
}
