# Plan: Zap-Triggered Channel Import

## Concept

When someone sees a video we reposted and likes it, they can zap a certain amount to trigger downloading **all videos from that creator's channel**. This creates a revenue model — users pay sats to import entire channels onto Nostr.

---

## How It Works

1. User sees a kind 34235 video event published by the DVM
2. User zaps the event with a specific amount (e.g., 1000+ sats) or sends a DM command
3. DVM detects the zap and extracts the original platform channel from the `origin`/`r` tag
4. DVM downloads all videos from that channel via yt-dlp
5. DVM uploads each video to Blossom and publishes as kind 34235 events
6. DVM creates a NIP-51 curation set (kind 30005) grouping all videos from that channel

---

## Nostr Primitives Available

### Video Organization

| Approach | How It Works | Pros | Cons |
|----------|-------------|------|------|
| **Author = Channel** | All kind 34235 events from the DVM's pubkey form one "channel" | Simple, clients already support it | All imported channels mixed together |
| **Separate pubkey per channel** | Generate a new keypair per imported channel | Clean separation, each channel has its own profile | Complex key management, clients may not discover them |
| **NIP-51 Curation Sets (kind 30005)** | Create a playlist per imported channel | Standard, supported by Flare.pub | Videos still published under DVM's pubkey |
| **Hashtags (`t` tags)** | Tag each video with the channel name/ID | Easy filtering | No rich metadata (title, image, description) |

### Recommended: Curation Sets (kind 30005) + Origin Tags

- Publish all videos under the DVM's pubkey (or a dedicated publish key)
- Tag each video with `["origin", "<platform>", "<channel-id>", "<original-url>"]`
- Create a kind 30005 curation set per channel with title, description, and references to all video events
- Clients like Flare.pub will display these as playlists

### Example Curation Set Event

```json
{
  "kind": 30005,
  "content": "",
  "tags": [
    ["d", "youtube-channel-UCxxxx"],
    ["title", "Creator Name - YouTube"],
    ["description", "All videos imported from @CreatorName on YouTube"],
    ["image", "<channel-thumbnail-url>"],
    ["a", "34235:<dvm-pubkey>:<video-d-tag-1>"],
    ["a", "34235:<dvm-pubkey>:<video-d-tag-2>"],
    ["a", "34235:<dvm-pubkey>:<video-d-tag-3>"]
  ]
}
```

---

## Payment / Trigger Mechanism

### Option 1: Zap-Triggered (Passive)
- Monitor kind 9735 (zap receipt) events on our published video events
- If zap amount >= threshold (e.g., 1000 sats), trigger channel import
- Zap message could include a command like "import channel"
- **Pros**: Frictionless, works from any client
- **Cons**: Need to run LNURL server, parse zap amounts, possible spam

### Option 2: NIP-90 DVM Job Request (Formal)
- Define a custom job kind (e.g., kind 5901 or a new kind) for "import channel"
- User publishes a job request with the channel URL
- DVM responds with `payment-required` feedback (kind 7000) + bolt11 invoice
- User pays, DVM processes
- **Pros**: Standard DVM flow, clear payment semantics, discoverable via NIP-89
- **Cons**: Requires client support for DVM job requests

### Option 3: DM Command + Lightning Invoice
- User sends DM: `/import <channel-url>`
- DVM replies with a Lightning invoice for the import fee
- On payment confirmation, DVM starts importing
- **Pros**: Works with our existing DM infrastructure
- **Cons**: Manual, not discoverable

### Recommended: Start with Option 3 (DM), plan for Option 2 (NIP-90)

DM-based is simplest and works today. NIP-90 is the "proper" way and should be the long-term goal.

---

## Pricing Model

Possible approaches:
- **Per-channel flat fee**: e.g., 5000 sats to import a channel
- **Per-video fee**: e.g., 100 sats per video in the channel
- **Tiered**: first N videos free (from the original share), pay to unlock the rest
- **Subscription**: recurring payment to keep importing new uploads (future)

### Channel Size Discovery
Before charging, DVM should:
1. Use `yt-dlp --flat-playlist --print id` to count videos without downloading
2. Report back: "This channel has 47 videos. Import all for 4,700 sats?"
3. User confirms by paying the invoice

---

## Implementation Steps

### Phase 1: Channel Metadata Extraction
- When processing a video, extract channel info via yt-dlp (`--print channel_id,channel,channel_url`)
- Store channel metadata in SQLite alongside the video record
- Tag published events with `["origin", "<platform>", "<channel-id>", "<original-url>"]`

### Phase 2: DM-Based Channel Import Command
- Parse `/import <channel-url>` from DMs
- Use yt-dlp to enumerate all videos in the channel
- Reply with video count and Lightning invoice
- On payment, download + upload + publish each video
- Create a kind 30005 curation set for the channel

### Phase 3: Zap Monitoring
- Subscribe to kind 9735 events referencing our published video events
- Track zap amounts per event in SQLite
- When threshold reached on a video, auto-trigger channel import

### Phase 4: NIP-90 DVM Job (Future)
- Register a custom job kind for channel imports
- Publish NIP-89 handler info for discoverability
- Implement the full request → payment → result flow
- Could propose as a new NIP if there's community interest

---

## Potential New NIP: "Channel Import Request"

If this concept gains traction, we could propose a NIP defining:

- **Job kind**: e.g., `kind 5905` — "Import External Video Channel"
- **Input tags**: `["i", "<channel-url>", "url"]`, `["param", "platform", "youtube"]`
- **Payment**: Via NIP-90 `payment-required` flow
- **Result**: `["a", "30005:<pubkey>:<d-tag>"]` pointing to the created curation set
- **Event tags on result**: References to all published kind 34235 video events

This would let any Nostr client request a channel import from any DVM that supports it, creating an open marketplace for video importing services.

---

## SQLite Schema Extension

```sql
CREATE TABLE channels (
    id INTEGER PRIMARY KEY,
    platform TEXT NOT NULL,             -- youtube, tiktok, instagram
    channel_id TEXT NOT NULL,           -- platform-specific channel ID
    channel_name TEXT,
    channel_url TEXT,
    total_videos INTEGER,
    imported_videos INTEGER DEFAULT 0,
    curation_set_event_id TEXT,         -- kind 30005 event ID
    requested_by_pubkey TEXT,           -- who requested the import
    payment_invoice TEXT,               -- bolt11 invoice
    payment_status TEXT DEFAULT 'pending', -- pending, paid, expired
    payment_amount_sats INTEGER,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(platform, channel_id)
);

CREATE TABLE channel_videos (
    id INTEGER PRIMARY KEY,
    channel_id INTEGER REFERENCES channels(id),
    video_id TEXT NOT NULL,             -- platform-specific video ID
    video_url TEXT NOT NULL,
    title TEXT,
    blossom_url TEXT,
    event_id TEXT,                      -- kind 34235 event ID
    status TEXT DEFAULT 'pending',      -- pending, downloading, uploaded, published, failed
    created_at TEXT DEFAULT CURRENT_TIMESTAMP
);
```

---

## Open Questions

1. **Video organization** — Curation sets (kind 30005) per channel? Separate publish keys per channel? Both?
2. **Pricing** — Flat fee per channel or per-video pricing?
3. **Rate limiting** — Max videos per import? Concurrent downloads?
4. **Storage** — Who pays for Blossom storage long-term? Pass cost to requester?
5. **New uploads** — Should the DVM periodically check imported channels for new videos? Subscription model?
6. **NIP proposal** — Worth proposing a formal NIP for this, or keep it as a custom DVM feature?
7. **Client integration** — Talk to Nostria / Flare.pub about supporting kind 30005 video curation sets?

---

## Revenue Potential

- Each channel import generates sats
- Zap splits on imported videos generate ongoing passive income
- If creators claim their content (from zap-splits plan), they get a share too
- Creates a network effect: more imported content → more viewers → more zap-triggered imports
