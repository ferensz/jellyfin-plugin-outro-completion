# Outro Completion for Jellyfin

A **server-side Jellyfin 12.2 plugin** that marks an episode watched when playback stops **inside a native Outro segment**, even when its progress is below Jellyfin's global maximum resume percentage. It clears the resume point so the episode leaves Continue Watching.

Addresses the behavior described in [jellyfin-web #7039](https://github.com/jellyfin/jellyfin-web/issues/7039). No Jellyfin Web patches, File Transformation, browser extensions, external API keys, or Intro Skipper database access are needed.

## Requirements

- **Jellyfin Server 12.2** (built against `Jellyfin.Controller` / `Jellyfin.Model` **12.2.0**, .NET 10).
- An enabled native segment provider with **Outro** markers for the episodes, such as Intro Skipper.
- For Intro Skipper, enable **Update Media Segments** and ensure its markers have been synchronized to Jellyfin. Enable the provider for the relevant library. If needed, run Jellyfin's **Media segment scan** task.
- A client that reports playback stops and positions to Jellyfin. Next-episode confirmation also requires playback-start reports.

Later Jellyfin versions may be compatible but have not been verified. This build is not for Jellyfin 10.10 or 10.11.

## Behavior

By default, a stop with `Outro.StartTicks <= PositionTicks <= Outro.EndTicks` completes the episode immediately. Merely reaching credits while playback continues does not. The actual stop position is authoritative; missing positions are not guessed from stale progress. Positions after an Outro's end are **not** treated as being inside credits (important for post-credits scenes).

Users may enable **Require the immediate next episode to start**. Then the plugin waits for the first subsequent playback to be the immediate next episode:

- For the **same user, Jellyfin session, and device**.
- In the **same series**, following Jellyfin's episode-list ordering, including season transitions and specials. It does not just add one to the episode number or consult the unwatched-dependent Next Up list.
- Within **120 seconds** by default; the administrator can choose 5–600 seconds.

An unrelated title, skipped episode, replay, or timeout cancels the pending completion. A series finale cannot be confirmed this way; leave the extra check off if you want finales completed on a credits stop. The engine also handles a next-start report overtaking the prior stop by up to five seconds when it observed the original episode start.

The plugin:

- Honors each participating user's preferences independently, including additional session users.
- Uses the **played media version's** markers; it never borrows timestamps from a different version. Completion propagates to alternate versions using Jellyfin's built-in behavior.
- Preserves play counts, favorites, ratings, stream selections, and last-played dates.
- Deduplicates stop reports and avoids clearing a new resume point after a later replay or explicit user-data edit.
- Does not change global resume percentages, mark movies watched, seek, or alter the playback queue.
- Does not retroactively clean existing Continue Watching entries.

**Limitations:** The server cannot identify an Up Next button click. A manual next-episode start and an automatic transition look the same. The optional confirmation check only gates this plugin's extra completion: Jellyfin can still mark an episode watched through its own percentage rules. Bad credit markers can cause bad completion decisions. Missing/unrecognized playback identity information limits correlation; pending transitions are in memory and are discarded on server restart. A client which never sends a stop report cannot trigger the correction, though Jellyfin may later synthesize an idle-stop report using its last known position.

## Build and test

Install the **.NET 10 SDK** and **PowerShell 7**:

```powershell
./build.ps1
```

This builds, runs the tests, and creates:

```text
artifacts/OutroCompletion_1.0.0.0.zip
artifacts/OutroCompletion_1.0.0.0.zip.sha256
artifacts/OutroCompletion_1.0.0.0.zip.md5
artifacts/test-results/outro-completion.trx
```

For just the tests:

```sh
dotnet test Jellyfin.Plugin.OutroCompletion.slnx --configuration Release
```

The ZIP contains only the plugin DLL and documentation/license, **not** Jellyfin's own assemblies. Tests cover completion boundaries, confirmation and reordering, duplicate reports, per-user isolation, preferences persistence, native segment queries, partial watch-state updates, authentication, and HTTP endpoints with a Jellyfin base URL. HTTP tests use a test authentication handler, not a running Jellyfin server.

## Install

### Jellyfin custom repository (recommended)

1. Open **Dashboard → Plugins → Repositories**, then add:
   - **Name:** `ferensz — Outro Completion`
   - **Repository URL:**
     ```text
     https://raw.githubusercontent.com/ferensz/jellyfin-plugin-outro-completion/main/manifest.json
     ```
2. Save, open the **Catalog** (refresh if needed), select **Outro Completion**, and install it.
3. Restart Jellyfin, open the plugin's settings, and ensure Intro Skipper's **Update Media Segments** option is enabled.

Back up Jellyfin's data before installing. The catalog filters this release to Jellyfin **12.2 or newer** using `targetAbi: 12.2.0.0`; later versions are not yet verified. After you add the repository, future published versions are available through Jellyfin's normal plugin update mechanism.

Source and releases: https://github.com/ferensz/jellyfin-plugin-outro-completion

### Manual installation

1. Back up your Jellyfin data, including watch history.
2. Stop Jellyfin.
3. Create `Outro Completion_1.0.0.0` **inside Jellyfin's actual plugins directory**, then extract the release ZIP there. The DLL must be directly inside that folder, not in another nested directory.
   - Official Docker image: commonly `/config/plugins/Outro Completion_1.0.0.0/` inside the container. Use your persistent `/config` volume.
   - Native packages and other containers: locate the configured plugins directory; paths vary by installation.
4. Start Jellyfin and verify **Dashboard → Plugins → Outro Completion** appears without an activation error.
5. Check the plugin configuration, save your defaults, and verify native Outro markers exist for a test episode.

Do not copy the build/test output directory into Jellyfin; it can contain dependencies which must not replace the server's versions. For an upgrade, stop Jellyfin and replace only the previous Outro Completion installation, retaining its configuration files. Do not leave two versions of the DLL installed.

To uninstall, stop Jellyfin, remove this plugin's installed folder, and restart. Existing watched-state changes are **not** undone. Use Jellyfin's normal Mark Unplayed action to correct an unwanted completion.

## Per-user preferences

The administrator configures the global switch, default confirmation behavior, and timeout in the plugin's dashboard page.

Ordinary users open:

```text
https://your-jellyfin-server/OutroCompletion/Preferences
```

If Jellyfin uses a base URL, retain it:

```text
https://your-server/jellyfin/OutroCompletion/Preferences
```

The dashboard provides this link for sharing. Sign in with your **Jellyfin username and password**, then choose whether to enable the plugin for yourself and whether next-episode confirmation is required. “Use the server default” continues to inherit future administrator changes.

This is a standalone plugin-hosted page, not a new native client menu. Authentication goes only to the same Jellyfin server; use HTTPS over untrusted networks. Passwords and access tokens are not stored by the page. The token is kept in memory until sign-out or page closure; explicit sign-out revokes the session. The page does not use external scripts or services. Accounts with an authentication provider that does not support Jellyfin's username/password endpoint cannot use this page; an existing authenticated Jellyfin user token can still use the API below.

### API

Authenticated users can `GET` or `PUT /OutroCompletion/Me` using normal Jellyfin authentication:

```json
{
  "enabled": true,
  "requireNextEpisode": true
}
```

Use `null` for `requireNextEpisode` to restore inheritance. There is deliberately **no user-ID selector**. Identity comes exclusively from Jellyfin's authenticated user claim; an API key without a user identity is rejected. The response also includes effective/default confirmation flags, server enablement, and the confirmation window. Invalid bodies return 400; unavailable preference storage returns 503.

Personal settings are stored in `Jellyfin.Plugin.OutroCompletion.users.json` in Jellyfin's **plugin configuration directory**, separately from the standard plugin XML. Include both files in backups. Corrupt personal settings are reported in logs and are never silently overwritten; completion is skipped rather than guessing a user's preference.

## Verify on your server

1. Choose an episode with credits starting **below** your maximum resume percentage.
2. With confirmation off, stop inside those credits (or use Up Next). Confirm the episode is watched, its resume position is zero, and it no longer appears in Continue Watching after the client refreshes.
3. Enable confirmation. Stop in credits without starting another item: the plugin must not complete it. Repeat and start the immediate next episode on the same client within the window: it should complete.
4. Repeat with a different series, skipped episode, another device, and a stop just outside credits: none should trigger plugin completion.
5. Confirm another user's preferences/watch history are unaffected.

For troubleshooting, inspect Jellyfin logs for `Outro Completion` and `Completed episode`. Check native Outro boundaries, enabled providers, the played version, stop-position reporting, personal settings, and any expired confirmation window. A stale client view may need refreshing; the plugin sends normal Jellyfin user-data notifications, not custom UI commands.

## Implementation

- `Core/CompletionEngine.cs`: bounded per-user/session state machine and immutable event snapshots.
- `Playback/`: typed event consumers, native segment/order adapters, and watch-state persistence.
- `Configuration/`: server defaults and atomic, separately persisted personal preferences.
- `Api/` and `Web/`: authenticated self-service endpoints/page and administrator configuration page.

Jellyfin 12.2 publishes `PlaybackStopEventArgs` after its normal stop-state save. The plugin consumes that awaited event rather than racing the queued legacy event, then patches only `Played` and `PlaybackPositionTicks` through `IUserDataManager`. It never writes directly to Jellyfin's database or Intro Skipper's storage.

## License

GPL-3.0-only; see [LICENSE](LICENSE). Provided without warranty. When distributing binaries, also provide access to the corresponding source and build scripts as required by the license.

## Publishing another release

1. Update the project's `Version`, `AssemblyVersion`, and (if needed) Jellyfin dependencies/`JellyfinTargetAbi`; update `CHANGELOG.md`. Commit the source before building, so the release tag can identify the source used for the build.
2. Run `./build.ps1` to test and create the ZIP and checksum files.
3. Generate a candidate manifest from the **exact ZIP you will upload**:
   ```powershell
   ./scripts/Update-Manifest.ps1 -PackagePath ./artifacts/OutroCompletion_1.0.0.0.zip -Tag v1.0.0 -Changelog 'Initial release for Jellyfin 12.2.'
   ```
   Use your new version/tag for subsequent releases. The script writes `artifacts/manifest.json`, preserves older published versions, validates the ZIP against the built DLL, and uses Jellyfin's required **MD5** checksum. SHA-256 remains available separately. It refuses to replace an advertised version with different release data.
4. Create a GitHub release at the source commit/tag, uploading that ZIP, its `.md5`/`.sha256` files, and the generated manifest. Never rebuild or replace the ZIP after generating its checksum.
5. Once the release assets are publicly downloadable, replace root `manifest.json` with the generated candidate, commit, and push to `main`. This makes the new version visible at the stable custom-repository URL.

Do not publish manifests from ordinary CI builds: rebuilding an existing version can change ZIP bytes. The advertised checksum must always match the immutable GitHub release asset, not a later local/CI rebuild.
