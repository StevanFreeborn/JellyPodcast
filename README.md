# JellyPodcast

A Jellyfin plugin that downloads podcast episodes from RSS, Atom, and Apple Podcasts feeds and tags them for use in Jellyfin Books or Music libraries.

Targets Jellyfin 12.4.x on .NET 10.0.

## Overview

Instead of proxying or wrapping streams, JellyPodcast downloads episode audio enclosures directly to disk, embeds ID3/MP4/Vorbis tags and cover art with `TagLibSharp`, and tracks downloaded GUIDs in `.podcast_state.json`. Because episodes are standard local audio files, Jellyfin clients can direct-play them without server-side transcoding.

## Features

- Supports RSS 2.0 (with iTunes extensions), Atom feeds, and `podcasts.apple.com` URLs
- Downloads `.mp3`, `.m4a`, `.aac`, `.ogg`, and `.opus` enclosures atomically via `.tmp` files
- Embeds `Album`, `AlbumArtists`, `Performers`, `Title`, `Track`, `Year`, `Comment` (show notes), `Genres`, and `FrontCover` artwork (using episode-specific artwork when present, falling back to the podcast cover)
- Saves show `cover.jpg` and per-episode sidecar images (`<episode>.jpg`/`.png`/`.webp`/`.gif`) so both Jellyfin **Books** libraries (recommended for resume/played tracking) and **Music** libraries display artwork natively without extra settings
- Skips already-downloaded episodes using `.podcast_state.json` (and re-downloads if a file is deleted from disk)
- Refreshes feeds every 2 hours via the **Refresh Podcast Library** scheduled task and automatically queues a Jellyfin library scan when episodes are added or removed
- Imports and exports podcast subscriptions as OPML 2.0 files for easy migration from Pocket Casts, Overcast, or AntennaPod
- Supports per-podcast retention overrides (`Enabled`, `Disabled`, or global), episode caps, and manual episode/orphan cleanup endpoints

## Installation

### Jellyfin Plugin Repository (Recommended)

This lets Jellyfin install and update the plugin directly from the web dashboard without copying files manually:

1. In Jellyfin, open **Dashboard -> Plugins -> Repositories** (under the **Catalog** tab or **Repositories** settings).
2. Click **Add** (`+`) and enter:
   - **Repository Name**: `JellyPodcast`
   - **Repository URL**: `https://raw.githubusercontent.com/StevanFreeborn/JellyPodcast/main/manifest.json`
3. Open **Catalog**, find **JellyPodcast**, and click **Install**.
4. Restart Jellyfin.

### Manual Release Install

1. Download the `jellyfin-plugin-podcast_*.zip` archive from [GitHub Releases](https://github.com/StevanFreeborn/JellyPodcast/releases).
2. Extract its contents (`Jellyfin.Plugin.Podcast.dll`, `TagLibSharp.dll`, `meta.json`, license files) into a plugin directory inside your Jellyfin plugins path:
   - **Docker**: `/config/plugins/JellyPodcast_2.0.1.0/`
   - **Linux**: `/var/lib/jellyfin/plugins/JellyPodcast_2.0.1.0/`
   - **Windows**: `%ProgramData%\Jellyfin\Server\plugins\JellyPodcast_2.0.1.0\`
   - **macOS**: `~/Library/Application Support/jellyfin/plugins/JellyPodcast_2.0.1.0/`
3. Restart Jellyfin.

### From Source

Requires the .NET 10.0 SDK.

```bash
git clone https://github.com/StevanFreeborn/JellyPodcast.git
cd JellyPodcast
dotnet publish src/Jellyfin.Plugin.Podcast/Jellyfin.Plugin.Podcast.csproj -c Release -o ./dist
```

Copy `dist/Jellyfin.Plugin.Podcast.dll` and `dist/TagLibSharp.dll` to your Jellyfin plugin directory and restart Jellyfin.

## Setup

1. Open **Dashboard -> Plugins -> JellyPodcast**.
2. Set **Library Path** (default: `/config/data/podcasts`). For Docker, use a path inside the container.
3. Add one or more feed URLs (RSS, Atom, or Apple Podcasts) — or click **Import OPML** to load subscriptions from Pocket Casts or another podcast app — review the list, and click **Save**.
4. Go to **Dashboard -> Scheduled Tasks** and run **Refresh Podcast Library**.
5. Go to **Dashboard -> Libraries -> Add Library**:
   - **Content type**: **Books** (recommended if you want playback position resume) or **Music**.
   - **Folder**: your configured **Library Path**.
6. Scan the library.

## Disk Layout

```text
<LibraryPath>/
└── <Sanitized Podcast Title>/
    ├── cover.jpg
    ├── .podcast_state.json
    ├── 2026-11-20 - Episode Title One.mp3
    └── 2026-11-23 - Episode Title Two.m4a
```

## Configuration

| Setting                  | Description                                                                  | Default                 |
|--------------------------|------------------------------------------------------------------------------|-------------------------|
| Library Path             | Directory where podcast folders, audio files, and `cover.jpg` are stored     | `/config/data/podcasts` |
| Download Cover Art       | Download `cover.jpg` and embed cover art into audio metadata tags            | `true`                  |
| Max Episodes Per Podcast | Maximum number of episodes retained per feed                                 | `50`                    |
| Enable Auto Cleanup      | Delete episodes older than the configured retention period                   | `false`                 |
| Days to Keep Episodes    | Retention period in days when auto cleanup is enabled (overridable per feed) | `30`                    |

### Migration & Maintenance Actions

- **Import OPML** (`POST /Podcast/ImportOpml`): Parses an `.opml` subscription export (e.g., from Pocket Casts), deduplicates against current feeds, and stages new entries for review before saving.
- **Export OPML** (`GET /Podcast/ExportOpml`): Downloads your saved podcast subscriptions as `jellyfin-podcasts.opml`.
- **Clean Up All Episodes** (`POST /Podcast/Cleanup`): Deletes downloaded audio files and `.podcast_state.json` while keeping `cover.jpg` for configured feeds, and removes directories for feeds no longer in configuration.
- **Clean Up Removed Feeds** (`POST /Podcast/CleanupOrphans`): Removes only directories and files belonging to feeds removed from configuration.

## Troubleshooting

- **Plugin fails to load**: Verify that both `Jellyfin.Plugin.Podcast.dll` and `TagLibSharp.dll` are in the plugin directory.
- **No episodes appear in Jellyfin**: Check that **Library Path** points to a path readable by the Jellyfin process, confirm the **Refresh Podcast Library** task completed without errors in the Jellyfin logs (`docker logs jellyfin | grep -i podcast`), and verify the library uses **Books** or **Music**.
- **Feed not updating**: Confirm the feed is marked **Enabled** and returns valid RSS/Atom XML without HTTP authentication.

## Development

```bash
# Build
dotnet build Jellyfin.Plugin.Podcast.slnx --configuration Release

# Run tests
dotnet test --configuration Release

# Run tests with Cobertura + HTML coverage report
dotnet run scripts/test-coverage.cs
```

See [CONTRIBUTING.md](CONTRIBUTING.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for architecture notes, contribution guidelines, and dependency licenses.

## Attribution & License

This project originated as a fork of [JHoahg/JellyPodcast](https://github.com/JHoahg/JellyPodcast) and was rewritten to download local audio files and embed metadata directly.

Licensed under the MIT License. See [LICENSE](LICENSE).
