# Plan: Video Attribution & Zap Splitting

## Goal

Credit the original video creators on reposted content and enable zap splitting so creators can receive sats — even if they don't have a Nostr account yet.

---

## Phase 1: Attribution Tags

Add metadata to published events that credits the original source.

### Kind 34235 (NIP-71 Video Events)
- `["origin", "<platform>", "<video-id>", "<original-url>"]` — tracks where the video came from
- `["p", "<creator-pubkey>", "<relay>"]` — if creator's Nostr pubkey is known
- `["r", "<original-url>"]` — link back to original

### Kind 1 (Notes)
- Include original URL in content (already done)
- Add `["r", "<original-url>"]` tag
- Add `["p", "<creator-pubkey>"]` if known

### Implementation
- Extract platform + video ID from URL (UrlExtractor already parses this)
- Add origin/r tags automatically on every published event
- Add p tag only when creator pubkey is provided

---

## Phase 2: Zap Splits (NIP-57)

NIP-57 natively supports zap splitting via `zap` tags on any event:

```json
["zap", "<your-pubkey>", "wss://relay.damus.io", "<weight>"]
["zap", "<creator-pubkey>", "wss://relay.damus.io", "<weight>"]
```

Clients (Amethyst, Damus, Primal, Snort) will split zaps proportionally by weight.

### When creator pubkey IS provided
- Add two `zap` tags with configurable split ratio (e.g., 50/50 or 30/70)
- Zaps are automatically split by compliant clients

### When creator pubkey is NOT provided
- Add only your pubkey in the `zap` tag (100% to you)
- Track the video in SQLite for potential future payout
- The `origin` tag still credits the source for attribution

### DM Format
```
<video-url>                          → no creator credit, 100% zaps to you
<video-url> <npub>                   → split zaps with creator
<video-url> <npub> <split>           → custom split (e.g., "70" = 70% to creator)
```

### Configuration
Add to `AppSettings`:
```
Nostr__DefaultCreatorZapShare=50     # default % to creator when pubkey provided
```

---

## Phase 3: Creator Mapping & Tracking

### SQLite Schema Extension
```sql
CREATE TABLE creator_earnings (
    id INTEGER PRIMARY KEY,
    original_url TEXT NOT NULL,
    platform TEXT NOT NULL,
    platform_user_id TEXT,          -- e.g., YouTube channel ID
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
- TikTok: extract username from URL
- Instagram: extract username from URL
- This enables future matching: "this YouTube channel = this npub"

---

## Phase 4: Unclaimed Zap Escrow (Future Work)

For creators who don't have a Nostr account yet.

### Option A: Custodial Tracking (Simpler)
- All zaps go to your account
- Track earnings per creator in SQLite
- When creator claims (proves channel ownership), pay out via Lightning
- Claim process: creator sends DM with proof (e.g., posts a nostr pubkey on their YouTube about page)
- You manually verify and send Lightning payment

### Option B: Nutzaps / Cashu Escrow (Trustless)
- Use NIP-61 (Nutzaps) — ecash tokens locked to a keypair
- Generate a keypair per unclaimed creator, store privkey encrypted
- When creator claims, release the private key so they can sweep tokens
- More complex, less ecosystem support currently
- Could be automated: creator proves ownership → DVM releases key

### Option C: Hybrid
- Start with Option A (custodial tracking)
- Log all zap receipts (kind 9735) directed at your events
- Build a simple dashboard or DM command to check earnings
- Migrate to Option B when Nutzaps mature

---

## Open Questions

1. **Zap split ratio** — What default split? 50/50? 70% to creator?
2. **Event kind** — Switch to kind 34235 (NIP-71) by default? It supports `origin` tag natively and is semantically correct for video content.
3. **DM command format** — Is `<url> <npub>` sufficient, or do we want named commands like `/post <url> --creator <npub> --split 70`?
4. **Claim process** — How should a creator prove they own a YouTube/TikTok channel? Manual verification by you, or some automated proof?
5. **Escrow approach** — Start with Option A (custodial) or jump to Option B (Nutzaps)?

---

## Suggested Implementation Order

1. **Phase 1** — Add `origin` and `r` tags to all published events (~1 hour)
2. **Phase 2** — Parse optional npub from DM, add `zap` tags with split (~2 hours)
3. **Phase 3** — Extract platform user IDs via yt-dlp metadata, extend SQLite schema (~2 hours)
4. **Phase 4** — Design and build claim mechanism (scope TBD based on decisions above)

---

## Relevant NIPs

| NIP | Purpose |
|-----|---------|
| NIP-57 | Lightning Zaps + `zap` tag for splits |
| NIP-71 | Video events (kind 34235) with `origin` tag |
| NIP-61 | Nutzaps (Cashu ecash on Nostr) — future escrow |
| NIP-17 | Private DMs (already implemented) |
