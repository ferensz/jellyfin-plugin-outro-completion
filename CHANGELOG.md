# Outro Completion 1.0.0

Initial release for **Jellyfin 12.2** (.NET 10).

- Mark episodes watched and clear their resume points when stopped inside native Outro segments.
- Optional per-user confirmation that the immediate next episode started on the same session/device.
- Native Intro Skipper marker support, Jellyfin episode ordering, alternate-version handling, and independent additional-user preferences.
- Authenticated personal settings page and administrator defaults.
- Duplicate/reordered event handling and protection for later replays or explicit watch-state edits.
- Custom Jellyfin repository manifest with the release ZIP's MD5 checksum.

## Install from the Jellyfin catalog

Add this repository URL under **Dashboard → Plugins → Repositories**:

```text
https://raw.githubusercontent.com/ferensz/jellyfin-plugin-outro-completion/main/manifest.json
```

Install **Outro Completion** from the Catalog and restart Jellyfin. Ensure native Outro markers are available (enable Intro Skipper's **Update Media Segments** option).

Each user can configure the extra check at `/OutroCompletion/Preferences` on their server, retaining any configured base URL.

Build and automated tests passed. Live playback validation against the user's Jellyfin installation is still recommended. See the README for behavior, limitations, and manual installation.
