# Third-Party Notices and Attribution

## Upstream Origin

This repository originated as a fork of [JHoahg/JellyPodcast](https://github.com/JHoahg/JellyPodcast) (MIT License, Copyright (c) 2026 Johannes Hagemann) and has since been substantially rewritten into a native audio downloader and metadata tagger.

## Third-Party Dependencies

### TagLibSharp (v2.3.0)
- **License:** LGPL-2.1-only
- **Project:** https://github.com/mono/taglib-sharp
- **Usage:** Dynamically linked and distributed alongside the plugin as `TagLibSharp.dll` for reading and writing ID3, Vorbis, and MP4 audio tags and cover artwork.

### Jellyfin.Controller & Jellyfin.Model (v12.4.0)
- **License:** GPL-3.0-only
- **Project:** https://github.com/jellyfin/jellyfin
- **Usage:** Compile-time Jellyfin server SDK references (`ExcludeAssets="runtime"`), provided at runtime by the host Jellyfin server.
