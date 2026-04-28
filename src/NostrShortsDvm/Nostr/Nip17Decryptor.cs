using System.Text.Json;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;

namespace NostrShortsDvm.Nostr;

/// <summary>
/// Handles NIP-17 gift wrap decryption.
/// Gift Wrap (kind 1059) -> Seal (kind 13) -> Rumor (kind 14, the actual DM).
/// </summary>
public class Nip17Decryptor
{
    private readonly ILogger<Nip17Decryptor> _logger;

    public Nip17Decryptor(ILogger<Nip17Decryptor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to decrypt a NIP-17 gift-wrapped event and returns the inner rumor (DM).
    /// Returns null if decryption fails or the event is not a valid gift wrap.
    /// </summary>
    public NostrEvent? Decrypt(NostrEvent giftWrap, ECPrivKey recipientPrivKey)
    {
        if (giftWrap.Kind != 1059)
        {
            _logger.LogWarning("Event is not a gift wrap (kind 1059), got kind {Kind}", giftWrap.Kind);
            return null;
        }

        try
        {
            // Step 1: Decrypt gift wrap content to get the seal (kind 13)
            var giftWrapPubKey = NostrExtensions.ParsePubKey(giftWrap.PublicKey);
            var sealJson = NIP44.Decrypt(recipientPrivKey, giftWrapPubKey, giftWrap.Content);

            if (string.IsNullOrEmpty(sealJson))
            {
                _logger.LogWarning("Failed to decrypt gift wrap content");
                return null;
            }

            var seal = JsonSerializer.Deserialize<NostrEvent>(sealJson);
            if (seal == null)
            {
                _logger.LogWarning("Failed to deserialize seal from gift wrap");
                return null;
            }

            _logger.LogDebug("Decrypted seal (kind {Kind}) from pubkey {PubKey}", seal.Kind, seal.PublicKey);

            // Step 2: Decrypt seal content to get the rumor (kind 14)
            var sealPubKey = NostrExtensions.ParsePubKey(seal.PublicKey);
            var rumorJson = NIP44.Decrypt(recipientPrivKey, sealPubKey, seal.Content);

            if (string.IsNullOrEmpty(rumorJson))
            {
                _logger.LogWarning("Failed to decrypt seal content");
                return null;
            }

            var rumor = JsonSerializer.Deserialize<NostrEvent>(rumorJson);
            if (rumor == null)
            {
                _logger.LogWarning("Failed to deserialize rumor from seal");
                return null;
            }

            _logger.LogDebug("Decrypted rumor (kind {Kind}): {Content}",
                rumor.Kind, rumor.Content?.Substring(0, Math.Min(100, rumor.Content?.Length ?? 0)));

            return rumor;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt NIP-17 gift wrap");
            return null;
        }
    }

    /// <summary>
    /// Gets the sender's public key hex from the decrypted rumor/seal.
    /// </summary>
    public static string? GetSenderPubKey(NostrEvent giftWrap, ECPrivKey recipientPrivKey)
    {
        try
        {
            var giftWrapPubKey = NostrExtensions.ParsePubKey(giftWrap.PublicKey);
            var sealJson = NIP44.Decrypt(recipientPrivKey, giftWrapPubKey, giftWrap.Content);
            var seal = JsonSerializer.Deserialize<NostrEvent>(sealJson ?? "");
            return seal?.PublicKey;
        }
        catch
        {
            return null;
        }
    }
}
