# Movie Night — Jellyfin Live Broadcast Plugin

**Spec version:** 0.1 (draft for build)
**Target:** Jellyfin 10.10.x · .NET 8 · targetAbi 10.10.0.0
**Build tool:** Claude Code, from `jellyfin/jellyfin-plugin-template` scaffold
**Author:** Jon (spec) / Claude Code (implementation)

> **Retarget note (2026-08-13):** the install target is the NAS's real Jellyfin
> instance, which runs **10.11.6**, not 10.10.x. Build is now pinned to
> `net9.0` / `Jellyfin.Controller`+`Model` `10.11.6` / targetAbi `10.11.0.0`
> (10.11.x requires net9.0 per its NuGet dependency group — 10.10.x's net8.0
> no longer applies). Every ".NET 8" / "10.10.x" / "10.10.0.0" reference below
> is superseded by this note; the rest of the architecture is unaffected.

---

## 1. Problem statement

A server owner wants to broadcast a single movie to all connected users as a live, owner-controlled stream that appears as a normal Live TV channel. Requirements that eliminated existing options:

- **SyncPlay** — rejected. Uneven client support (Roku, some TV clients), per-viewer transcode load, sync drift, user-driven rather than owner-driven.
- **ErsatzTV / Tunarr / MediaMTX sidecar** — rejected for the distribution requirement. Works, but requires Docker/compose/CLI setup. Target user is a non-technical Jellyfin admin ("my friend") who must be able to install this **entirely from the Jellyfin dashboard GUI**: add repo URL → install → restart → use. No .env files, no CLI, no extra containers.

A Jellyfin plugin is the only distribution mechanism that meets the plug-and-play constraint while inheriting universal client support via Live TV.

## 2. Goals

1. Server admin picks any video file from their existing Jellyfin libraries, clicks **Go Live**, and a channel ("Movie Night" by default) becomes watchable by every user on every Jellyfin client that supports Live TV (Roku, Xbox, Apple TV/Swiftfin, Android TV, web, mobile).
2. One encode on the server regardless of viewer count. Viewers joining late join in progress, live-TV style. No seek, no pause-for-everyone.
3. Zero-CLI install and operation. All control through a plugin configuration page in the admin dashboard.
4. Works on modest hardware (reference: Intel N100, 8 GB RAM) with hardware acceleration when available and graceful software fallback.

## 3. Non-goals (v1)

- Scheduling / playlists / recurring channels (single manual broadcast only)
- Multiple simultaneous channels
- Chat, reactions, or any social layer
- Catch-up / DVR / pause-live-TV
- Per-user access control beyond Jellyfin's existing Live TV permissions
- Windows-native (non-Docker) Jellyfin servers are **supported but tier-2 tested** — see §11

## 4. Architecture overview

```
┌────────────────────────── Jellyfin server ──────────────────────────┐
│                                                                     │
│  Movie Night plugin (.NET 8 DLL)                                    │
│  ├─ Config page (dashboard, admin-only)                             │
│  ├─ BroadcastManager (ffmpeg process lifecycle)                     │
│  ├─ HLS output dir (plugin data path /hls, temp)                    │
│  ├─ REST controller                                                 │
│  │   ├─ /MovieNight/stream/master.m3u8   ← Jellyfin tuner fetches   │
│  │   ├─ /MovieNight/stream/{file}        ← variant playlists + .ts  │
│  │   ├─ /MovieNight/epg.xml              ← stub XMLTV               │
│  │   └─ /MovieNight/api/*                ← admin control endpoints  │
│  └─ TunerRegistrar (auto-creates M3U tuner + XMLTV listing)         │
│                                                                     │
│  Jellyfin Live TV subsystem                                         │
│  └─ sees "Movie Night" channel → serves to all clients w/ own auth  │
└─────────────────────────────────────────────────────────────────────┘
```

Key insight: the plugin never talks to clients directly. It serves HLS to **Jellyfin itself** (loopback), and Jellyfin's Live TV subsystem handles client delivery, authentication, and any per-client remux. Client compatibility is therefore Jellyfin's Live TV compatibility, which is exactly what we want.

## 5. ffmpeg pipeline

### 5.1 Binary

Use Jellyfin's own bundled ffmpeg (`jellyfin-ffmpeg`). Path is available from the server's `IServerConfigurationManager` / encoding options — do not ship or download a separate binary. This guarantees QSV/VAAPI builds match what the server already uses.

### 5.2 Encoding ladder (ABR)

Master playlist with up to three variants. Ladder is selected by a single **Quality preset** dropdown in config — the admin never sees ffmpeg flags.

| Preset | Rungs (video) | Audio | Intended for |
|---|---|---|---|
| **High (default)** | 1080p @ 5 Mbps · 720p @ 3 Mbps · 480p @ 1.2 Mbps | AAC-LC stereo 128k (shared) | HW accel available |
| **Medium** | 720p @ 3 Mbps · 480p @ 1.2 Mbps | AAC-LC stereo 128k | Weaker HW / limited upload |
| **Low (single-rung)** | 720p @ 2.5 Mbps | AAC-LC stereo 96k | Software encode fallback, tiny servers |

Encoding parameters common to all rungs:

- **Video:** H.264, High profile, level 4.0 max, `-g` = 2× fps (keyframe every 2 s, aligned to segment length), CBR-ish (`-maxrate`/`-bufsize` = 1×/2× bitrate) for stable HLS
- **Audio:** downmix to stereo AAC always. No passthrough AC3/DTS in v1 — Roku live-stream audio compatibility is the binding constraint. (v2 candidate: secondary AC3 track.)
- **Subtitles:** off by default. Config offers "Burn in subtitle track N" (single dropdown listing embedded sub tracks of the selected file). Burned-in only — no separate sub track muxing in v1.
- **Pacing:** `-re` on input (real-time read). This is what makes it a broadcast.
- **HLS:** `-f hls`, 4 s segments, `hls_list_size 10`, `hls_flags delete_segments+independent_segments`, variant playlists + master via `-var_stream_map`.

### 5.3 Hardware acceleration matrix

Detect from Jellyfin's configured encoding options (the admin already set this up for their server; reuse it, don't re-detect).

| Server HW accel setting | Encoder used | Notes |
|---|---|---|
| Intel QSV | `h264_qsv` | Reference platform (N100). 3-rung ladder OK: decode once, scale, 3× encode sessions |
| VAAPI | `h264_vaapi` | Same ladder |
| NVENC | `h264_nvenc` | Same ladder; session limits on consumer GPUs are fine (≤3) |
| AMF | `h264_amf` | Same ladder |
| None / unknown / probe failure | `libx264 -preset veryfast` | **Force Low preset** (single rung) regardless of admin selection, with a visible warning in the config UI |

Implementation note: build the ffmpeg argument string via a small command-builder class with one method per accel type, unit-testable without running ffmpeg.

### 5.4 Process lifecycle (BroadcastManager)

- **Go Live:** validate file exists and is playable (ffprobe via jellyfin-ffmpeg), clean HLS dir, spawn ffmpeg, transition state `Idle → Starting → Live` once `master.m3u8` + first segments exist (poll, 30 s timeout → `Failed` with captured stderr tail shown in UI).
- **Stop:** SIGTERM, 5 s grace, SIGKILL. Clean HLS dir. State → `Idle`.
- **Natural end of movie:** ffmpeg exits 0 → state `Ended`, HLS dir retained for `hls_list_size` window then cleaned; channel shows off-air.
- **Crash:** nonzero exit → state `Failed`, stderr tail persisted to plugin log and surfaced in UI. **No auto-restart in v1** (a silent restart loop on a bad file is worse than a clear failure).
- **Server restart while live:** broadcast does not resume; state resets to `Idle`. (Live means live.)
- **Watchdog:** if segment mtime stalls > 30 s while state is `Live`, kill and mark `Failed` (stalled encode).
- **Disk guard:** HLS dir lives under the plugin data path. With delete_segments and a 10-segment window, steady-state footprint ≈ 3 rungs × 10 segs × ~2.5 MB ≈ 75 MB. Hard cap check at 2 GB → kill + `Failed` (protects against delete_segments misbehaving).

## 6. Channel integration (TunerRegistrar)

On **Go Live** (idempotent — check before create):

1. **M3U tuner host:** create via Jellyfin's Live TV service/API with playlist URL `http://127.0.0.1:{serverPort}/MovieNight/playlist.m3u` — a plugin-served one-line M3U pointing at `master.m3u8`, with `tvg-id`, `tvg-name`, channel logo attribute.
2. **Guide data:** plugin serves `/MovieNight/epg.xml`, a stub XMLTV with one channel and a single programme block: title = movie's display name (from library metadata), start = go-live time, stop = start + runtime. Register as an XMLTV listings provider the same way, mapped to the channel.
3. **Guide refresh:** trigger a Live TV guide refresh task after registration so the channel appears without waiting for the nightly refresh.
4. **Off-air behavior:** when `Idle`, the M3U endpoint still serves the channel entry but `master.m3u8` returns 404 → clients show a normal tune-failure. EPG shows "Off air." (Alternative — removing the tuner when idle — rejected: churning tuner config confuses some clients' channel caches.)

**Research task for implementation phase:** confirm the exact 10.10 mechanism for programmatic tuner/listing creation from inside a plugin — either direct use of `ILiveTvManager`/tuner host services via DI, or a localhost REST call to `POST /LiveTv/TunerHosts` and `POST /LiveTv/ListingProviders` with an admin-scoped API key the plugin provisions. Prefer the in-process service route; fall back to REST. This is the highest-uncertainty item in the project — spike it first (Phase 1 gate).

## 7. Configuration UI (dashboard page)

Standard plugin config page (HTML + JS embedded resource, like reference plugins). Admin-only by nature of dashboard placement, but **all control API endpoints must independently require admin auth** (`[Authorize(Policy = "RequiresElevation")]`) — never trust page placement.

Layout, top to bottom:

1. **Status card:** state badge (Idle / Starting / Live / Ended / Failed), now playing, elapsed / runtime, active encoder (e.g. "h264_qsv, 3 rungs"), viewer count if cheaply available from Jellyfin sessions (nice-to-have, not required for v1).
2. **Movie picker:** dropdown/search of movies from the server's libraries via Jellyfin item query (Movies + optionally all video items toggle). Stores ItemId, resolves to path server-side. No free-text file paths — that's a CLI in disguise and a path-traversal risk.
3. **Subtitle burn-in:** dropdown populated from ffprobe of selected item (None / track list).
4. **Quality preset:** High / Medium / Low (see §5.2), with detected-HW note and forced-Low warning when software fallback is active.
5. **Channel name:** text field, default "Movie Night" (used in M3U + EPG).
6. **Go Live / Stop** buttons with confirm on Stop-while-live.
7. **Last failure panel:** collapsed by default; shows stderr tail from last `Failed` state.

### Control API

| Endpoint | Method | Auth | Purpose |
|---|---|---|---|
| `/MovieNight/api/status` | GET | Admin | State machine snapshot (polled by UI every 3 s) |
| `/MovieNight/api/golive` | POST | Admin | Body: itemId, preset, subtitleTrack, channelName |
| `/MovieNight/api/stop` | POST | Admin | Stop broadcast |
| `/MovieNight/api/probe/{itemId}` | GET | Admin | Subtitle/audio track listing for pickers |
| `/MovieNight/stream/*`, `/playlist.m3u`, `/epg.xml` | GET | **Anonymous, loopback-only** | Consumed by Jellyfin's tuner. Reject non-loopback remote addresses. |

Loopback-only enforcement on stream endpoints is the security model: media bytes never leave the server unauthenticated; clients only ever get the stream through Jellyfin's authed Live TV path.

## 8. Packaging & distribution

- **Repo:** static plugin repository — `manifest.json` + versioned zip, per the standard Jellyfin plugin repo format (guid, versions[], targetAbi `10.10.0.0`, sourceUrl, checksum, timestamp). Host on Forgejo releases (Jon's) or GitHub releases; manifest URL is the single thing the friend ever pastes.
- **build.yaml:** from template; framework `net8.0`, targetAbi `10.10.0.0`.
- **CI:** on tag → build, zip, checksum, update manifest.json. (Forgejo Actions or GitHub Actions; either is fine, pick whichever repo hosts it.)
- **Versioning:** 4-part (1.0.0.0 style) as Jellyfin expects; changelog per version in manifest.
- **Friend install path (acceptance script):** Dashboard → Plugins → Repositories → add URL → Catalog → Movie Night → Install → restart server → open Movie Night settings → pick movie → Go Live. **Every step must be GUI. If any step requires SSH/CLI, the build has failed its core requirement.**

## 9. Reference implementations to crib from

Claude Code should read these before writing code:

- `jellyfin/jellyfin-plugin-template` — scaffold, build.yaml, config page wiring
- `jellyfin/jellyfin-plugin-nextpvr` or `jellyfin-plugin-tvheadend` — how real tuner/listing integrations register with Live TV
- Jellyfin source: `MediaBrowser.Controller.LiveTv` namespace (10.10 branch) — tuner host + listings provider interfaces
- Any plugin with a hosted background service (`IHostedService` via `IPluginServiceRegistrator`) — pattern for BroadcastManager (note: main plugin class cannot itself be the IHostedService)

## 10. Bitrate / bandwidth budget (reference deployment)

- Server encode: 1 decode + 3 encodes on QSV ≈ well within N100 headroom; verify in Phase 2 gate with `intel_gpu_top` equivalent observation via CPU% + realtime speed ≥ 1.0×.
- Upload per remote viewer: whatever rung their client picks, worst case 5.3 Mbps (1080p + audio + overhead). Through Cloudflare Tunnel, 5 remote viewers ≈ 27 Mbps sustained — document in README that upload bandwidth, not CPU, is the practical viewer cap.
- LAN viewers: negligible.

## 11. Device test matrix (Phase 3 gate)

Test = install fresh, tune channel, watch ≥ 10 min, join mid-stream, survive a Stop → Go Live cycle.

| Tier | Client | Platform | Must pass |
|---|---|---|---|
| 1 | Jellyfin Web | Desktop browser | Yes |
| 1 | Jellyfin Roku (official) | Roku | Yes — most codec-picky client; drives the AAC-stereo + H.264 decisions |
| 1 | Jellyfin for Xbox | Xbox | Yes |
| 1 | Swiftfin | Apple TV | Yes |
| 2 | Jellyfin Android TV | Android TV / Fire TV | Should pass; document quirks |
| 2 | Jellyfin mobile | iOS / Android | Should pass |
| 2 | Windows-native Jellyfin server | Server-side | Should pass (path handling, process signals differ — SIGTERM → Process.Kill semantics) |

Known-risk checks to test explicitly:

- Roku: live HLS with ABR master playlist (some tuner paths flatten to first variant — if so, decide: acceptable, or serve single-variant to tuner and let Jellyfin transcode down for weak clients)
- Swiftfin: channel tune from Idle (404 m3u8) shows a sane error, not a crash
- All: behavior at natural movie end (stream ends vs. client spinner)
- Guide display: channel name/logo/programme title render on each tier-1 client

## 12. Build plan (phased, gated)

**Phase 0 — Scaffold** · Template compiles, empty config page loads on a 10.10 dev server (Docker on Jon's Windows box or a throwaway container on the NAS). Repo + CI skeleton.
Gate: plugin installs from a test manifest URL via dashboard.

**Phase 1 — Tuner spike (de-risk first)** · Prove programmatic M3U tuner + XMLTV listing registration on 10.10 with a hand-made static HLS folder (pre-generate segments with ffmpeg manually — the only CLI ever, dev-side). Channel appears and plays in web client.
Gate: "Movie Night" channel tunes in Jellyfin Web from plugin-served static HLS. **If this gate fails, revisit architecture before writing anything else.**

**Phase 2 — Broadcast engine** · BroadcastManager, command builder (unit-tested per §5.3 matrix), state machine, Go Live/Stop API, status polling. Single-rung only.
Gate: full Go Live → watch in web → natural end → Stop cycles, crash + stall paths verified, no orphan ffmpeg processes after 20 start/stop cycles.

**Phase 3 — Config UI + device matrix** · Real settings page, movie picker, subtitle burn-in, presets. Run §11 matrix.
Gate: all tier-1 clients pass.

**Phase 4 — ABR ladder** · Multi-rung via var_stream_map, HW accel matrix complete, forced-Low software fallback.
Gate: 3-rung live on N100 with realtime ≥ 1.0× for full movie length; Roku ABR behavior resolved.

**Phase 5 — Package & friend gate** · Manifest repo live, versioned release, README with screenshots.
Gate: **the friend test** — a non-technical admin installs and goes live on her own server using only the README, zero synchronous help. Ship 1.0 when this passes.

## 13. Open questions (resolve during build, tracked in-repo)

1. Exact 10.10 API surface for tuner/listing registration (Phase 1 spike answers this).
2. Roku ABR-over-tuner behavior (Phase 3/4).
3. Does Jellyfin's tuner path proxy HLS cleanly, or does it try to probe/transcode the "channel"? If it insists on re-transcoding per client, evaluate marking the stream direct-play-friendly via tuner settings.
4. Viewer count from ISessionManager — trivial or noisy? (Drop from v1 if noisy.)
5. Windows-native server process management differences (tier-2, Phase 3).

## 14. Explicitly deferred (v2 candidates)

Scheduled broadcasts · playlist/multi-movie nights · secondary surround audio track · pause-live · multiple channels · non-admin "host" role · countdown/pre-roll bumper slate (fun one: serve a looping "Starting soon" slate while `Starting`)
