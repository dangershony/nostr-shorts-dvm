# AGENTS.md

## Project Overview

**Nostr Shorts DVM** is a Data Vending Machine (DVM) for the Nostr protocol that automates re-posting short-form videos. It listens for NIP-17 encrypted direct messages from an authorized Nostr user containing video URLs, downloads the video, uploads it to a Blossom media server, and publishes it as a Nostr event.

### Supported Platforms
- YouTube Shorts (and regular YouTube)
- TikTok
- Instagram Reels
- Facebook Reels
- X/Twitter

### Flow
1. User sends an encrypted DM (NIP-17 gift wrap) containing a video URL
2. DVM decrypts the three-layer envelope (gift wrap → seal → rumor)
3. Verifies the sender matches the authorized pubkey
4. Extracts the video URL
5. Checks for duplicates (SQLite)
6. Downloads via yt-dlp
7. Uploads to Blossom server (BUD-02 authenticated upload)
8. Publishes a Nostr event (kind 1 note or kind 34235 NIP-71 video event)
9. Sends an encrypted DM reply confirming success

## Tech Stack

- **.NET 8 / C#** — Console application using `Microsoft.Extensions.Hosting` for DI/config/logging
- **NNostr.Client** — Nostr protocol (relay connections, NIP-44 encryption, event signing)
- **NBitcoin.Secp256k1** — Key handling
- **Microsoft.Data.Sqlite** — Duplicate URL tracking
- **yt-dlp** (external CLI) — Video downloading
- **Docker** — Deployment (multi-stage build with yt-dlp + ffmpeg)

## Architecture

```
src/NostrShortsDvm/
├── Program.cs              # Entry point, DI composition, key parsing, shutdown
├── Config/
│   └── AppSettings.cs      # Strongly-typed settings (Nostr, Blossom, YtDlp, Database)
├── Models/
│   └── VideoJob.cs         # DTO tracking a video through the pipeline
├── Nostr/
│   ├── NostrRelayClient.cs # Relay connection, gift-wrap subscription (kind 1059)
│   ├── Nip17Decryptor.cs   # NIP-17 three-layer decryption
│   └── EventPublisher.cs   # Publishes kind 1/34235 events + NIP-17 DM replies
└── Services/
    ├── MessageProcessor.cs # Orchestrates the full 9-step pipeline
    ├── UrlExtractor.cs     # Regex URL extraction for 5 platforms
    ├── VideoDownloader.cs  # Wraps yt-dlp CLI process
    ├── BlossomUploader.cs  # BUD-02 authenticated upload (SHA-256 + kind 24242 auth)
    └── DuplicateTracker.cs # SQLite-backed dedup with URL normalization
```

## Nostr NIPs Implemented

- **NIP-17** — Private direct messages (gift wrap envelope)
- **NIP-44** — Versioned encryption
- **NIP-59** — Gift wrap / seal
- **NIP-71** — Video events (kind 34235)
- **BUD-02** — Blossom media upload authentication (kind 24242)

## Configuration

Configuration is loaded from `appsettings.json` + environment variables (double-underscore delimited). See `.env.example` for all options.

Required:
- `Nostr__PrivateKey` — DVM's nsec or hex private key
- `Nostr__ListenFromNpub` — npub or hex pubkey to accept DMs from
- `Blossom__ServerUrl` — Blossom server URL

Optional:
- `Nostr__PublishPrivateKey` — Separate key for publishing (defaults to PrivateKey)
- `Nostr__EventKind` — `1` (note) or `34235` (NIP-71 video), default `1`
- `Nostr__Relays__0`, `Nostr__Relays__1`, etc. — Relay WebSocket URLs

## Coding Conventions

- Top-level statements in `Program.cs` (no explicit Main method)
- Constructor injection via `Microsoft.Extensions.DependencyInjection`
- Async/await throughout; `CancellationToken` propagated to all async methods
- `ILogger<T>` for structured logging
- No frameworks beyond the standard Microsoft.Extensions stack
- External processes (yt-dlp) invoked via `System.Diagnostics.Process`

## Known Limitations / Future Work

- No retry/resilience logic for network failures (relay disconnects, Blossom upload failures)
- Video title is never extracted from yt-dlp metadata (`VideoJob.Title` unused)
- Only subscribes to events from "now" — DMs sent while offline are missed
- Entire video file loaded into memory for upload (could stream for large files)
- No health monitoring or automatic reconnection
- No tests
- URL normalization strips all query params (could cause edge-case dedup issues)

## Running

```bash
# Docker (recommended)
docker compose up -d

# Local (requires .NET 8 SDK, yt-dlp, ffmpeg)
cd src/NostrShortsDvm
dotnet run
```

## Deployment

The Dockerfile uses a multi-stage build: .NET 8 SDK for build, .NET 8 runtime + yt-dlp + ffmpeg for the final image. Persistent data is stored in `/app/data` (SQLite DB) and `/app/temp` (downloaded videos before upload).
