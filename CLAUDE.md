# Movie Night — project guardrails

Full spec + phased build plan: `planning/SPEC.md` (canonical — don't re-derive from scratch, read it).

## Status

**Phases 0–2 DONE** (2026-08-13/14): install-from-manifest, tuner/EPG
registration + channel plays (v0.2.6.0), real QSV Go Live broadcast
(v0.3.3.0, hardened through v0.3.22's force_key_frames fix). Repo public at
https://github.com/Thisisjon5/jellyfin-plugin-movienight; manifest.json at
repo root on master is the install source; target = the NAS's real Jellyfin
([[jellyfin]], 10.11.6, port 8096).

**Previous arc (2026-08-18/19): shared ABR ladder.** The 2026-08-18 soak FAILED
— Jellyfin's Live TV path spawns one ffmpeg per client ALWAYS, even for a pure
copy, so server cost was O(N) and the sliding window was destroyed. Replaced by
a ladder served through our own `ITunerHost` so clients pull segments directly:
`planning/DESIGN-abr-ladder.md`. **M0 gate (T0) PASSED and T2 PASSED
(2026-08-19, v0.3.34.0)** — Roku/Xbox/Chrome DirectPlay with zero server
ffmpeg; ladder encodes at 1.7–2.6x realtime with exact 4.004s GOP alignment.
Universal pause (switcher v3) is unchanged and sits upstream of all this.

**Current arc (2026-09-03): Halloween V1.** Deadline: movie nights late Sept /
Oct 2026, hosted on Jon's friend's gaming rig (she is the host); the NAS is now
the dev/test box. Jon's rulings: single 720p rung OK, **rung count is a
setting**, burn-in captions, ~15s pause latency OK, mpegts, **slates match the
source's geometry (option A)**. **Step 1 is LIVE on the NAS: v0.4.5.0** - six
releases on 2026-09-03, every bug server-only (DI cycle that took Jellyfin
down; registration timeout; pageshow race; v3 zero-viewer kill; the pause
seam). Xbox + Roku DirectPlay the live ladder; pause/resume proven with an
ffmpeg HLS client (`EncoderRestarts` 0, no disconnect) but **not yet on
Roku/Xbox**. **Next session starts at `planning/HANDOFF-2026-09-03-evening.md`.**
Plan: `planning/ROADMAP-2026-09-03.md`. Full history: `planning/ITERATION-LOG.md`;
prior-art research: `planning/RESEARCH-livestreaming-prior-art.md`; rulings:
`planning/DECISIONS.md`. **Lab before ship (Jon, 2026-09-03):** reproduce and
verify through `POST /MovieNight/api/debug/encode-probe` before a release that
touches the encoder - never restart-and-hope.

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
- **`POST /System/Restart` (Jellyfin API) is enough to load a staged plugin
  update** — no container restart needed, so plugin install cycles work even
  when the NAS MCP token is expired. But after ANY server restart, an
  already-open Jellyfin Web tab reuses a stale `liveStreamId` and the
  PlaybackInfo endpoint throws NullReferenceException on every tune attempt —
  the client must hard-reload (F5) before it can tune again. Cost two
  misdiagnosed "v3 is broken" rounds on 2026-08-15.
- **A `MediaSourceInfo`'s `Container` does TWO jobs and they can disagree.** It
  feeds the client's direct-play profile matcher AND picks ffmpeg's `-f` input
  demuxer on any server-side fallback. Measured 2026-08-19: `"ts"` gets Roku
  direct play but makes the fallback run `-f mpegts` against an HLS *playlist*
  (exit 187); `null` fixes the fallback but costs Roku direct play; **`"hls"` is
  the value that works** — Roku, Xbox and Chrome all DirectPlay it, and it gives
  a correct `-f hls` to whoever falls back. The profile match happens BEFORE
  `SupportsTranscoding` is consulted, so that flag is not what decides direct
  play. Related and separate: `Path`/`TranscodingUrl` must be an **absolute**
  URL the client can reach — a relative one is handed to ffmpeg as a file path
  (`-f hls -i "/MovieNight/..."` → exit 254 in 30ms). Full client analysis:
  `planning/RESEARCH-client-profiles.md`.
- **The "Update Plugins" scheduled task is currently broken server-wide on this
  NAS** — `IOException` on `Jellyfin Tweaks_4.0.0.0/thumb.png` ("being used by
  another process") thrown from `PopulateManifest` inside `GetAvailablePackages`
  aborts the entire package scan, so NO plugin can update through it. Install
  directly instead: `POST /Packages/Installed/{name}?version=X.Y.Z.W` (204),
  then `POST /System/Restart`. Verify the load in the logs as always.
- **Jellyfin's log files are readable over the API** (`GET /System/Logs`,
  `GET /System/Logs/Log?name=...`) — full server + per-ffmpeg logs without
  NAS MCP or SSH. Quick Connect can be authorized server-side
  (`POST /QuickConnect/Authorize?code=X&userId=Y` with an API key) to log a
  browser in without touching a password.
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

- **Unit tests need `Jellyfin.Model` with runtime assets in the TEST project.** The
  plugin excludes Jellyfin's runtime assets (the server supplies them), so a test
  that constructs a `MediaStream` throws `FileNotFoundException: MediaBrowser.Model`
  unless the test csproj references the package itself (added 2026-09-03). With a
  non-9 SDK, run `DOTNET_ROLL_FORWARD=Major dotnet test`.

- **Nothing reachable from an `ITunerHost` constructor may inject `IGuideManager`
  or `ILiveTvManager`.** Jellyfin builds every ITunerHost WHILE constructing
  ILiveTvManager, so the graph closes into a DI cycle and the whole SERVER fails
  to start ("A circular dependency was detected") - v0.4.0.0 did exactly this.
  Resolve Live TV services lazily at call time (BroadcastManager does, via
  IServiceProvider). `DependencyGraphTests` walks the constructor graph and
  fails on the cycle; keep it.
- **A plugin install REPLACES the previous version's folder** - there is no
  older DLL on disk to fall back to. If a version stops Jellyfin starting, the
  recovery is `docker exec jellyfin mv "/config/data/plugins/Movie Night_X"
  "/config/MovieNight_X.broken" && docker restart jellyfin` (Jon runs it; the
  harness blocks mutating SSH). The startup error page shows the real log from
  the LAN: `http://192.168.68.118:8096/startup/logger`.
- **The NAS takes ~4 min from plugin load to `CoreStartupHasCompleted`.**
  TunerRegistrar's readiness wait is 10 min for that reason; a 60 s wait
  silently skipped registration and left Live TV unwired on an otherwise
  healthy server (0.4.1.0). Also: `POST /Packages/Installed` can time out
  (`000`) and still have succeeded - check the plugins dir, not the response.
- **The pause seam is a geometry change, and QSV cannot survive one.** The
  ladder encoder reads one continuous feed; pause swaps it movie->slate. If
  width, height, frame rate or pixel format differ, ffmpeg rebuilds its filter
  graph and `hwupload`/`scale_qsv` cannot be rebuilt mid-stream ("Impossible to
  convert ... Parsed_scale_qsv_2", exit 218). `SourceGeometry` is probed at Go
  Live and `BuildSw2SlateArgs` synthesises the slate to match - never hardcode
  the slate's size or rate again. SAR rounding does not trigger it.
- **Under a ladder session the encoder is the ONLY `feed.ts` consumer.** The v3
  zero-viewer kill (last consumer gone -> kill feeders) measured viewers when each
  tune consumed feed.ts; now it would kill the feed under the encoder on any
  swap. `BroadcastManager.LadderSessionActive` disarms it; `ClearSwitcherV2`
  clears it so no teardown path can forget.
- **ffprobe's CSV output is in ITS canonical field order, not the
  `-show_entries` order** (width, height, sample_aspect_ratio, pix_fmt,
  r_frame_rate). A parser that assumed the requested order passed unit tests
  and would have read the SAR as the frame rate. Pin fixtures to real output.
- **Config-page init must not depend solely on `pageshow`** - a hard reload at
  the page URL can fire it before the inline script attaches the listener,
  leaving every field on its placeholder. Call init directly too, idempotently.
- **`encode-probe` is the lab.** `{out}` = its scratch dir, WIPED every run; put
  multi-step inputs in the container's `/tmp`. `concat:/tmp/a.ts|/tmp/b.ts`
  mirrors feed.ts exactly and reproduced the pause-seam failure offline in
  minutes. An ffmpeg on the laptop reading the authed ladder URL is a valid HLS
  client for encoder/seam proofs - but not a proxy for Roku/Xbox behaviour.

## Standing rules

- Follow the phase gates in `planning/SPEC.md` §12 in order — don't skip ahead
  (e.g. don't build the config UI before the Phase 1 tuner spike proves the
  architecture). If a gate fails, stop and revisit before continuing.
- No decisions on hosting, dev-server placement, or scope beyond what's already
  ruled in the spec without asking first — this repo is brand new and nothing here
  is a standing autonomy grant yet.
