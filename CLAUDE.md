# Movie Night — project guardrails

Full spec + phased build plan: `planning/SPEC.md` (canonical — don't re-derive from scratch, read it).

## Status

**Phase 0 — DONE (2026-08-13).** Gate met: plugin installs from the manifest URL
via the NAS Jellyfin dashboard, restarts clean (`Loaded plugin: Movie Night 0.1.0.0`,
no errors), config page renders. Confirmed by Jon in-browser.

Repo is public on GitHub: https://github.com/Thisisjon5/jellyfin-plugin-movienight
(Jon's ruling 2026-08-13, GitHub over Forgejo). v0.1.0.0 tagged and released;
manifest.json lives at repo root on master, served at
https://raw.githubusercontent.com/Thisisjon5/jellyfin-plugin-movienight/master/manifest.json,
registered as a Plugin Repository named "MovieNight" in the NAS Jellyfin's
`system.xml`. Install target is the NAS's real Jellyfin container ([[jellyfin]],
10.11.6ubu2404-ls24, host port 8096) — not a throwaway dev instance.

**Phase 1 — DONE (2026-08-13), v0.2.6.0.** Gate met: "Movie Night" channel tunes
and plays (with audio) in Jellyfin Web, confirmed by Jon directly. `TunerRegistrar`
(`IHostedService`) registers an `"m3u"` tuner + `"xmltv"` listings provider via
`ITunerHostManager.SaveTunerHost`/`IListingsManager.SaveListingProvider` (in-process,
no REST needed — confirmed against actual 10.11.11 source, highest-uncertainty item
in the whole project, now fully de-risked). `MovieNightController` serves
`playlist.m3u`/`stream/{fileName}` (master.m3u8 + segments, one dispatching route)/
`epg.xml`, loopback-only. Five real bugs found and fixed en route (see gotchas) —
this phase took far more iteration than expected; budget accordingly for Phase 2.

Known follow-up, not blocking: the hand-made test clip is only 15s, which raced
Jellyfin's live-TV transcode startup latency (~10-17s from tune to first segment)
closely enough to intermittently trip the client's `levelLoadTimeOut`. Worked on
retry. Phase 2's real BroadcastManager won't have this problem (continuous ffmpeg
process, not a finite pre-baked clip) — no fix needed unless Phase 1 spike assets
get reused for further manual testing, in which case regenerate at 60-90s.

**Phase 2 — DONE (2026-08-14), v0.3.3.0.** Gate met: Go Live actually spawns
ffmpeg, encodes via the NAS's real Intel QSV hardware, serves HLS, and the
guide picks it up promptly; natural end and explicit Stop both leave no
orphan ffmpeg process (confirmed via `nas_memory_status` process list after
each cycle). `BroadcastManager` (state machine + process lifecycle + stall/
disk-guard watchdog) and `FfmpegCommandBuilder` (HW accel matrix, unit-tested)
built clean, but the *first real* Go Live test against actual hardware found
three genuine bugs no amount of unit testing would've caught — see gotchas.
Not yet run: the spec's full "20 start/stop cycles" stress test — only a
handful of manual cycles done today, all clean.

## Build

```
dotnet build Jellyfin.Plugin.MovieNight.sln
```

## Known gotchas

- Spec (§header) originally called for `net8.0` / targetAbi `10.10.0.0`, but the
  real install target (NAS Jellyfin) runs **10.11.6**, and `Jellyfin.Controller`
  10.11.x requires `net9.0` per its own NuGet dependency group — retargeted
  2026-08-13 (Jon's ruling), see SPEC.md retarget note. NuGet packages are pinned
  to the exact `10.11.6` (matching the running server precisely, to minimize
  API-drift risk); `build.yaml` targetAbi stays at the conventional `10.11.0.0`
  floor (Jellyfin's own convention for declaring minimum compatible version).
- Template ships GitHub Actions that call `jellyfin/jellyfin-meta-plugins` reusable
  workflows requiring the jellyfin org's bots/secrets (`command-dispatch`,
  `command-rebase`, `sync-labels`, `publish`, `changelog`, `scan-codeql`) — deleted,
  they don't work outside the jellyfin org. Kept `build.yaml`/`test.yaml` (generic,
  no org-specific secrets) as the CI skeleton.
- Git hosting: GitHub, public repo (Jon's ruling 2026-08-13) — spec §8 left this
  open for Phase 5, resolved early because Phase 0's install-path gate needed a
  real manifest host now. `gh release create` per version; manifest.json lives at
  repo root on master, update it (new version entry, checksum, sourceUrl) on every
  tagged release.
- **`Directory.Build.props` hardcodes the assembly's real `Version`/`AssemblyVersion`/
  `FileVersion`** — MUST be bumped to match `build.yaml`'s version on every release.
  Jellyfin's plugin dashboard shows the catalog-metadata version right after
  install, but switches to the *actual loaded assembly's* version once active —
  if they don't match, it shows "Version 0.0.0.0 / Repository: Unknown" even
  though everything else is fine. Verify with
  `[System.Reflection.AssemblyName]::GetAssemblyName(dllPath).Version` before
  packaging a release, don't just trust `build.yaml`.
- **Any code that calls back into Jellyfin's own HTTP API from a hosted service
  must wait for `IHostApplicationLifetime.ApplicationStarted`.** `IHostedService.
  StartAsync` runs during host startup, before Kestrel is listening — a loopback
  HTTP call in `StartAsync` (e.g. `SaveTunerHost`, which validates the M3U by
  fetching it) gets "Connection refused" every time. Register the real work via
  `lifetime.ApplicationStarted.Register(...)` instead of doing it inline.
- **`ApplicationStarted` alone still isn't enough** — Jellyfin's own middleware
  returns 503 for a further window while "Running startup tasks" executes after
  Kestrel starts listening. Poll `IServerApplicationHost.CoreStartupHasCompleted`
  (500ms interval) before making the loopback call.
- **Never declare two `[HttpGet]` routes on the same controller where one is a
  literal path and the other a parameterized template that can match the same
  URL shape** (e.g. `stream/master.m3u8` + `stream/{fileName}`). In practice the
  parameterized route can win and shadow the literal one — merge them into a
  single action that dispatches internally instead.
- **Don't ship loose files alongside the plugin DLL if you can avoid it.** On this
  NAS, files extracted from the plugin zip appeared correctly in directory
  listings but `File.Exists` returned false for them at runtime (confirmed via an
  independent host-side read on the identical path also getting `ENOENT`) — a
  filesystem quirk with zip extraction in this environment. Embed anything the
  plugin needs to serve as an `EmbeddedResource` with an explicit `LogicalName`
  instead (same pattern as `configPage.html`), read via
  `Assembly.GetManifestResourceStream`. Sidesteps the whole bug class regardless
  of root cause.
- **After a version bump, "Install" in the dashboard often stages the new version
  without loading it on the very next restart** — the "Update Plugins" scheduled
  task sometimes only detects/stages the install *during* that restart's own
  startup sequence, requiring a second restart to actually load it. Always verify
  via `nas_container_logs` (`Loaded plugin: Movie Night X.Y.Z.W`) after restarting,
  don't assume one restart cycle was enough.
- **Trigger "Update Plugins" via `ApiClient.startScheduledTask(<taskId>)` from a
  browser JS console, not the dashboard button** — the dashboard tab can go
  visually unresponsive/backdrop-stuck under NAS memory pressure (see below),
  and the scheduled-task run button silently no-ops if the click doesn't land.
  Look up the task ID via `(await ApiClient.getScheduledTasks()).find(t =>
  t.Name === 'Update Plugins')`. Same trick works to fill the config-page test
  harness's fields when the page renders behind a stuck backdrop image: set
  `.value` via the native property setter + dispatch an `input` event, don't
  rely on `computer` tool clicks landing on the right element.
- **raw.githubusercontent.com is CDN-cached per edge node** — pushing a
  manifest.json update and immediately triggering "Update Plugins" on the NAS
  can install nothing new even though `curl` from elsewhere already sees the
  fresh content, because the NAS's request hit a different (stale) edge. Wait
  ~30-60s after a manifest push before triggering an install if it silently
  no-ops the first time.
- **QSV's `scale_qsv` filter needs an explicit `hwupload` first** and only
  accepts `-1` (not `-2`) for the "keep aspect" dimension — found via the
  actual first live Go Live test against this NAS's Intel QSV hardware, not
  in review. Bare `scale_qsv=-2:720` fails two different ways: (1) software-
  decoded frames aren't QSV-resident, "Impossible to convert between the
  formats..." — fix: `format=nv12,hwupload=extra_hw_frames=64,scale_qsv=...`
  first (mirrors what the VAAPI branch already did); (2) even after that,
  `-2` triggers "Size values less than -1 are not acceptable" — QSV only
  accepts `-1`. VAAPI/software `scale`/`scale_vaapi` are unaffected (both
  accept `-2`) but are untested against real hardware — same class of bug
  could exist there, not yet exercised live.
- **`-force_key_frames` wedges h264_qsv on this NAS — never reintroduce it on
  the QSV branch.** With `-force_key_frames expr:...`, h264_qsv (jellyfin-ffmpeg
  7.1.3, iHD driver) emits NO keyframe-flagged packets at all for 25fps sources,
  so the HLS muxer never opens a single segment file and Go Live times out at
  30s with ffmpeg encoding at full speed the whole time — the single most
  misleading failure signature this project has produced (it was misdiagnosed
  as a double-Go-Live race in v0.3.19; that race was real too, but the timeouts
  were this). 24/29.97fps sources only "worked" because a driver-side IDR
  slipped out around frame ~250 (hence every successful Go Live taking 10+
  seconds to come up). QSV gets `-g 100 -forced_idr 1` instead (v0.3.22.0),
  verified ~4s segments from the first seconds. Root-caused via the
  `POST /MovieNight/api/debug/encode-probe` endpoint (v0.3.21.0) — an arbitrary
  ffmpeg-args probe that runs into a scratch dir for N seconds and returns
  files+stderr; use it (plus `GET .../debug/encoder-dirs`) for any future
  encoder mystery before reaching for a release-per-experiment loop.
- **Jellyfin's "Refresh Guide" scheduled task only runs once every 24h by
  default** (`IntervalTicks: 864000000000` = exactly 1 day, confirmed via
  `ApiClient.getScheduledTasks()`) — a guide-based client (Roku) showed "no
  schedule information" through an entire test broadcast because nothing
  forced a refresh after Go Live. `TunerRegistrar` already got one refresh
  for free (registering a listing provider triggers it), but that's once at
  startup only. `BroadcastManager` now injects `IGuideManager` and calls
  `RefreshGuide(new Progress<double>(), ct)` fire-and-forget on every state
  change (Go Live, natural end, crash, Stop) — the method Jellyfin's own
  "Refresh Guide" task calls internally, found via the same reflection-
  against-cached-NuGet-DLLs technique used for the Phase 1 tuner research
  (`MediaBrowser.Controller.LiveTv.IGuideManager`).

## Standing rules

- Follow the phase gates in `planning/SPEC.md` §12 in order — don't skip ahead
  (e.g. don't build the config UI before the Phase 1 tuner spike proves the
  architecture). If a gate fails, stop and revisit before continuing.
- No decisions on hosting, dev-server placement, or scope beyond what's already
  ruled in the spec without asking first — this repo is brand new and nothing here
  is a standing autonomy grant yet.
