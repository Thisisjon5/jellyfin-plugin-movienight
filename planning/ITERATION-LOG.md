# Iteration Log — 2026-08-14/15 (the universal-pause days)

Every version shipped today, what it changed, exactly what was tested, and what
happened. Written for the "why can't we solve this" retrospective. Companion
doc: `RESEARCH-livestreaming-prior-art.md` (how other projects do this).

**The goal being chased all day:** a host-controlled pause/resume of the Movie
Night live channel that every connected client (Jellyfin Web, Roku) survives —
nobody frozen, nobody kicked back to the channel list, resume lands a little
before the pause point.

**The recurring villain:** every Live TV client is fed by Jellyfin's own remux
ffmpeg (`-codec copy -copyts` reading our tuner URL). Anything that stalls,
gaps, or timestamp-jumps our stream gets interpreted by that remux (or the
player behind it) as "stream ended" or an unannounced discontinuity. Nearly
every failure below is this one constraint wearing a different mask.

---

## Arc 1 — Morning: Phase 2 hardening (v0.3.1–0.3.3)

| Ver | Change | Test | Result |
|-----|--------|------|--------|
| 0.3.1 | QSV filter chain: `format=nv12,hwupload` before `scale_qsv` | Real Go Live on NAS QSV | Fixed exit-218 "Impossible to convert between formats" |
| 0.3.2 | `scale_qsv=-1:720` (QSV rejects `-2`) | Real Go Live | Fixed; broadcast played |
| 0.3.3 | Guide refresh fired on every broadcast state change | Roku guide check | Fixed "no schedule information" (built-in refresh runs only every 24h) |

Arc verdict: **basic broadcast works** — Go Live, play with audio on Web+Roku,
natural end and Stop leave no orphans.

## Arc 2 — Midday: universal pause over the HLS design (v0.3.4–0.3.15)

Six spikes in sequence, each killed by a live test:

- **0.3.4 — Spikes 1+2: client playstate commands.** Tested: does
  `SessionInfo.PlayState` report for Live TV sessions; does
  `SendPlaystateCommand` pause a real client. **Result: command not honored**
  by real clients on tuner sessions. Client-side pause = dead end.
- **0.3.5 — Spike 3: freeze by swapping the encoder** for a black/silent
  source writing the same HLS filenames. **Result: broadcast stopped writing
  segments and the stall watchdog killed it** mid-test; stderr wasn't even
  captured on that path. Kill-and-respawn cold start too slow.
- **0.3.6 — Spike 4: SIGSTOP/SIGCONT the encoder.** Mechanically froze/resumed
  cleanly on Web and Roku, **but clients desynced badly** — traced to
  Jellyfin's remux hop buffering differently per client. Dead end.
- **0.3.7 — Spike 5: filler-channel splice.** Background filler encoder;
  pause splices filler segments into a hand-written served playlist with
  `EXT-X-DISCONTINUITY`; resume respawns the movie near the pause point.
  **Result: failed live** — filler spawned on demand left a few seconds' gap
  with no new segments; Jellyfin's remux treated the stalled playlist as
  stream-end and exited cleanly; both Web and Roku kicked to channel list.
- **0.3.8 — pre-warm the filler** (runs continuously from Go Live).
  **Pause now held cleanly on Web + Roku.** Resume broke next.
- **0.3.9 → 0.3.13 — five iterations on resume** (splice timer not
  restarted; a dual-trigger race writing the same segment file — confirmed via
  repeated IOException on segment_055.ts; an A/B rollback 0.3.10→0.3.9→0.3.10
  isolating a pause regression; keeping the filler live through the new
  encoder's warm-up; grace windows on teardown). Each fixed one failure and
  live-testing exposed the next. Copy-based splicing kept generating
  file-locking and teardown races.
- **0.3.14 — Spike 6: pointer model, no copying.** Each encoder writes its own
  prefix in its own directory; the served playlist just points at filenames;
  the movie encoder is left running across pause. Removed the whole
  IOException bug class.
- **0.3.15 — served playlist owned from Go Live** (fixed a dual-writer
  collision between ffmpeg's own live playlist and our hand-written one —
  clients flip-flopped between paused-filler and live-movie).

Arc verdict: **pause worked; resume was never observed working end-to-end**
across the splice boundary — and (established later, at 18:37) even the
"working" pause only worked for clients that *joined during* the pause. A
client watching **across** the splice freezes, because our
`EXT-X-DISCONTINUITY` marker does not survive Jellyfin's remux: the remux
copies the raw timestamp cliff downstream with no signal, and the player
stalls. This is architectural, not a bug in the splicing.

## Arc 3 — Afternoon: the 25fps mystery (v0.3.16–0.3.22)

What looked like a return of the race turned out to be a day-old latent bug:

- **0.3.16 / 0.3.17** — the two architecture spikes were built (raw-TS channel,
  zmq switcher channel) but *not yet exercised*.
- **0.3.18** — self-service config page (Go Live/Stop/Pause/Resume buttons,
  spike toggles). Minutes later a **double-Go-Live race** hit live (button gave
  no in-flight feedback → double click → two ffmpegs stomping one directory).
- **0.3.19** — atomic state claim + button debounce. Race genuinely fixed
  (the "ignoring" guard observed firing) — **but the 30s Go Live timeouts kept
  happening**, which meant the race had been misdiagnosed as their cause.
- **Investigation (0.3.20 instrumentation):** stderr head capture + an
  `encoder-dirs` debug endpoint. Live polling during a failing Go Live proved
  the output directory stays **completely empty for the whole 30s** while
  ffmpeg encodes at full speed — write-side stall, not detection failure.
  Controlled A/B via the API: two different 25fps files failed 100%
  (7 attempts); 24fps and 29.97fps files went live in ~10s every time. Same
  exact command with software x264 on a laptop: all files fine.
- **0.3.21 — encode-probe endpoint** (run any ffmpeg arg list on the NAS for
  N seconds, report files+stderr; ~15s per experiment instead of a
  release-restart cycle). Bisection matrix on the failing file:
  baseline QSV → 0 files; QSV minus audio → 0 files (kills interleave
  theories); QSV → plain mpegts → 6 MB (encoder fine); software x264 → HLS →
  clean segments (muxer fine); QSV **minus `-force_key_frames`** → segments
  immediately ← the answer; QSV with fkf plus `-g 50` → still 0.
- **0.3.22 — THE FIX:** `-force_key_frames expr` wedges h264_qsv on this
  iHD driver — with it, 25fps sources emit **no keyframe-flagged packets at
  all**, so the HLS muxer never opens a segment file. 24/29.97fps sources only
  limped through via a driver IDR near frame ~250 (why every "successful"
  Go Live took 10+ s). QSV now uses `-g 100 -forced_idr 1`. **Verified: the
  7-time-failing file went Live in 5s**; a real movie (AC3 5.1, 23.98fps,
  feature length) in 7s.

Also fixed en route: tuner registered with `TunerCount=1`, so one frozen
client session blocked every other tune with "M3U simultaneous stream limit
reached" → now 0 (unlimited) (v0.3.23).

## Arc 4 — Evening: the single-process switcher (v0.3.23–0.3.25)

- **Switcher spike as shipped (zmq) could never start:** jellyfin-ffmpeg is
  built **without libzmq** ("No such filter: 'zmq'", proven via encode-probe).
- **0.3.23 —** control channel replaced with ffmpeg's interactive **stdin**
  protocol (`c` + `streamselect -1 map N`), verified locally (frame-inspected
  color change, reply ret:0). On the NAS: channel tuned and played (proving
  **raw MPEG-TS through the tuner works** — spike #8 answered), but switches
  didn't take effect and there was zero visibility into why.
- **0.3.24 —** commands broadcast to every live switcher process + per-process
  stderr captured into the API response. **Result: blue → maroon → green
  switched live** through Jellyfin's remux into a real client, `ret:0` replies
  visible, zero seams. **Single-process switching validated end-to-end.**
- **0.3.25 — switcher v2, real content.** Movie enters via a restartable QSV
  *feeder* into a persistent local feed URL (feed.ts holds the connection open
  across feeder swaps so the switcher input stalls but never EOFs);
  `streamselect`+`astreamselect` switch movie ↔ pause card (quiet tone);
  pause logs the wall-clock movie position and kills the feeder; resume
  respawns it at pausedAt−10s. **Tested live:** movie played through the
  two-stage pipeline ✓; pause cut to the card with the same client session
  surviving ✓ (something the HLS design never achieved); pause position
  58.2s → resume feeder at 48.2s ✓; ffmpeg's input-discontinuity compensation
  **healed the feeder swap automatically** ✓. **But:** during the pause the
  switcher's *output* froze — multi-input filters (`streamselect` rides
  framesync) wait for frames on **all** inputs, so the starved feed input
  stalled the whole graph; the card never aired, the remux timed out, client
  kicked. Separately the x264 re-encode ran at 0.55x realtime on this
  swap-loaded box (30→17 fps decline) — the double encode is too heavy as
  configured.

## Where that leaves the architecture

Proven building blocks:
1. Raw continuous MPEG-TS through Jellyfin's M3U tuner: **works**.
2. One persistent encoder surviving an input source being killed and
   respawned at a different position (timestamp cliff auto-healed): **works**.
3. Live switching inside one ffmpeg via stdin commands: **works** — but only
   while all inputs keep delivering frames.
4. Pause/resume position bookkeeping (wall-clock, rewind-on-resume): trivial,
   **works**.

The two open problems, precisely:
1. A multi-input filter graph stalls when the "paused" input stops delivering
   → candidate fix: switch at the **feed layer** instead (persistent encoder
   has ONE input; the plugin swaps which upstream — movie feeder or looping
   card feeder — flows through it). Uses only proven blocks 1+2.
2. Double-encode cost (feeder + persistent encoder both encoding) on an
   8GB/4-core NAS already deep in swap → needs a perf pass (QSV for the
   persistent encoder, cheaper mezzanine, or single-encode designs).

See `RESEARCH-livestreaming-prior-art.md` for how existing projects handle
(and mostly avoid) these exact problems.

## Arc 5 — 2026-08-15 morning: switcher v3, feed-layer switching (v0.3.26–0.3.28)

Jon's go on the research-validated design. Rulings recorded first
(DECISIONS.md): resume returns to the logged timestamp minus a rewind,
teardown-and-rewarm acceptable; pause audio quiet/gentle; card-vs-loop is an
asset decision.

- **Pre-build validation:** encode-probe confirmed two concurrent QSV encode
  sessions sustain full realtime (a.ts @4Mbps + b.ts @2.5Mbps, both exactly
  realtime-sized over the window).
- **0.3.26 — v3 build.** Persistent per-channel process: ONE input (the local
  feed URL), QSV re-encode. `streamselect`/`astreamselect`/stdin commands
  deleted from the v2 path. Pause = plugin starts a slate feeder (looping
  card + quiet tone, x264, yuv420p) and kills the movie feeder; resume =
  reverse at pausedAt−10s. `feed.ts` holds the switcher's input connection
  open across swaps (stall, never EOF).
  **Test 1:** first tune spun forever. Two independent causes found:
  (a) the eagerly-started feeder had run ~4.5 min unconsumed → its backlog
  burst through the pipeline at 1.7x → every buffer minutes deep, first-tune
  probe chewed 21s of surge, Jellyfin's stream session wedged;
  (b) after a `/System/Restart`, Jellyfin's web client reused a stale
  liveStreamId and its PlaybackInfo endpoint NREs on it (client page reload
  clears it — Jellyfin bug, not ours).
  **Test 2 (fresh client):** tuned and PLAYED — the movie through
  feeder → feed → copy-consuming switcher → raw TS → remux → browser.
- **0.3.27 — lazy feeder start.** Feeder now starts from the feed.ts copy
  loop when the feed first gains a consumer — backlog structurally impossible
  (also gives crashed feeders a restart path).
  **Test (clean acceptance):** tune landed at the live edge, zero backlog,
  movie playing ✓. **Pause still killed the client (~20s):** the
  stop-log's stderr capture showed the persistent encoder frozen at exactly
  the pause point — it never received one slate byte; the feed swap delivered
  nothing. Second finding: the QSV re-encode had been running 0.68–0.8x
  realtime under the full pipeline load (probe measured raw GPU capacity,
  not the loaded system), drifting behind before the pause.
- **0.3.28 — copy remuxer + instrumented chunked feed copy.** Both findings
  addressed by simplification: the persistent process's only job is
  continuous timestamps across swaps, and ffmpeg's demuxer-level
  discontinuity compensation applies to `-c copy` exactly as to the
  re-encode where it was observed working — so the persistent process is now
  a near-zero-CPU passthrough (`-fflags +genpts -i feed -c copy -f mpegts`),
  and the perf question evaporates. The feed copy loop is now chunked (64KB)
  with per-chunk source-swap detection and full logging (consumer
  connect/disconnect, per-source pid + byte counts) so any future swap
  failure names its exact spot.
  **Status: installed on the NAS, NOT yet tested — session ended here.**

### Where the next session starts

Run the acceptance test on v0.3.28 exactly as scripted: `sw2/golive` (cats
video 4986b34785abe53a84d2e54ead8fa3a5) → tune switcher channel with a FRESH
web client → movie plays → `sw2/pause` → slate + quiet tone within pipeline
latency, session survives → wait 20s → `sw2/resume` → movie back at
pausedAt−10s, same session. The feed.ts log lines tell the swap story either
way. Open risks: does `-c copy` actually smooth the swap discontinuity
downstream (if not: fallback is re-encode with QSV *decode+encode* or lighter
settings); slate/movie stream-parameter matching under copy (both are 720p
h264 + 44.1k stereo AAC by construction). See planning/HANDOFF-2026-08-15.md.

## Arc 6 — 2026-08-16 evening: v0.3.28 acceptance test — PASS

The gate everything waited on. Cats video (16.3 min), Jon watching Jellyfin
Web + Roku simultaneously, plugin driven via the sw2 admin API, feed.ts
instrumentation watched live through `/System/Logs`.

- **Full script passed, two pause/resume cycles.** Both clients survived
  every transition in the same session — no retune, no kick. Universal
  pause/resume through Jellyfin Live TV is real.
- **Cycle 1:** pause at 141.7s — server swap gap 0.5s (movie feeder ended
  after 74.3MB, slate copying 0.5s later), slate + tone clean on both
  clients. Resume at 131.7s (exactly −10s): clean on both, rewind confirmed.
- **Cycle 2:** pause at 254.3s — position tracking airtight (matched
  wall-clock elapsed since resume within 0.6s). Swap gap 1.4s this time;
  clients visibly rebuffered, played out their remaining buffered movie
  seconds, then showed slate. Rough but delivered. Resume at 244.3s: clean,
  no buffering.
- **Stop:** FeederAlive/SlateAlive false, SwitcherProcessCount 0 — no
  orphans.
- **Open findings:** (a) slate cold-start latency varies (0.5s vs 1.4s) and
  the slower swap reads rough on clients — candidate fix: keep the slate
  feeder warm instead of spawning per pause; (b) Roku showed periodic
  stutter during *normal* playback both before any pause and unrelated to
  swaps (Jon suspects packaging/buffer sizing) — investigate via this run's
  FFmpeg.Remux/DirectStream logs before touching anything.

### Where the next session starts

Roadmap ruled by Jon 2026-08-16 (planning/ROADMAP-2026-08-16.md): (1) rewire
config page to v3 → (2) movie-length soak via that page → (3) promote v3 +
delete HLS machinery (Jon's explicit ruling gates the deletion) → (4) proper
UI, mocks first. Step 1 begun same session.

## Arc 7 — 2026-08-16 evening: v0.3.29/0.3.30 + episode mini-soak — PASS

Same session as arc 6, continued. Shipped two releases, then a ~45-min
dress rehearsal (W13 S3E7, 1080p/23.976/DTS — same format family as the
ruled Tuesday asset) with Jon driving the config page and a Roku watching.

- **0.3.29 — config page → v3** (roadmap step 1). Buttons + status drive
  sw2; channel-name input removed.
- **0.3.30 — natural-end auto-pause + feed heartbeat.** Both from tracing
  failure points pre-soak: movie EOF previously left the feed silently
  stalled (copy loop spinning, clients frozen, no log); now the copy loop
  notifies BroadcastManager on source EOF and an unexplained end
  auto-pauses onto the slate. Heartbeat = once-a-minute pid/bytes/kbps.
- **Pre-soak probes:** The Creator (2023) ruled the Tuesday asset — 1080p
  h264 progressive 23.976, DTS 7.1 default; encode-probe through the exact
  feeder args ran 4.0x realtime, DTS→stereo downmix clean.
- **Mini-soak results (all server swaps 0.2-1.4s, teardown zero-orphan):**
  baseline pause ✓, 5.5-min long pause ✓, page-driven resume ✓, rapid
  pause→resume 3.3s apart ✓ (slate lived 3.3s/24KB, no wedge, no burst
  after), natural end → auto-pause fired first try ✓, Stop from page ✓.
  Step 1 done-criterion met in full.
- **THE finding — zero-viewer buffer echo (roadmap 1b, ruled for v0.3.31
  before Tuesday):** both clients dropped during initial tune → feeder
  blocked on full stdout pipe ~2min → on retune, -re catch-up burst at ~3x
  realtime (heartbeat: 12.7 Mbps) → rejoining client's playhead landed the
  whole gap behind live. Measured echo: 1:50 / ~2:00 / 1:52 across three
  transitions — a constant offset, not drift. Fix: last-consumer disconnect
  kills the feeder + marks feeder-pending at current position; lazy-start
  respawns at live edge.
- **Lesser findings:** first-tune latency 18-22s (switcher spawn +
  Jellyfin stream probe — polish item); page 409-race alert copy is vague
  ("check the logs"); legacy switcher-select buttons confuse (die in step
  3); NAS clock runs ~28s ahead of the laptop (use server log timestamps
  for analysis); slate steady-state ~150-180 kbps (static card, expected).

## Arc 8 — 2026-08-16 night: v0.3.31 zero-viewer fix, shipped + verified

Same session, Jon's "ship it now". feed.ts consumers are now counted;
last-disconnect kills movie+slate feeders; EnsureSw2FeederStarted respawns
the movie feeder at the wall-clock live position (or a fresh slate when
paused). Verified live solo (cats video, claude-in-chrome driving Jellyfin
web): consumer-drop → "last feed consumer gone" log ✓; 4.5-min zero-viewer
gap; retune → feeder respawned at 674.0s, wall-clock exact ✓; first
heartbeat 4231 kbps, no catch-up burst (old behavior: 12721 kbps) ✓;
position clock runs while untuned (live-channel semantics) ✓; clean stop ✓.

- Note: Jellyfin holds the tuner stream open long after a web client
  navigates away without a proper stop — used POST /LiveStreams/Close
  ?liveStreamId=... (query form; JSON body form 400s) to close it
  deliberately during the test.
- **NEW BUG, reproduced twice on desktop Chrome (and matches Jon's phone
  earlier tonight): Jellyfin web live-TV playback of our channel wedges at
  video readyState 0.** Server blameless — the session's own live.m3u8 URL
  returns a valid multi-segment playlist (verified by fetching the player's
  exact URL from the page), Jellyfin's Remux ffmpeg produces segments at
  1.04x, but hls.js polls the playlist and never requests one segment; no
  console errors; fresh page load did NOT clear it this time. Roku plays
  the same stream fine. Investigate before relying on web clients
  (Tuesday's soak can be Roku-led if unresolved).

### Arc 8 addendum — the "web broken" scare, resolved (same night)

Systematic elimination: NOT Jellyfin Enhanced (disabled it - still wedged;
re-enabled after), NOT stale sessions (fresh tab, fresh page - still
wedged), NOT transient server state (survived 5 restarts), server provably
correct at every step. Then the reframe: ALL failing desktop tests ran in
claude-in-chrome automated tabs; Jon testing BY HAND: **Firefox plays,
Xbox plays** (Roku already proven). The automated-tab wedges were a test-
rig artifact (background/automated tabs get throttled timers + different
activation semantics). LESSON: client playback of this channel is only
validly tested by a human on a real screen - never trust automated-tab
playback state.

The one genuine failure: **Jellyfin Android app (integrated player) +
Live TV** - black player, 00:00/00:00 playbar, on first tune (pre-seam);
same phone DirectPlays VOD fine, connectivity fine. Hypothesis + fix
candidates + the settled live-edge model: ROADMAP-2026-08-16.md §1c.
Diagnostic API notes: POST /LiveStreams/Close?liveStreamId=... (query
form) closes a stuck tuner stream; /Sessions LastActivityDate only
updates on API calls - an app idling on a dead player looks inactive
while still connected (misread once tonight).

### Where the next session starts

Roadmap step 2: Tuesday 2026-08-19 soak of The Creator (4-pause schedule +
natural end), Roku/Xbox/web-led; Android participates only if 1c's phone
experiments (integrated player off / quality cap) pan out earlier. Also
pending: Roku periodic-stutter forensics (FFmpeg session logs preserved
from arc 7); remuxer timestamp-continuity probe (1c).

## Arc 9 — 2026-08-18 evening: the Tuesday soak FAILED, and why

Roadmap §2 (movie-length soak) run for real: **The Creator (2023)**, three
clients at once (Xbox + Roku + web) on v0.3.31. All three froze. Verdict:
**FAIL**, with a root cause that invalidates the delivery half of switcher v3.

### What was NOT wrong

Jellyfin never crashed (10.11.6, 23 ms API responses throughout). The NAS never
went down. The feeder never died. `SwitcherProcessCount` stayed at 1 — our side
of the pipeline is genuinely N-independent, as designed.

### The measurement that found it

All three client-side processes were `-c copy` remux (`q=-1.0`), running at:

```
0.915x    0.933x    0.927x
```

A copy remux cannot be CPU-bound. A copy at 0.92x is **starved by its input**.
And 22 fps / 23.976 fps = 0.918 — the number matches exactly. All three read
the same shared `stream.ts`, so all three starved together. That is why they
froze simultaneously rather than one at a time.

### Root cause: O(N) disk on a single spindle

Jellyfin's per-client ffmpeg (one per session, always, even with matching
codecs) is launched as:

```
-f hls -i .../master.m3u8  -codec:v:0 copy -codec:a:0 copy
-f hls -hls_segment_filename /config/cache/transcodes/<session>%d.ts
-hls_playlist_type event -hls_list_size 0
```

`-hls_list_size 0` + `playlist_type event` = **segments are never deleted**.
Each client accumulates the entire broadcast on disk (~3.9 GB per client for a
2h film). Three clients wrote ~12 GB continuously while the feeder read the
38.2 Mbps source — all on ONE 7200rpm spindle.

Feed heartbeat byte-deltas tell the story the printed cumulative kbps hides:

```
0-841s      ~4260 kbps, heartbeats exactly 60s apart   healthy
841->952s    2018 kbps, 111s gap                       collapsing
1012->1110s   988 kbps,  98s gap                       worst
1110->1170s 10209 kbps                                 burst as clients died
1170->1350s  ~4270 kbps, 60s apart                     ZERO viewers, perfect
```

The feed recovered the instant the clients died, on the same 38.2 Mbps source,
same feeder pid (8520, never restarted). Source bitrate is an **amplifier**,
not the cause; client-count contention is the cause.

### NAS state during the failure

```
load        11.4 / 12.7 / 15.8 on 4 cores      iowait 38.8% (4.4% idle)
RAM         5819 / 7691 MB                     swap 5293 / 5892 MB
disk        ONE 7200rpm 8TB WD (ROTA=1), /volume1 92% full
zombies     2482
```

`/System/Logs` (a directory listing) took **23.4 s** while the API answered in
23 ms — CPU and network fine, disk saturated. Load fell to 2.16 once everything
was stopped.

**The 2482 zombies are NOT ours** — every one is parented by the
`velocity-dashboard` container (velocity-agent stack) leaking unreaped python
children. Separate bug, own ticket, but it contributed to the box's baseline.

### The prediction that was never verified

DECISIONS.md 2026-08-14 recorded: *"one encoder, Jellyfin fans out. Verify pid
count stays 1 with a second real client when convenient."* That verification
never happened. It was **half right**: the upstream connection IS shared, but
each client still gets its own downstream ffmpeg. The unverified half is
exactly what failed. Flagged-but-unverified assumptions cost a movie night.

### Spike: does serving HLS avoid the per-client hop? NO

Zero code needed — the old Phase-2 HLS path (`POST /MovieNight/api/golive`) is
still present and did not bit-rot. Baseline **2** encoder processes with no
clients; **3** with one client tuned. Jellyfin interposed a per-client ffmpeg
even though the codecs already matched exactly and it was doing a pure `copy`.
This also retroactively explains why spikes 3-6 died: same hop.

### Spike: server-driven playstate on VOD (approach 2)? NO on Roku

```
                    at pause      +~15s
Xbox     paused=True   285.3s  ->  285.3s     frozen. honored.
Roku     paused=False  286.0s  ->  302.0s     kept playing. ignored.
```

Both returned HTTP 204 — the server dispatched both. Roku reports
`SupportsMediaControl: false` and meant it. Roku is a required client, and this
is client-side code no server plugin can change. SPEC §21's rejection of
SyncPlay was correct, and now the mechanism is documented rather than inferred.

By-product worth keeping: both clients played a normal library item via
`PlayMethod: DirectPlay` — zero server-side ffmpeg. Clients WILL pull media
directly when negotiation allows it; the interposition is specific to Live TV,
not a universal law. That is the premise the next design rests on.

### Where the next session starts

`planning/DESIGN-abr-ladder.md` — designed and ruled, gating spike not yet run.
Do the gate first: minimal custom `ITunerHost` -> static pre-generated ladder ->
one client -> count ffmpeg processes. Everything else is blocked on that answer.

---

## Arc 10 (2026-08-19) — T0 GATE PASSED: Jellyfin will hand the URL to the client

The gate from `DESIGN-abr-ladder.md` §8. Built as v0.3.32.0, answered on
v0.3.33.0. **Result: PASS**, on real Roku hardware.

### What was built

A deliberately minimal spike — no feeder, no live pipeline, no muxer, no QSV,
so that nothing except the question itself could fail:

- `T0Gate` — generates a static three-rung HLS ladder (synthetic testsrc/tone,
  software x264, one fixed closed GOP across all rungs) and instruments every
  fetch of it: remote address, user agent, path, hit/404.
- `T0GateTunerHost` — a custom `ITunerHost` advertising one channel on 901.
- `GET /MovieNight/stream/hls/{**path}` — anonymous for the spike, guarded by an
  exact-name whitelist (unit-tested against traversal and near-miss names).

### The first run failed on TWO of my own bugs at once

Both were field-value choices, not facts about Jellyfin, which is why one tune
could not separate them:

1. **Relative URL.** `Path`/`TranscodingUrl` were `/MovieNight/stream/hls/
   master.m3u8`. Jellyfin passed that to ffmpeg as a FILE path:
   `-f hls -i "/MovieNight/stream/hls/master.m3u8"` → exit 254 in 30 ms.
   `TotalHits: 0` — nothing ever fetched the ladder, not even the server.
2. **`Container = "hls"`.** Jellyfin answered `TranscodeReasons:
   ContainerNotSupported`. No device profile lists `hls` as a DIRECT-PLAY
   container — clients list it as a transcoding TARGET — so the profile matcher
   rejected direct play before `SupportsTranscoding=false` was ever consulted.

Lesson, already learned once during the QSV keyframe hunt and evidently not
learned well enough: **make the knob remote BEFORE running the experiment.**
v0.3.33.0 moved `baseUrl`, `container` and the four booleans to a runtime
endpoint (`POST t0/source`), turning a ~4-minute NAS restart per hypothesis into
a curl call. That change is what made the second run cheap.

### The PASS, measured

`container=ts`, `baseUrl=http://192.168.68.118:8096`, one Roku Ultra tuned:

| criterion | result |
|---|---|
| new ffmpeg processes | **0** (identical to baseline) |
| `PlayMethod` | **DirectPlay** |
| `TranscodeReasons` | **None** |
| ladder fetches | **56, all from 192.168.68.121** (the Roku), UA `Roku/DVP-15.3` |
| 404s | 0 |

`container=ts` was the unlock, not `SupportsTranscoding=false`. Worth
remembering which field did the work.

### Free result: the Roku did ABR unprompted

```
20:10:07  v1/index.m3u8   started on rung 1
20:10:17  v2/index.m3u8   dropped to rung 2
20:10:31  v0/seg_39..47   climbed to rung 0, pulled 9 segments in ~1s
```

Three rung switches, no one constraining anything, on the hardest client. T7's
question answered affirmatively in passing, and the GOP alignment is real.

### Also confirmed / also learned

- **`RequiresOpening=false` IS honoured** — `GetChannelStream` was never called
  across either run. Jellyfin does not open a server-side live stream for this
  source.
- **The static ladder is sound**, proven independently of any client: Jellyfin's
  own ffmpeg read the master playlist through the plugin route, selected rung
  v0, pulled segments over HTTP and decoded 240 frames at 187x, exit 0. This
  separated "our ladder is broken" from "Jellyfin insists on transcoding"
  without costing a human tune.
- **VOD behaviour, and why the sliding window is load-bearing.** The T0 ladder is
  `PLAYLIST-TYPE:VOD` + `ENDLIST`, so there is no live edge. The Roku fetched
  all 75 segments within ~60 s while its playhead sat at 270 s of 300 s — it
  raced to the end at disk speed. Nothing in T0 tests live-edge join, latency,
  or straggler convergence; those need M2's sliding window (T3) and the
  load-bearing 404 (T6).

### What this does NOT establish

- **Roku only.** Android failed the first (buggy) round and was not retried; web
  and Xbox untested. The compatibility matrix has one cell filled.
- **The LAN base URL is a scaffold.** It passed with `http://192.168.68.118:8096`
  hardcoded. Remote viewers over tailscale will not reach that — the real tuner
  host (M4) must derive a per-client address (`GetSmartApiUrl`) rather than hold
  a constant.

### Unrelated, found on the way

- `Update Plugins` is **failing server-wide** on this NAS:
  `IOException: ... 'Jellyfin Tweaks_4.0.0.0/thumb.png' because it is being used
  by another process`, thrown from `PopulateManifest` inside
  `GetAvailablePackages`, which aborts the whole scan. No plugin can update via
  the task. Workaround used throughout: `POST /Packages/Installed/{name}?version=`.
- The `velocity-dashboard` zombie leak (2482 on 08-18) had reached **2533** with
  the box at load 7.24 / **64.2 % iowait** / 91 % swap. The container was
  recreated mid-session, which reaped all of them: 0 zombies, load 2.33, iowait
  8.7 %. Root cause unfixed — it accrues ~50/day, so it will be back.

### Arc 10b (same evening) — the client matrix, and why `Container` needs a per-client answer

Tested the same passing ladder against Roku and the Android app by flipping the
runtime knob. The results do not agree with each other:

| `Container` | Roku | Android |
|---|---|---|
| `"ts"` | **DirectPlay, 0 ffmpeg, 56 fetches from the Roku's own IP** | `ContainerNotSupported` → hop → `-f mpegts` on a playlist → **exit 187** |
| `null` | **Transcode** (regressed — all fetches from the server) | Transcode; plays, because with no `-f` ffmpeg auto-detects HLS |

`Container` does double duty and the two duties disagree:

- it feeds the **client's direct-play profile matcher**, and
- it picks **ffmpeg's `-f` input demuxer** on any server-side fallback.

So `"ts"` bought Roku direct play and poisoned the fallback; `null` fixed the
fallback and cost Roku direct play. Both clients "worked" under `null` — via the
per-client ffmpeg the whole design exists to delete. **"It plays" is not the
criterion; `PlayMethod` plus who fetched the segments is.**

### The profile data

`GET /Sessions` → `Capabilities.DeviceProfile`:

```
Roku    DirectPlay video containers: dash, hls, ism, mkv,webm, mp4,mov,m4v, ts
Android no DeviceProfile registered  (the app sends its profile per PlaybackInfo request)
Web     no DeviceProfile registered
```

**Roku accepts `hls` for direct play.** It was never actually disqualified — the
one time we tried `hls`, the URL was still relative and unfetchable, and the
failure was mis-attributed to the container. `hls` is therefore the only value
that could satisfy both sides at once: Roku direct-plays it, AND it hands a
correct `-f hls` to any client that does fall back. **Untested; test before
assuming a per-client mechanism is needed at all.**

### The constraint this exposes (not in the design doc)

`ITunerHost.GetChannelStreamMediaSources(channelId, ct)` returns ONE
`MediaSourceInfo` per channel and is never handed the requesting device's
profile. If no single `Container` satisfies every client, M4 must vary the
media source per requester. Two mechanisms, only one of which actually helps:

1. **Vary the MediaSourceInfo per requester (this is the one that works).** The
   call happens on the PlaybackInfo request thread, so the client identity is
   reachable from ambient request state (`IHttpContextAccessor` → auth info's
   Client/DeviceId), and for clients that register capabilities (Roku does,
   Android does not) `IDeviceManager` can return the profile by device id.
   Fallback for unregistered clients is a client-name heuristic.
2. **Vary the CONTENT served at the ladder URL by User-Agent.** Easy — our route
   already sees the client directly — but it cannot fix anything, because a
   client that is refused direct play never reaches our URL at all. Worth
   recording so it is not proposed later as a solution to this problem.

### Measurement correction

An ffmpeg process census is NOT a reliable instrument here. Jellyfin remuxes
with `-readrate 10`, so it races through the 5-minute VOD ladder in ~30s, exits,
and the client plays out of `/config/cache/transcodes` — a census taken late
reads 0 whether or not a hop happened. The arc-10 PASS does not rest on it
(`PlayMethod: DirectPlay` plus fetches from the Roku's own IP are both
timing-independent), but `PlayMethod` + the hit log are the primary instruments
from here.

### Web: still unexplained

Web does not play, before or after a hard refresh, and produces **no server-side
error at all** — no ffmpeg, no exception, nothing in the log. Ruled out: CORS
(our route returns `Access-Control-Allow-Origin: *`). Leading hypothesis:
**mixed content** — a Jellyfin page served over `https://` will silently refuse
our `http://192.168.68.118:8096` ladder URL, which presents exactly as "nothing
happens". Needs the actual browser URL to confirm. Also unconfirmed whether the
web client in the session list (reporting the NAS's own address) is a browser on
a laptop or the `firefox` container running on the NAS itself.

### Arc 10c — T2 PASSED (ladder encode + GOP alignment), and fmp4 is free

Run via the `encode-probe` endpoint against a real library title
(`The Dressmaker (2015).mkv`, h264 1920x1080 **44.5 Mbps** 23.976fps — deliberately
the heavy-decode case, same class as the file that pinned a core in the failed
soak). Design §4(D) ladder shape: rung0 `-c copy`, rungs 1/2 `h264_qsv` at
3000k/1200k, `-g 96 -forced_idr 1`, `-hls_time 4`.

**T2 criterion 1 — encoder sustains >= 1.0x realtime: PASS, with headroom.**

Interleaved A/B/A/B (ordering alternated so drift cannot fake the result):

| run | segment type | fps | speed |
|---|---|---|---|
| 1 | mpegts | 42 | **1.73x** |
| 2 | mpegts | 49 | **2.04x** |
| 3 | fmp4 | 62 | **2.56x** |
| 4 | fmp4 | 60 | **2.49x** |

Every configuration clears 1.0x; slowest was 1.73x. An isolated earlier run
measured mpegts at 2.76x and fmp4 at **1.01x**, which looked like a serious
fmp4 penalty — it was a **loaded box** (load average 8.70, driven by the probes
themselves), not a property of the format. Reported only after the interleaved
repeat, which reversed it. §10.2's "believed comfortable, unmeasured" is now
measured.

**T2 criterion 2 — every rung's segments GOP-aligned: PASS, exactly.**

Identical under BOTH segment types:

```
v1 (720p qsv):  n=23  total=90.007  distinct durations = [4.004, 1.919 final]
v2 (480p qsv):  n=23  total=90.007  distinct durations = [4.004, 1.919 final]
```

4.004000s = **96 frames at 23.976fps**, to the millisecond, identical across
rungs. Clean rung-switching is demonstrated, not assumed. (The Roku had already
shown three unprompted rung switches in arc 10, which is the same property
observed from the client side.)

**T2 also demonstrated WHY the mezzanine is mandatory.** Rung 0 is `-c copy`, so
against an arbitrary movie it inherits that movie's GOP:

```
v0 (-c copy):  n=23  total=90.228  distinct = [1.681, 3.42, 3.67, 3.712, 4.004, 4.087, 4.171, 4.338]
```

Ragged boundaries and 0.22s of drift against the encoded rungs. The design
asserts the mezzanine must carry a fixed closed GOP matching the ladder's
(§4(A)); this is the empirical failure mode if it does not, and it is M1's
acceptance criterion.

**Segment-type finding: fmp4 costs nothing.** It was at least as fast as mpegts
in every paired run (mean ~2.53x vs ~1.89x; MPEG-TS carries ~5% packet overhead,
so cheaper fmp4 is plausible rather than surprising). Since fmp4 is also the
format most likely to widen client support (Firefox per arc 10b research;
possibly ExoPlayer per androidtv #5237, whose stated trigger is MPEG-TS), there
is **no encode-cost trade-off to rule on** — the earlier framing of this as a
tension was wrong.

**Implementation detail for M3:** fmp4 emits `#EXT-X-MAP:URI="init_1.mp4"` —
per-variant init files. Our ladder route's whitelist currently allows only
`master.m3u8`, `v[0-2]/index.m3u8` and `v[0-2]/seg_<digits>.ts`. It must also
allow `init_<digits>.mp4` (and `.m4s` segments) or fmp4 404s on the first byte.

**Still open before M1 commits to a format:** fmp4 has been proven on the
*encoder* side only. No client has played an fmp4 ladder yet — Roku, Xbox and
Chrome all passed on **mpegts**. A segment-type knob on the T0 ladder plus one
tune per client settles it, and that should happen before the mezzanine is built,
since the mezzanine must be produced in the chosen format.

**Housekeeping:** these probes wrote into the T0 ladder directory (the only path
the serving route exposes, so playlists could be read back), overwriting the
synthetic test-pattern ladder with 90s of The Dressmaker. Rebuild with
`POST /MovieNight/api/debug/t0/build-ladder` when the synthetic ladder is wanted
again.

## Arc 11 (2026-09-03) — v0.4.0.0 first live test: direct play works, PAUSE KILLS IT

**v0.4.0.0** (roadmap-09-03 step 1, built blind in a remote container, see
`HANDOFF-2026-09-03.md`) installed on the NAS by Jon and tested with real hardware.

**Result (Jon):** Go Live → Xbox and Roku both played the new channel, roughly
10 s apart from each other (expected: independent client buffers). **Pause →
both kept playing for at least a minute, then were kicked off.** Jon: "not
anywhere close to acceptable."

**Diagnosis (from code, not yet from logs — the local session should confirm
against `live/status` → `Encoder.LastExitCode` / `EncoderRestarts` / `Stderr`
and the server log around the pause):**

- At Pause the feed swaps from the film's stream to the slate's. In 0.4.0 the
  feeder is a COPY (no longer a QSV encode to 720p), so the encoder now sees
  the raw film (e.g. 1920×800 / 23.976 / maybe 10-bit) followed by the slate
  (1280×720 / 30 / 8-bit). That is a resolution + aspect + pix_fmt + fps change
  at the seam.
- ffmpeg rebuilds the filter graph on that (default `-reinit_filter 1`). But
  (a) an already-open encoder cannot accept new frame dimensions — the 0.4.0
  chain was `scale=-2:720` / `scale_qsv=-1:720`, whose OUTPUT size changes
  with the source aspect; and (b) on QSV the rebuilt `hwupload` hands
  `h264_qsv` a brand-new hardware frames context. Either kills the encoder.
- LiveSession restarted the dead encoder into the same directory. The new
  process numbered segments from 0 again → the playlist's media sequence went
  BACKWARDS → Roku and Xbox played out what they had buffered and abandoned
  the stream. "About a minute" ≈ buffer + stall/retry before giving up. After
  5 restarts LiveSession stops the session and withdraws the channel.
- v3 never hit this because its FEEDER encoded everything to 720p first, so
  the persistent process only ever saw one geometry.

**Fix — v0.4.1.0:**

1. **Fixed output geometry per rung.** Every chain is
   `scale=W:H:force_original_aspect_ratio=decrease,pad=W:H:-1:-1,setsar=1`
   then a fixed pixel format, in SYSTEM memory; hardware encoders take
   system-memory frames directly (h264_qsv uploads itself). A 2.39:1 film is
   letterboxed into 1280×720; the slate fills it. Nothing the encoder sees can
   change at a seam. The old `hwupload`+`scale_qsv` chain survives only as a
   debug knob (`hardwareScale=true`) for A/B.
2. **Sequence-continuous restarts.** A crash restart now passes
   `-start_number <highest seg on disk + 1>` and `+discont_start`, so numbering
   and media sequence continue and a discontinuity is declared.
3. **Segment length is a setting** (2–6 s, default 4). Pause-to-screen latency
   is ~3–4 segments; 2 s halves it if the encoder keeps up.

**Not fixed, by design:** the ~10–20 s from clicking Pause to the slate on
screen is client buffer depth (Jon's ruling: ~15 s OK). If the fix works, the
slate should appear on both clients within that window and nobody gets kicked.

**Open until re-tested:** whether h264_qsv on this NAS accepts system-memory
nv12 input at ≥1× realtime (it is the standard `-c:v h264_qsv` usage, but this
NAS has surprised before — `hardwareScale=true` restores the T2 chain if not,
at the cost of the seam bug returning); whether the seam is clean through the
software path (film → slate → film, three geometry changes per pause cycle).
