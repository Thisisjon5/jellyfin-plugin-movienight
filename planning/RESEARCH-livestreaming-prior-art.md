# Research: How Everyone Else Does Library-to-Live-TV — and Why Our Problem Is Hard

2026-08-14/15. Companion to `ITERATION-LOG.md`. Sources researched: ErsatzTV,
dizqueTV, Tunarr, StreamMaster, xTeVe/Threadfin, TVHeadend, Jellyfin
SyncPlay / Plex Watch Together, broadcast SSAI (ad-insertion) practice,
ffmpeg filter internals, and the Jellyfin issue tracker.

## The one-paragraph answer to "why can't we solve this"

Because it's two problems glued together, and only one of them has ever been
solved. **"Turn a library into a live channel"** is mature prior art —
ErsatzTV, dizqueTV, and Tunarr all do it, all fight exactly our seam problem,
and all still break at program boundaries in production (open issues cited
below). **"A host pauses that live channel for every viewer and resumes at a
timestamp"** exists nowhere. Not in the channel generators (ErsatzTV's only
pause-adjacent bug report was a deadlock; its "On Demand" playout mode is
buggy and per-session-idle-driven, not a pause button). Not in group-watch
systems (Jellyfin SyncPlay has shared pause but only for VOD items — and it
is not implemented on Roku at all). Not in DVR/timeshift systems (pause there
is a per-viewer buffer pointer; the broadcast never stops). The only
production tech with the right *shape* is broadcast slate/ad insertion
(SCTE-35 SSAI): briefly substitute other content into a continuously-running
stream without breaking players. We are, genuinely, building a thing that
doesn't exist — a live channel with watch-party pause semantics — which is
why every "obvious" approach has fought us.

## What the channel generators actually do

| Project | Tuner surface | Stream mechanics | Boundary/seam handling | Pause? |
|---|---|---|---|---|
| **ErsatzTV** | M3U+XMLTV, HDHomeRun emu | "HLS Segmenter" (continuous transcode into ffmpeg's HLS muxer) is the recommended mode; MPEG-TS mode wraps it; "HLS Direct" (remux-only) documented as having client issues at program boundaries | Normalize everything to one per-channel ffmpeg profile so there's no format jump at seams | Pause on Jellyfin **deadlocked the worker** (issue #1789, fixed as a bug); "On Demand" playout freezes progress when nobody streams — per-idle, not a button, and buggy (issue #2021) |
| **dizqueTV** | HDHomeRun emu, M3U | One ffmpeg per channel: concat demuxer over a generated playlist → MPEG-TS to stdout | Punted to the user: "most players will break after switching episodes if formats differ" — optional transcode normalization | None found |
| **Tunarr** | HDHomeRun emu, M3U | Two-stage: short-lived ffmpeg **per program** (normalize/transcode) + a concat/scheduler stage stitching them into one continuous output | **The best answer found anywhere:** tracks the last packet timestamp of each program and injects a precise offset into the next transcode session so output timestamps are monotonic across the channel's lifetime; forces IDR keyframes at boundaries | None found |
| **StreamMaster / xTeVe / Threadfin** | HDHomeRun emu | Proxies for *existing* IPTV streams — no library-to-channel generation, seams not their problem | n/a | n/a |

Cross-cutting: everyone exposes M3U+XMLTV or HDHomeRun, everyone serves
MPEG-TS or HLS, and the **program-boundary hand-off is where all of them still
break** — Tunarr's open issue #1727 (scheduler halts between programs, ffmpeg
exits clean, "no next item queued", across all modes and 14 channels) is a
production system hitting the same class of failure our splice designs hit.
Notably, Tunarr's per-program normalize stage is **software-encode only**
(documented limitation) — our QSV feeder is ahead of prior art there.

## What "pause live TV" actually is elsewhere

- **DVR/timeshift (TiVo, TVHeadend, Plex DVR):** the feed never stops; each
  viewer has a buffer read-pointer they pause. TVHeadend's "on-demand"
  timeshift is the cleanest implementation. Fundamentally per-viewer — the
  inverse of our requirement. Jellyfin's own live TV has only a
  rewind-to-tune-in buffer, no real server DVR pause (and issue #16880 shows
  pausing doesn't even stop the upstream tuner pull).
- **Jellyfin SyncPlay:** real shared play/pause/seek, fully scriptable REST
  (`/SyncPlay/New`, `/Join`, `/Pause`, …), NTP-style clock sync with rate-nudge
  drift correction. Two disqualifiers: it operates on a **VOD item**, not a
  live tuner stream (adopting it = abandoning the live-TV/EPG model entirely),
  and **it is not implemented in the official Roku client** (open request).
- **Broadcast slate insertion (SSAI / SCTE-35):** the industry's mechanism for
  substituting content into a running live stream — cue markers, slate
  segments spliced in, `EXT-X-DISCONTINUITY` at the seams, program content
  stitched back at cue-in. This is a "pause card" in all but name, and it's
  the correct mental model for our feature. It also comes with the documented
  warning that discontinuity handling is fragile in real players (hls.js has
  multiple stuck-player issues around it).

## Our failures, cross-referenced against the literature

Every major failure from today's iterations (see ITERATION-LOG.md) matches a
documented, known pitfall — none were exotic:

1. **Frozen playlist/stream = player death** (spikes 3, 5 v1; v2 pause stall).
   Confirmed industry-wide: hls.js issues #7008/#7556 (players stuck when a
   live manifest stops advancing); Tunarr #1911 (60s heartbeat kills "stalled"
   sessions). Rule: *the stream must always be advancing, with something.*
2. **Discontinuity markers don't survive intermediaries** (the 18:37 freeze —
   clients watching across our splice stalled because Jellyfin's
   `-codec copy -copyts` remux dropped the `EXT-X-DISCONTINUITY` signal).
   SSAI practice assumes the splicer is the *last* thing before the player;
   we have a remux hop after us, so manifest-level signaling can't work.
   Re-encoding through one continuous process (making seams invisible rather
   than signaled) is the only shape compatible with the remux hop.
3. **Multi-input ffmpeg graphs stall when one input starves** (v0.3.25's pause
   card never airing). Documented ffmpeg behavior: framesync-based/multi-input
   filters have `eof_action`/`shortest` semantics precisely because starved
   inputs wedge graphs; nothing fails loudly. Rule: *the persistent process
   should have exactly one input.*
4. **Timestamp cliffs at source swaps.** Tunarr's entire architecture exists
   to solve this (monotonic offset injection). Our v0.3.25 test showed
   ffmpeg's input discontinuity compensation healing a feeder swap
   automatically when re-encoding — same idea, less precision. Prior art says:
   handle timestamps deliberately, don't rely on auto-healing.
5. **Jellyfin M3U tuner quirks are known-bad territory:** simultaneous-stream
   limit produces silent spinners (#4817 — bit us at 18:42 tonight),
   limits not honored (#10996), SharedHttpStream cleanup gaps (#12241),
   loopback-only remux URLs (#6082). Our TunerCount=0 change and loopback
   design already navigate these.

## What this means: the convergent architecture

Prior art + tonight's spikes point at one design, each piece independently
validated:

1. **One persistent output process per channel** (Tunarr's concat stage ≈ our
   switcher) producing continuous MPEG-TS for the tuner. Single input. Its job:
   re-encode and keep output timestamps monotonic across whatever arrives.
2. **Swappable feeder processes** (Tunarr's per-program stage ≈ our QSV
   feeder) writing into a local feed the persistent process consumes — movie
   at position X, or the pause slate loop. "Pause" = swap slate feeder in,
   kill movie feeder, log position. "Resume" = movie feeder at
   pausedAt−rewind, swap back. The feed never stalls (rule 1), the persistent
   graph has one input (rule 3), no manifest signaling needed (rule 2).
3. **Timestamp discipline at the swap** — either trust the re-encode's input
   compensation (observed working once) or do it Tunarr-style with explicit
   offsets. To be validated under load.
4. **Perf budget:** the double encode must fit the NAS — persistent stage
   should use QSV too (v0.3.25's software x264 ran 0.55x realtime on the
   swap-loaded box), or the feeder hands over cheap-to-decode mezzanine.

Alternative worth naming once: **Liquidsoap** — mature radio/video automation
built exactly for "seamlessly swap sources feeding one continuous output"
(priority fallback, crossfade, battle-tested). It would replace our feeder/
switcher plumbing with a new resident component on a RAM-tight NAS; noted as
the buy-vs-build option, not the default.

And the scope-reduction option, also named once for honesty: **per-viewer
pause** (each viewer rewinds/pauses independently against a retained segment
buffer) is boring, solved technology — if shared pause ever stops being worth
its cost, that's the fallback. SyncPlay-VOD is *not* a fallback while Roku
matters.
