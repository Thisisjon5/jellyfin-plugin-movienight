# Design — shared ABR ladder over a custom tuner host

**Status:** designed and ruled by Jon 2026-08-18. **Gating spike (T0) NOT run.**
Nothing below gets built until T0 passes.

Supersedes the *delivery* half of switcher v3. The feeder / slate / feed-swap
pause / position clock are unchanged and keep their v0.3.31 behaviour.

---

## 1. Problem

Roadmap §2's soak failed on 2026-08-18 with three clients. Root cause, measured
(ITERATION-LOG arc 9): **Jellyfin's Live TV path spawns one ffmpeg per client,
always** — even when the codecs match byte-for-byte and it is doing a pure copy.
Observed per-client command line:

```
-f hls -i http://127.0.0.1:8096/MovieNight/stream/master.m3u8
-codec:v:0 copy -codec:a:0 copy
-f hls -hls_segment_filename /config/cache/transcodes/<session>%d.ts
-hls_playlist_type event -hls_list_size 0
```

Two fatal consequences:

1. **O(N) disk writes.** `-hls_list_size 0` + `playlist_type event` means
   segments are never deleted; each client accumulates the entire broadcast
   (~3.9 GB per client for a 2 h film at 4.3 Mbps) on one 7200 rpm spindle.
2. **The sliding window is destroyed.** Our rolling playlist is re-muxed into an
   unbounded event playlist, so a client can never fall off the window — which
   is why stragglers stay behind permanently and cannot be pushed to live edge.

Verified negatives, so we do not retry them:

- Serving HLS instead of raw TS does **not** avoid the hop (baseline 2 encoder
  processes → 3 with one client tuned).
- Server-driven playstate sync does **not** reach Roku
  (`SupportsMediaControl: false`; Xbox honoured the same command, Roku ignored
  it). Client-side code, unreachable from a server plugin. Confirms SPEC §21.

## 2. Goals / non-goals

**Goals**

- Server compute **O(1) in viewer count**. 5 viewers minimum, target 10.
- Viewers are remote, across the internet, over tailscale.
- Adaptive bitrate: a viewer on a bad link degrades a rung instead of freezing.
- Live-edge convergence without client commands (they provably cannot reach Roku).
- Preserve universal client support via Live TV (Roku, Xbox, web, mobile).
- Preserve the existing owner-controlled pause.

**Non-goals**

- Per-viewer rewind / DVR. A sliding window deliberately discards the past.
- Sub-second latency. ~12–20 s behind the encoder is fine and expected.
- Per-user access control beyond Jellyfin's existing Live TV permissions.
- Fixing the NAS's unrelated health problems (see §10).

## 3. Capacity envelope (measured 2026-08-18)

| quantity | measured |
|---|---|
| upstream (NAS → WAN) | 142 Mbps / 222 Mbps (two runs) |
| NAT | easy; `MappingVariesByDestIP: false`; UPnP + NAT-PMP |
| tailscale DERP | LAX 4.1 ms (fallback only; direct P2P expected) |
| CPU | 4 cores |
| RAM | 8 GB (5.3 GB was in swap during the failed soak) |
| disk | ONE 7200 rpm 8 TB WD (`ROTA=1`), /volume1 92 % full |

Delivery is over tailscale with **no CDN**, so compute is O(1) but **egress is
O(N)**. Ten viewers on the 6 Mbps rung is ~60 Mbps against a 142 Mbps floor —
comfortable. Bandwidth is not the binding constraint; encode capacity and the
box's baseline health are.

---

## 4. Architecture

```
 (A) mezzanine.mp4   1080p ~6 Mbps h264 + AAC, FIXED GOP
        |               prepared ahead of movie night
        |  -re, ONE decode
        v
 (B) feeder  ----+                      (movie feeder or slate feeder)
        |        |  pause = swap which feeder is live (v3, UNCHANGED)
        v        |
 (C) feed.ts copy loop  <---------------+
        |
        v
 (D) ladder encoder      one ffmpeg, one input, three outputs
        |                  v0 = copy | v1 = qsv 720p | v2 = qsv 480p
        v
 (E) HLS muxer -> tmpfs   master.m3u8 + 3 variant playlists, SLIDING window
        |
        v
 (F) /MovieNight/stream/hls/...   authenticated plugin route
        ^
        |  client fetches playlists + segments directly, picks its own rung
 (G) custom ITunerHost -> MediaSourceInfo{ TranscodingUrl = master.m3u8, ... }
        ^
        |
    Jellyfin Live TV -> client
```

### (A) Mezzanine preparation — new

A prep step run before movie night. Transcodes the source once into the ladder's
**top rung**, so rung 0 is later served `-c copy` and costs no encode at all.
It also removes the 38.2 Mbps decode that pinned a core during the failed soak —
the feeder reads ~6 Mbps instead.

Constraint that makes ABR possible: the mezzanine **must be encoded with a fixed,
closed GOP matching the ladder's** (`-g 96`, i.e. 4.0 s at 23.976 fps), because
rung 0 inherits its keyframe positions verbatim. Without this, rung 0's segment
boundaries will not align with rungs 1–2 and clients cannot switch cleanly.

Sketch (exact args settled in T2):

```
-i <source> -c:v h264_qsv -b:v 6000k -maxrate 6000k -bufsize 12000k
-g 96 -forced_idr 1 -vf format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:1080
-c:a aac -b:a 160k -ac 2 -sn -map 0:v:0 -map 0:a:0 <mezzanine.mp4>
```

### (B)(C) Feeder + feed.ts — UNCHANGED

Exactly as v0.3.31. The movie feeder and slate feeder, the swap-on-pause, the
lazy start, the zero-viewer kill, the position clock. The ladder encoder simply
replaces the old switcher as the single consumer of `feed.ts`.

This is deliberate: pause is the most-tested part of the system and sits
entirely upstream of everything new here.

### (D) Ladder encoder — new (replaces the switcher process)

One ffmpeg, one input, three outputs. One process rather than three so the
decode happens once.

```
-fflags +genpts -i http://127.0.0.1:<port>/MovieNight/stream/feed.ts
-filter_complex "[0:v]split=2[s1][s2];
   [s1]format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:720[v1];
   [s2]format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:480[v2]"

# rung 0 — free
-map 0:v:0 -map 0:a:0 -c:v:0 copy -c:a:0 copy
# rung 1 — 720p
-map [v1] -map 0:a:0 -c:v:1 h264_qsv -b:v:1 3000k -maxrate:v:1 3000k -bufsize:v:1 6000k
   -g 96 -forced_idr 1 -c:a:1 aac -b:a:1 128k
# rung 2 — 480p
-map [v2] -map 0:a:0 -c:v:2 h264_qsv -b:v:2 1200k -maxrate:v:2 1200k -bufsize:v:2 2400k
   -g 96 -forced_idr 1 -c:a:2 aac -b:a:2 96k

-f hls -var_stream_map "v:0,a:0 v:1,a:1 v:2,a:2"
-hls_time 4 -hls_list_size 6 -hls_delete_threshold 1
-hls_flags delete_segments+independent_segments+program_date_time
-hls_segment_type mpegts
-master_pl_name master.m3u8
-hls_segment_filename <tmpfs>/v%v/seg_%d.ts  <tmpfs>/v%v/index.m3u8
```

**Two hard-won constraints from CLAUDE.md that this must respect:**

- **Never `-force_key_frames` on the QSV branch.** It makes h264_qsv emit no
  keyframe-flagged packets at all, the HLS muxer opens no segments, and Go Live
  times out — the single most misleading failure this project has produced.
  Use `-g 96 -forced_idr 1` instead.
- **`scale_qsv` needs an explicit `hwupload` first and only accepts `-1`**
  (not `-2`) for the keep-aspect dimension.

GOP alignment across rungs is what makes clean rung-switching possible: all
three share one input and one process, `-g 96` on the encoded rungs, and rung 0
inherits the mezzanine's matching fixed GOP. `-hls_time 4` then lands segment
boundaries on IDRs. **This is an assumption to verify in T2, not a fact.**

### (E) Sliding window and tmpfs sizing

4 s segments, `-hls_list_size 6` = a **24 s window**; players typically start
3 segments from the end, so ~12 s behind the encoder.

Worst-case resident bytes (window + 1 pending, per `-hls_delete_threshold 1`):

| rung | bitrate | bytes/segment | ×7 segments |
|---|---|---|---|
| v0 | 6.0 Mbps | 3.0 MB | 21.0 MB |
| v1 | 3.0 Mbps | 1.5 MB | 10.5 MB |
| v2 | 1.2 Mbps | 0.6 MB | 4.2 MB |
| | | **total** | **~36 MB** |

**A 128 MB tmpfs is generous.** This retires the "tmpfs vs 8 GB RAM" risk
outright — and is the precise opposite of Jellyfin's `-hls_list_size 0`, which
is what filled the spindle.

### (F) Segment serving route — auth change

New route `GET /MovieNight/stream/hls/{variant}/{file}`, replacing
`IsLoopbackRequest()` with Jellyfin's own `[Authorize]`.

This revises SPEC §148 (loopback-only), ruled by Jon 2026-08-18. Narrow in
practice: `TranscodingUrl` is fetched from **Jellyfin's own HTTP server**, where
our plugin routes already live — same host, same port, same token the client is
already using. No new exposed surface, no second server.

Rules:

- `{variant}` constrained to `v0|v1|v2`; `{file}` to `index.m3u8` or
  `seg_<digits>.ts`. Anything else 404s. No path traversal reachable.
- Segments: `Cache-Control: public, max-age=60` (names are immutable).
- Playlists: `Cache-Control: no-cache`.
- 404 on an expired segment is **correct and load-bearing** — it is the signal
  that pushes a straggler back to the live edge.

### (G) Custom ITunerHost — new

Reflected contract (jellyfin.controller 10.11.6, verified):

```
ITunerHost
  Task<..> GetChannels(bool, ct)
  Task<..> GetChannelStream(string, string, IList, ct)
  Task<..> GetChannelStreamMediaSources(string channelId, ct)   <- we control this
  Task<..> DiscoverDevices(int, ct)
  string Name; string Type; bool IsSupported
```

`GetChannelStreamMediaSources` returns:

```
Protocol                            = MediaProtocol.Http
Path / TranscodingUrl               = <base>/MovieNight/stream/hls/master.m3u8
Container                           = "hls"
TranscodingSubProtocol              = MediaStreamProtocol.hls
SupportsDirectPlay                  = true
SupportsDirectStream                = true
SupportsTranscoding                 = false     <- do not offer Jellyfin the hop
UseMostCompatibleTranscodingProfile = false     <- was TRUE in the failed soak
IsInfiniteStream                    = true
RequiresOpening / RequiresClosing   = false
MediaStreams                        = declared h264 + aac, per-rung bitrates
```

`SupportsTranscoding = false` is the load-bearing field: it is how we tell
Jellyfin there is no fallback hop to take. **Whether Jellyfin honours that or
refuses playback outright is exactly what T0 answers.**

---

## 5. Data flow

**Go Live** — mezzanine selected; feeder starts lazily on first consumer; ladder
encoder starts; first segments appear in tmpfs; tuner playlist advertises the
channel; guide refresh fires (`IGuideManager.RefreshGuide`, as today).

**Tune** — Jellyfin asks our tuner host for media sources; we return the
MediaSourceInfo above; client fetches `master.m3u8`, picks a rung by its own
measured throughput, pulls segments directly. **No server-side process is
created for that client.**

**Steady state** — one feeder + one ladder encoder, regardless of viewer count.
Segment writes are constant; segment reads are served from page cache.

**Pause** — unchanged from v3: the feed swaps movie → slate. The ladder encoder
never restarts, so segments keep flowing and every client sees the slate within
its own buffer depth. **Resume** swaps back at the logged timestamp.

**Straggler** — a client that buffers badly falls behind, requests a segment
that has been deleted, 404s, reloads the playlist and rejoins at the live edge.
This replaces "permanently behind forever," which the current architecture
cannot fix at all.

**Zero viewers** — v0.3.31 behaviour retained; the position clock keeps running,
the feeder is killed and respawns at the live edge on the next consumer. Open
question: whether the ladder encoder should also idle (see §10).

## 6. Failure modes

| failure | behaviour | handling |
|---|---|---|
| Ladder encoder dies | all clients starve | supervise + restart; media sequence resets, clients rejoin at edge; log loudly |
| Feeder dies | as v3 today | existing `HandleSw2FeederEnded` |
| Client falls off window | 404 on segment | **intended** — rejoin at live edge |
| tmpfs fills | bounded ~36 MB | cannot realistically occur; alarm if it does |
| Jellyfin restart mid-broadcast | stale `liveStreamId`; clients wedge | known gotcha: clients must reload. Document, do not fix |
| Rung boundaries misaligned | ugly or failed rung switches | caught by T2 before any live test |
| Mezzanine missing | no broadcast | fail Go Live with a clear error; do **not** silently fall back to the 38 Mbps source |

## 7. What gets deleted

Nothing yet. The old HLS writer, filler/resume/splice code and segment routes
stay until the ladder is proven live — then roadmap §3's deletion happens as one
gated commit. The switcher process is superseded by the ladder encoder.

---

## 8. Testing plan

Two standing rules from prior arcs:

- **Only a human tuning real hardware counts as a client test.** Automated
  browser tabs manufacture false failures (arc 8) — that finding cost a night.
- **Measure, do not assert.** Every criterion below is a number or a process
  count, not an impression.

### T0 — THE GATE (blocks everything)

*Does Jellyfin hand `TranscodingUrl` to the client untouched?*

Minimal custom `ITunerHost` returning a MediaSourceInfo that points at a
**pre-generated static** HLS ladder. No feeder, no live pipeline, no muxer.
Tune ONE client.

- **PASS:** zero new ffmpeg processes attributable to the client, AND the
  session reports `PlayMethod: DirectPlay`.
- **FAIL:** any new ffmpeg process, or `PlayMethod: Transcode`.

On FAIL, stop. Live TV delivery cannot be made O(1), and the decision becomes
whether to give up the Live TV client UX — a Jon ruling, not an implementation
choice.

### T1 — Ladder argument builder (unit)

Extends the existing `FfmpegCommandBuilderTests` pattern. No ffmpeg execution.

**PASS:** args for all three rungs generated per accel type; `-force_key_frames`
appears **nowhere** on a QSV branch (regression guard for the v0.3.22 bug);
`scale_qsv` is always preceded by `hwupload` and never uses `-2`.

### T2 — Ladder encode + GOP alignment (offline)

Run the ladder via `POST /MovieNight/api/debug/encode-probe` against the
mezzanine for ~60 s into a scratch dir.

**PASS:** three variant playlists plus `master.m3u8`; every rung's segment count
and durations match within one segment; `ffprobe` shows an IDR at the first
frame of **every** segment in **every** rung; encoder sustains **≥ 1.0×**
realtime.

This is where the GOP-alignment assumption in §4(D) is confirmed or killed.

### T3 — Sliding window (offline)

Let T2's output run ~5 minutes.

**PASS:** each variant directory holds ≤ 7 segments steady-state; oldest
segments are deleted; `#EXT-X-MEDIA-SEQUENCE` increases monotonically; total
tmpfs usage stays under 128 MB.

### T4 — Single live client

Full pipeline, one human-tuned client.

**PASS:** plays; `PlayMethod: DirectPlay`; ffmpeg count is exactly
feeder + ladder (**no per-client process**); feed heartbeat holds ~the mezzanine
bitrate with 60 s spacing.

### T5 — Pause / resume through the ladder

**PASS:** pause reaches the screen within one buffer depth (~12–20 s); resume
returns to the logged timestamp; the ladder encoder pid is **unchanged**
throughout (no restart); no rung-switch artefact at the seam.

### T6 — Straggler forced to live edge

Deliberately stall one client (pull its network ~60 s), then restore.

**PASS:** on restore the client rejoins **at the live edge**, not 60 s behind.
Measured as session position vs. server position, converging to within one
window (24 s). This is the §1c residual, fixed.

### T7 — ABR rung switching

Constrain one client's bandwidth below the top rung.

**PASS:** the client drops a rung and keeps playing — it does **not** freeze.
Verified per client type; Roku separately, on real hardware.

### T8 — Scale (the test the failed soak needed)

Add clients 1 → 3 → 5 → 8, holding each step ~10 minutes.

**PASS at every step:** ffmpeg count stays at feeder + ladder (**constant, does
not grow with N**); feed heartbeat deltas stay within 10 % of nominal with 60 s
spacing; NAS iowait stays < 15 %; no client's position drifts from the server's
by more than one window.

This is the criterion the 2026-08-18 soak would have failed at N=3 — heartbeat
gaps of 111 s and 98 s, deltas of 2018 and 988 kbps against a 4260 nominal.

### T9 — Full soak

Feature-length film, 5+ clients, several pauses.

**PASS:** film completes; sessions survive; ffmpeg count constant throughout;
logs clean; **zero orphan processes at the end**; tmpfs bounded.

### Client compatibility matrix

Each of T4–T7 recorded per client. Roku is the hard case and is always tested on
real hardware.

| client | T4 play | T5 pause | T6 live-edge | T7 rung switch |
|---|---|---|---|---|
| Roku Ultra | | | | |
| Xbox (uwp) | | | | |
| Web (Chrome) | | | | |
| Android app | | | | |

Android is expected to be interesting: §1c's black screen was ExoPlayer choking
on a raw infinite TS. HLS is ExoPlayer's native format, so this design may fix
it as a side effect. Worth checking, not worth blocking on.

### Reusable measurement commands

Broadcast state and per-session play method:

    curl -s -H "X-Emby-Token: $KEY" "$B/MovieNight/api/debug/sw2/status"
    curl -s -H "X-Emby-Token: $KEY" "$B/Sessions"

The ffmpeg census — the number that must not grow with N:

    tailscale ssh Jon@nas 'ps -eo pid,etime,pcpu,args | grep ffmpeg'

Box health:

    tailscale ssh Jon@nas 'uptime; free -m; top -bn1 | head -5'

Feed heartbeat deltas, the real starvation detector — compare byte counts
between consecutive lines, not the printed cumulative kbps, which hides stalls:

    curl -s -H "X-Emby-Token: $KEY" --get --data-urlencode "name=log_<date>.log" \
         "$B/System/Logs/Log" | grep "feed.ts heartbeat"

---

## 9. Milestones (dependency-ordered)

| # | milestone | blocked on | done when |
|---|---|---|---|
| M0 | Gate spike | — | T0 passes |
| M1 | Mezzanine prep + fixed GOP | M0 | T2 alignment holds |
| M2 | Ladder encoder + sliding window | M1 | T2, T3 |
| M3 | Authed segment route | M0 | route serves; loopback check gone; traversal 404s |
| M4 | Real tuner host on the live pipeline | M2, M3 | T4, T5 |
| M5 | Live-edge + ABR behaviour | M4 | T6, T7 |
| M6 | Scale + soak | M5 | T8, T9 |
| M7 | Delete superseded HLS machinery | M6 | roadmap §3, **gated on Jon's ruling** |

M1 and M3 are independent of each other and can run in parallel after M0.

## 10. Open questions

1. **Should the ladder encoder idle at zero viewers?** v0.3.31 kills the feeder;
   the ladder encoder's behaviour is undecided. Cheap either way — decide after T4.
2. **Ladder encode cost on 4 cores + QSV.** Two QSV encodes plus a copy from a
   6 Mbps decode should be comfortable, but it is unmeasured until T2.
3. **Mezzanine prep wall-clock.** A 2 h film is a real encode job; it needs to be
   a scheduled, ahead-of-time step, not something done at Go Live.
4. **NAS baseline health is not addressed here** and is not this design's job.
   During the failed soak: load 14, iowait 38.8 %, 5.3 GB swapped, /volume1 92 %
   full, and **2482 zombie processes all parented by the `velocity-dashboard`
   container** (velocity-agent stack, unreaped python children). That leak needs
   its own fix — O(1) delivery will help, but it is not a substitute for a
   healthy box.
