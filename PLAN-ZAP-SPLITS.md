# Plan: Video Attribution & Zap Splitting

## Goal

Credit the original video creators on reposted content and enable zap splitting so creators can receive sats — even if they don't have a Nostr account yet.

---

## Current State

- **NIP-71 (kind 34235)** is already the default event kind for published videos
- **Original URL** is already tagged on published events via `["r", "<original-url>"]`
- **Multi-account support** implemented — multiple publish keys with per-sender routing
- **Profile auto-update** — publish accounts' profiles are updated on startup with DVM npub and bot version
- **Lightning address for zaps**: `vidu@coinos.pro`

> **NOTE**: Investigate whether there is a Lightning/LNURL service that can programmatically create lightning addresses per account (for multi-account zap routing). Check LNbits, Alby, or similar services.

---

## Decisions Made

1. **Event kind**: Kind 34235 (NIP-71 video) is the default — already implemented
2. **Zap split ratio**: Default 50/50 when creator pubkey is provided
3. **DM command format**: `<url>` or `<url> <npub>` or `<url> <npub> <split>` — simple space-separated
4. **Escrow approach**: Start with Option A (custodial tracking), migrate to Cashu/Nutzaps later
5. **Lightning address**: `vidu@coinos.pro` for receiving zaps on published events

---

## Phase 1: Attribution Tags

Add metadata to published events that credits the original source.

### Tags Added to All Events

- `["origin", "<platform>", "<video-id>", "<original-url>"]` — tracks where the video came from
- `["r", "<original-url>"]` — link back to original (already implemented)
- `["p", "<creator-pubkey>", "<relay>"]` — if creator's Nostr pubkey is provided in the DM

### Video ID Extraction

Extract video IDs from URLs for the `origin` tag:
- **YouTube**: video ID from `/shorts/<id>`, `/watch?v=<id>`, `youtu.be/<id>`
- **TikTok**: video ID from `/video/<id>`
- **Instagram**: shortcode from `/reel/<code>` or `/p/<code>`
- **Facebook**: reel ID from `/reel/<id>`
- **X/Twitter**: status ID from `/status/<id>`

### Implementation
- Extend `UrlExtractor` to also return platform + video ID
- Add `origin` tag in `EventPublisher.CreateNip71Event()` and `CreateKind1Event()`
- Add `p` tag when creator pubkey is present on the `VideoJob`

---

## Phase 2: Zap Splits (NIP-57)

NIP-57 supports zap splitting via `zap` tags on any event:

```json
["zap", "<publish-pubkey>", "wss://relay.damus.io", "<weight>"]
["zap", "<creator-pubkey>", "wss://relay.damus.io", "<weight>"]
```

Clients (Amethyst, Damus, Primal, Snort) will split zaps proportionally by weight.

### When creator pubkey IS provided
- Add two `zap` tags: one for the publish account, one for the creator
- Default split: 50/50 (configurable via `Nostr__DefaultCreatorZapShare`)
- Custom split can be specified in the DM

### When creator pubkey is NOT provided
- Add single `zap` tag with 100% to publish account
- The `origin` tag still credits the source for attribution
- Track in SQLite for potential future payout if creator claims

### DM Format
```
<video-url>                          -> no creator credit, 100% zaps to publisher
<video-url> <npub>                   -> split zaps 50/50 with creator
<video-url> <npub> <split>           -> custom split (e.g., "70" = 70% to creator)
```

### Lightning Address on Profile
- Set `lud16` field on publish account's kind 0 profile to `vidu@coinos.pro`
- This allows clients to resolve where to send zaps
- Updated automatically on startup alongside the DVM npub in the about field

### Configuration
```
Nostr__DefaultCreatorZapShare=50     # default % to creator when pubkey provided
Nostr__LightningAddress=vidu@coinos.pro  # lightning address for zap receiving
```

---

## Phase 3: Creator Mapping & Tracking

### SQLite Schema Extension
```sql
CREATE TABLE IF NOT EXISTS creator_earnings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    original_url TEXT NOT NULL,
    platform TEXT NOT NULL,
    platform_user_id TEXT,          -- e.g., YouTube channel ID, TikTok username
    creator_npub TEXT,              -- NULL if unclaimed
    event_id TEXT,
    blossom_url TEXT,
    zap_share_percent INTEGER,      -- creator's share
    total_zaps_sats INTEGER DEFAULT 0,
    claimed INTEGER DEFAULT 0,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP
);
```

### Platform User ID Extraction
- YouTube: extract channel ID/handle from video metadata (yt-dlp `--print channel_id`)
- TikTok: extract username from URL (`@username`)
- Instagram: extract username from URL
- Facebook: extract page/user from URL
- X/Twitter: extract username from URL
- This enables future matching: "this YouTube channel = this npub"

---

## Phase 4: Unclaimed Zap Escrow (Future Work)

For creators who don't have a Nostr account yet.

### Option A: Custodial Tracking (Starting Here)
- All zaps go to the publish account's lightning address
- Track earnings per creator in SQLite
- When creator claims (proves channel ownership), pay out via Lightning
- Claim process: creator sends DM with proof (e.g., posts a nostr pubkey on their YouTube about page)
- You manually verify and send Lightning payment

### Option B: Cashu/Nutzaps with P2PK (Future — Trustless)
- Use NIP-61 (Nutzaps) — ecash tokens locked to a pubkey
- For each external creator, generate a dedicated keypair
- Accept nutzaps locked to that creator's generated pubkey
- Store the private key in SQLite, associated with the platform identity
- **Locktime + refund**: Add a `refund` pubkey (DVM's key) with a locktime (e.g., 6 months) so unclaimed tokens return to the DVM automatically
- **On claim**: Swap accumulated tokens to new tokens locked to the creator's real Nostr pubkey

### Creator Claim Process (same for both options)
1. Creator sends DM to the DVM: `/claim <platform-url>`
2. DVM generates a unique verification code (e.g., `nostr-claim:abc123`)
3. Creator posts the code in their YouTube description / TikTok bio / channel about page
4. DVM checks for the code using yt-dlp metadata or scraping
5. Once verified, DVM pays out (Lightning) or releases tokens (Cashu)

---

## Implementation Order

1. **Phase 1** — Add `origin` tag with video ID extraction, `p` tag for creator attribution -- **DONE**
2. **Phase 2** — Parse optional npub + split from DM, add `zap` tags, set `lud16` on profile -- **DONE**
3. **Phase 3** — Extract platform user IDs, extend SQLite schema for creator earnings -- **DONE**
4. **Phase 4** — Design and build claim mechanism (scope TBD)

---

## Relevant NIPs

| NIP | Purpose | Status |
|-----|---------|--------|
| NIP-57 | Lightning Zaps + `zap` tag for splits | Implemented (v0.0.2) |
| NIP-71 | Video events (kind 34235) | Implemented (v0.0.1) |
| NIP-61 | Nutzaps (Cashu ecash on Nostr) — future escrow | Future |
| NIP-17 | Private DMs | Implemented (v0.0.1) |

---

## Version History

- **v0.0.1** — Initial DVM: NIP-17 DM listener, video download, Blossom upload, NIP-71 publishing, multi-account support, profile auto-update
- **v0.0.2** — Attribution tags, zap splits, creator tracking, kind 1 quote-repost for NIP-71 compatibility, detailed error messages in DM replies, startup notification DMs, lightning address on profile
- **v0.0.3** — Kind 1 quote-repost now includes Blossom video URL + description in content for universal inline playback

### What's Implemented (v0.0.2)

#### Phase 1: Attribution Tags
- [x] Video ID extraction from URLs for all 5 platforms (YouTube, TikTok, Instagram, Facebook, X/Twitter)
- [x] `origin` tag on all published events: `["origin", "<platform>", "<video-id>", "<url>"]`
- [x] `r` tag with original URL (was already in v0.0.1)
- [x] `p` tag for creator attribution when npub provided in DM
- [x] Platform user ID extraction from TikTok (`@user`), X/Twitter, Facebook URLs

#### Phase 2: Zap Splits (NIP-57)
- [x] DM format parsing: `<url>`, `<url> <npub>`, `<url> <npub> <split%>`
- [x] `zap` tags on all published events with configurable split ratio
- [x] Default 50/50 split (configurable via `Nostr__DefaultCreatorZapShare`)
- [x] `lud16` lightning address set on publish account profiles (`vidu@coinos.pro`)
- [x] Confirmation DM includes zap split info

#### Phase 3: Creator Tracking
- [x] `creator_earnings` SQLite table created on startup
- [x] Every published video tracked with platform, user ID, creator npub, zap share %
- [ ] Platform user ID extraction from yt-dlp metadata (YouTube channel ID) — URL-based only for now
- [ ] Zap receipt monitoring (kind 9735 subscription)
- [ ] Creator claim process

#### Additional Features (v0.0.2)
- [x] Kind 1 quote-repost of NIP-71 video events for client compatibility
- [x] Detailed error reasons in failure DM replies (download errors, upload errors)
- [x] Startup/restart notification DM sent to all listening keys with version info
