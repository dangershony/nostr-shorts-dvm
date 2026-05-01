# Nostr Shorts DVM

A Data Vending Machine (DVM) for the Nostr protocol that automates re-posting short-form videos from popular platforms.

Send it a video link via encrypted Nostr DM and it will:
1. Download the video (via yt-dlp)
2. Upload it to a [Blossom](https://github.com/hzrd149/blossom) media server
3. Publish it as a Nostr event (kind 1 note or kind 34235 NIP-71 video)
4. Reply with a confirmation DM

## Supported Platforms

- YouTube Shorts (and regular YouTube videos)
- TikTok
- Instagram Reels
- Facebook Reels
- X / Twitter

## How It Works

The DVM listens on Nostr relays for **NIP-17 encrypted direct messages** (gift-wrapped, kind 1059) from an authorized pubkey. When a message containing a video URL arrives, it runs through a pipeline:

1. Decrypt the three-layer NIP-17 envelope (gift wrap → seal → rumor)
2. Verify the sender matches the configured pubkey
3. Extract the video URL from the message
4. Check for duplicates (SQLite database)
5. Download the video using yt-dlp
6. Upload to a Blossom server with BUD-02 authentication
7. Publish a Nostr event with the hosted video URL
8. Track the URL to prevent future duplicates
9. Send an encrypted DM reply confirming success

## Quick Start

### Prerequisites

- [Docker](https://docs.docker.com/get-docker/) and Docker Compose

### Setup

1. Clone the repo:
   ```bash
   git clone https://github.com/dangershony/nostr-shorts-dvm.git
   cd nostr-shorts-dvm
   ```

2. Copy the example env file and fill in your values:
   ```bash
   cp .env.example .env
   ```

3. Edit `.env` with your configuration (see [Configuration](#configuration) below).

4. Run:
   ```bash
   docker compose up -d
   ```

5. Check logs:
   ```bash
   docker compose logs -f
   ```

## Configuration

All configuration is via environment variables (or the `.env` file). Double-underscore `__` separates nested keys.

### Required

| Variable | Description |
|---|---|
| `Nostr__PrivateKey` | DVM's private key (`nsec` or hex). Used to decrypt incoming DMs and send replies. |
| `Nostr__ListenFromNpub` | The `npub` (or hex pubkey) of the user the DVM will accept DMs from. |
| `Blossom__ServerUrl` | URL of your Blossom media server (e.g. `https://blossom.example.com`). |

### Optional

| Variable | Default | Description |
|---|---|---|
| `Nostr__PublishPrivateKey` | *(same as PrivateKey)* | Separate key for publishing events (post under a different identity). |
| `Nostr__EventKind` | `1` | `1` for a standard note, `34235` for a NIP-71 video event. |
| `Nostr__Relays__0` | `wss://relay.damus.io` | Relay WebSocket URLs (add more with `__1`, `__2`, etc.). |
| `Nostr__Relays__1` | `wss://nos.lol` | |

## Running Locally (without Docker)

Requires .NET 8 SDK, yt-dlp, and ffmpeg installed on your system.

```bash
cd src/NostrShortsDvm
dotnet run
```

## Nostr NIPs Used

- **NIP-17** — Private direct messages (gift wrap envelope)
- **NIP-44** — Versioned encryption
- **NIP-59** — Gift wrap / seal
- **NIP-71** — Video events (kind 34235)
- **BUD-02** — Blossom media upload authentication (kind 24242)

## Architecture

```
src/NostrShortsDvm/
├── Program.cs              # Entry point, DI, key parsing, shutdown
├── Config/AppSettings.cs   # Strongly-typed settings
├── Models/VideoJob.cs      # Video pipeline DTO
├── Nostr/
│   ├── NostrRelayClient.cs # Relay connection + subscription
│   ├── Nip17Decryptor.cs   # NIP-17 decryption
│   └── EventPublisher.cs   # Event publishing + DM replies
└── Services/
    ├── MessageProcessor.cs # Pipeline orchestration
    ├── UrlExtractor.cs     # URL extraction (5 platforms)
    ├── VideoDownloader.cs  # yt-dlp wrapper
    ├── BlossomUploader.cs  # BUD-02 upload
    └── DuplicateTracker.cs # SQLite dedup
```

## License

MIT
