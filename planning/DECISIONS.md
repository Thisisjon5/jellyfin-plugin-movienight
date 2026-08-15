# Decisions log

Rulings from architect conversations with Jon. Binding on downstream PRD/spec
work unless a later entry here reopens them. Newest at bottom.

---

## 2026-08-14 — Universal pause (v1 scope reopened)

**Context:** SPEC.md §3 listed "pause-live-TV" as an explicit v1 non-goal and
§14 listed "pause-live" as a v2 candidate. Jon reopened this for v1, ahead of
finishing Phase 3 as originally spec'd, to support a "movie night with
friends" use case (co-watching while chatting on Discord).

### Ruling: authority split

- **Anyone can trigger pause. Only the host can trigger resume.**
  Jon: "That is the distinction: anyone can hit pause, host has to hit
  resume."
- Pause is **automatic once detected — no host approval gate.** Jon: "Someone
  hits pause, that ideally very quickly flows back to the host, at which
  point it pauses for everyone else. When the host is ready they hit resume
  and everyone's clients sync back to the host's timecode." Confirmed
  explicitly: no approval step, host's control surface just reflects that a
  pause happened.
- **"Host" = the existing admin config page** (§7 of SPEC.md, already has
  Go Live/Stop under `RequiresElevation` auth). Resume + rewind are new
  buttons on that same page — **no new client-facing control surface for the
  host side.** Confirmed explicitly (no separate "host" client concept).
- **The only viewer-side pause trigger is hitting native pause in their own
  Jellyfin client** (Roku remote, web player, Xbox, Swiftfin) — detected
  server-side via session polling, not a separate control page for
  non-admins. Confirmed explicitly: "the native-pause-button-detection path
  is the only viewer-side trigger you want (not a separate control surface)."

### Ruling: what pause/resume needs to guarantee

- **Not frame-accurate for stragglers.** A client that's a few segments
  behind live when pause hits will see the freeze a few seconds later than
  clients at the live edge — Jon accepted this as fine for v1: "I think
  thats fine as a V1."
- **Resume tolerance: backing up 10-30 seconds and restarting everyone
  together is fine.** Jon: "when we decide ultimately that we are ready to
  start backup again, its totally cool if we need to backup 10-30 seconds,
  start everyone at the same spot again, and keep going." No per-client
  exact-position preservation needed — resume lands everyone on one shared
  timecode, not wherever each client happened to be when it paused.
- **Resume source of truth is the server clock** (the frozen pause
  position), not any individual client's position. Jon: "we should lock in
  on 'everyone starts back from the server clock'."
- **Host can optionally rewind the resume point before resuming** — e.g. a
  fixed "rewind 30s" button, or a seek-back-then-hit-resume flow. Jon left
  the exact UI shape open ("maybe... or maybe its simply seek back then they
  hit resume") — this is a UI-affordance decision, not an architectural one;
  defer the exact control shape to the config-UI build step. Architecturally
  it's just an adjustment to the stored pause-position value before resume
  fires.

### Ruling: drift ceiling during normal (non-paused) live playback

- **No viewer should ever be allowed to drift behind live edge by 2-3
  minutes.** Jon: "What I dont want is someone drifting behind by 2-3
  minutes at any point in time. I dont want them to be able to pause or have
  to seek forward because they're behind live edge by that much. This is
  meant to be a purely live, communal experience."
- This is a **separate, always-on drift-correction system**, independent of
  whether anyone has ever paused — not folded into the pause feature, but
  using the same underlying primitive (force a session to seek to timecode
  X).
- **Exact drift threshold is deferred — "we can tune during the spike."**
  Not chosen yet.
- **Hard technical ceiling already exists from Phase 2 config:** SPEC.md
  §5.2's `hls_list_size 10` × 4s segments with `delete_segments` retains only
  ~40 seconds of live window. A client drifting past that doesn't degrade
  gracefully — it 404s on deleted segments and hard-breaks. So "2-3 minutes
  of drift" is not actually reachable under the current config; correction
  must trigger well before the ~40s wall, and/or the retention window gets
  widened (disk cost, per the existing disk-guard math in §5.4 — currently
  ~75MB steady state, scales with window size). Not yet decided whether to
  widen it — deferred to the spike.

### Enforcement mechanism (validated via research, not yet spiked)

Researched `ISessionManager`/`SessionInfo` API surface directly from the
`Jellyfin.Controller` 10.11.6 / `Jellyfin.Model` 10.11.6 NuGet package XML
docs (same technique used for the Phase 1/2 `IGuideManager` research —
`MediaBrowser.Controller.xml` / `MediaBrowser.Model.xml` next to the cached
DLLs):

- `SessionInfo.PlayState` (a `PlayerStateInfo`) exposes `IsPaused` and
  `PositionTicks` directly, live, per session. `ISessionManager.Sessions` is
  a plain enumerable — the plugin can poll it on its own cadence rather than
  depending on event timing. A `PlaybackProgress` event with the same fields
  also exists if event-driven proves tight enough.
- `ISessionManager.SendPlaystateCommand(controllingSessionId, sessionId,
  PlaystateRequest, ct)` takes a `PlaystateCommand` enum including `Pause`,
  `Unpause`, and `Seek` — the same mechanism Jellyfin's own dashboard
  "Now Playing" panel uses for admin remote control of another session.

**Chosen shape (follows from the above + the resume design Jon described):**

- **Server-side freeze**, not literal `ffmpeg` process suspension: kill the
  real encode, spawn a frozen-last-frame encode that keeps cutting segments
  on the normal cadence (so the existing stall watchdog and HLS live-
  playlist pull model are untouched — no special-casing needed), then on
  resume kill the freeze encode and respawn the real encode at the
  (possibly rewound) paused position via `-ss`.
- Server-side freeze is required **regardless** of `SendPlaystateCommand`
  working — if the underlying encode kept advancing while clients were
  locally paused via a command, resuming would mean catching up to the live
  edge, which is exactly the DVR/catch-up behavior SPEC.md §3 rules out as a
  non-goal.
- `SendPlaystateCommand(Pause)` is pushed to every other session as an
  addition on top of the server-side freeze, so local players actually stop
  rendering/pulling rather than sitting on a frozen frame while their local
  buffer plays out.
- Resume forces every session to `Seek` + `Unpause` to the one shared
  server-clock timecode.

### Spikes queued (not yet run)

Both needed before implementation starts — same class of uncertainty as the
Phase 1 tuner-registration spike, i.e. genuinely unverified against real
Jellyfin behavior:

1. **Does `PlayState.IsPaused` / `PositionTicks` report correctly and
   promptly for a Live TV tuner session specifically** (not just VOD, which
   is the documented/tested use case)? This is the load-bearing risk — it's
   the only trigger surface for "anyone can pause." If it doesn't hold up,
   "anyone can pause" has no viable mechanism without inventing a new
   viewer-facing control surface (out of scope per the ruling above).
2. **Does `SendPlaystateCommand(Pause/Unpause/Seek)` actually get honored by
   real clients while playing the Movie Night channel** — web first (cheap),
   then Roku (documented in SPEC.md §11 as the pickiest tier-1 client, most
   likely to diverge from spec-compliant behavior).

Drift-correction threshold and HLS retention-window sizing are also tuned
during these spikes rather than decided up front.

---

## 2026-08-14 — Universal pause: mechanism replaced (filler-channel splice)

**Context:** the four spikes queued above were run tonight. Spike 1 (session
polling) confirmed clean. The rest changed the plan:

- **Spike 2:** `ISessionManager.SendPlaystateCommand(Pause/Unpause/Seek)`
  sent successfully (200 OK) against Jon's real Firefox session on the live
  channel but was not honored — position kept advancing, zero client effect.
  The command-push half of the originally-ruled mechanism is dead.
- **Spike 3:** replaced the real encode with a synthetic `color=black`/
  `anullsrc` frozen source writing to the same HLS filenames. Worked at
  first on both a real browser and a real Roku, then the synthetic encoder
  stopped producing new segments after some seconds, tripped the 30s stall
  watchdog, broadcast marked `Failed`. Root ffmpeg-level cause undiagnosed
  (failure path didn't capture stderr). Code fully replaced, not left in repo.
- **Spike 4:** replaced spike 3 with `SIGSTOP`/`SIGCONT` on the real running
  ffmpeg process. Mechanically clean twice in a row (state stayed `Live`,
  no crash, no watchdog false-trigger) but freeze/resume was not
  synchronized across clients — Roku consistently lagged the browser,
  sometimes by many seconds, and one moment the browser appeared to resume
  on its own with zero action taken, then re-froze (likely Jellyfin's own
  downstream remux draining an already-queued buffer, not a real resume).
  **Jon explicitly rejected this as-tested** ("didn't like what I saw").

**Root cause identified:** Movie Night's pipeline has two ffmpeg hops, not
one — our own encoder, and a second per-client ffmpeg Jellyfin's Live TV
subsystem always spins up to remux/re-serve our stream (confirmed via
`SupportsDirectPlay: false, SupportsDirectStream: false` on the
`MediaSourceInfo` Jellyfin builds for our tuner source — appears inherent
to any Live TV tuner source, not tied to our choice of HLS). Each hop plus
the client's own local buffer drains at a different rate before showing the
freeze, which is exactly the desync spike 4 exposed. This resolves spec
§13 open question #3 in the negative: Jellyfin does insert its own remux
hop for our tuner source.

### Ruling: pause mechanism is a filler-channel splice, not a frame freeze

Jon: "Maybe its not a freezeframe... maybe we have a waiting room video or
something queued up... when we 'pause' we go back to the waiting room video
or a pause video, then when we resume we fire up the movie stream again."
Confirmed and refined across the conversation into the following shape —
same class of pattern as broadcast playout automation (Jon's own prior
experience with Elemental/Grass Valley at Netflix): a primary content
source plus a filler/slate source, spliced into one continuous live output.

- **One reusable filler-encoder slot**, not three standing processes.
  Pointed at whichever of three bundled video assets matches the current
  broadcast state: waiting-room (pre-show), pause-card (mid-movie standby),
  thank-you card ("Thanks for watching" — Jon's exact copy, natural end).
  Restarted (reconfigured) between them at state transitions.
- **Splice primitive: `#EXT-X-DISCONTINUITY`** (core HLS spec, RFC 8216) —
  explicitly **not SCTE-35**. Jon asked directly whether this meant literal
  SCTE-35 markers; ruled no — SCTE-35 is the ad-*decisioning* cue layer for
  per-viewer/regional targeted content substitution, which this doesn't
  need (one global switch, same content to every viewer). Only the
  underlying "this segment isn't a continuation, don't treat it as a stall
  or corruption" primitive is needed.
- **Warm/cold requirements per transition, ruled explicitly:**
  - Channel-open → movie start ("select movie"): filler slot restarts from
    waiting-room content to pause-card content. Tolerable brief gap —
    host-paced, not viewer-surprise-paced, nobody's watching the movie yet.
  - **While the movie plays: the filler slot (pause-card content) must stay
    warm and continuously producing new segments the entire time** — this
    is the one hard constraint, because pause timing is viewer-unpredictable
    and must splice instantly, zero cold start.
  - Pause: kill the movie encoder, record position, splice to the
    already-warm filler slot.
  - Resume: respawn the movie encoder at `saved_position − rewind_seconds`
    (tunable, matches the existing 10-30s ruling above), wait for it to
    confirm real segments flowing via the same Starting→Live gate Phase 2
    already built for Go Live, **then** splice back. Viewers keep watching
    the pause card, uninterrupted, for however long the respawn takes — no
    spinner risk on either side.
  - Natural end: movie exits 0, filler slot restarts to thank-you content.
    Tolerable brief gap, coincides with the movie's own natural conclusion.
- **Why the filler slot can't be a fixed static file set:** a *live* HLS
  playlist is expected to keep incrementing its segment sequence over time;
  a source that stops producing new segments looks stalled to downstream
  hops for the same reason the frozen encoder did in spike 3/4. The filler
  content is static/near-static but the encode producing it must keep
  running continuously whenever it's the active or standby source.
- **Position tracking is plugin-computed, not read from ffmpeg.** Since the
  plugin controls the `-ss` start offset and ffmpeg paces in real time
  (`-re`, 1:1), position = last-start-offset + (now − last-start-wall-clock-
  time). No stderr/progress parsing needed. `SessionInfo.PlayState.
  PositionTicks` (spike 1) remains the signal for *detecting* a pause was
  triggered, not for computing the resume point — server clock stays
  authoritative per the original ruling.
- **Resource load:** at most 2 concurrent encoders at any moment (movie +
  warm-standby filler), never 3 perpetual ones. Filler encode is a single
  low-bitrate rung of a static/near-static loop — a rounding error next to
  the movie's own ABR ladder, already budgeted in SPEC.md §10.
- **Filler video sourcing:** hardcoded/bundled as `EmbeddedResource`s in the
  plugin (same pattern as `configPage.html`, sidesteps the documented
  zip-extraction file-serving bug in this repo's gotchas). Swappable via
  config explicitly deferred to v2, not v1.

### Ruling: host control surface

Jon's exact button set: **start stream, select movie, pause (toggles to
resume), rewind, fast forward, end stream.** Maps directly onto the state
machine above (channel-open → movie-playing → paused ↔ resumed →
natural-end). **Open, not decided this session:** whether rewind/fast-
forward apply only to adjusting the frozen pause-position before hitting
resume (cheap, matches the earlier "host can optionally rewind" ruling), or
also during live playback (would require live re-seek capability on the
movie encoder — more scope). Resolve before/during the build of those
specific controls.

### Ruling: fallback priority if the splice approach doesn't pan out

Jon: pursue the filler-splice mechanism as the next spike; **if it doesn't
work, ship v1 without pause** (reverting to the original spec §3 non-goal)
rather than block the rest of the broadcast engine on it. Pause becomes the
immediate next-priority feature to revisit afterward, not a gate on
shipping.

### Ruling: actively suppress native seek/pause/rewind UI as defense-in-depth

Jon: "if our plugin could hide the UI that also would work. be really
forceful about it, disable buttons for pause forward back etc." This is
**additive to** the filler-splice mechanism, not a replacement — it doesn't
solve host-controlled synchronized pause, it reduces confusing viewer-local
seek/pause fiddling (already made low-stakes by the resume server-clock
snap and the drift-correction system, but still worth suppressing for UX).

- **Real boundary named and accepted:** a server-side plugin cannot reach
  into Roku's, Xbox's, or Swiftfin's compiled native app UI directly —
  there is no injection surface for that.
- **The actual lever:** whatever capability flags Jellyfin's Live TV
  subsystem reports for our tuner source (chiefly DVR/timeshift support).
  All official Jellyfin apps read this same server-declared contract rather
  than each inventing their own heuristics — real un-DVR'd tuner channels
  already don't show seek/rewind controls in these same clients today. If
  we explicitly declare no timeshift capability (rather than relying on
  default behavior), this should suppress the affordance uniformly across
  the whole device matrix, not just web — more universal than any
  client-side UI hack would have been.
- **Escalated from passive hypothesis to an active goal:** find whichever
  flag governs this on the M3U tuner host type we registered under (Phase 1
  ruling), confirm it's explicitly set rather than assumed-default, and
  verify suppression on real clients in the same spike round as the
  discontinuity-splice check.
- **Still unverified, flagged rather than promised away:** whether a Roku
  remote's physical rewind/forward buttons bypass on-screen
  capability-driven UI and fire a seek command regardless of what the
  server declares.

### Ecosystem grounding (researched, not yet read in depth)

No off-the-shelf tool solves "communal pause of a simulated-live channel."
ErsatzTV / Tunarr (dizqueTV fork) / ffplayout solve the adjacent problem —
filler/slate splicing into a continuous live HLS output between scheduled
items — worth reading their source for splice mechanics specifically (the
one still-unverified piece: does Jellyfin's own remux hop tolerate our
discontinuity-tagged switch). They remain unusable as installed tools per
the existing distribution-model rejection in SPEC.md §1 (Docker/CLI setup).
Jellyfin's own SyncPlay solves a mechanically different problem (per-client
command orchestration against independent VOD sessions) — already rejected
in SPEC.md §1 for Roku support, and spike 2 tonight separately confirmed its
underlying command primitive doesn't fire on a Live TV session anyway.

### Open items carried forward (not decided, flagged for the next spike round)

1. Does Jellyfin's remux hop / real clients (web, then Roku) tolerate an
   `EXT-X-DISCONTINUITY`-spliced source switch cleanly? Unverified — this
   is the load-bearing risk for the whole mechanism, same class of
   uncertainty as the original Phase 1 tuner spike.
2. **Active goal, not just a hope:** declare our tuner source as not
   supporting timeshift/DVR seek, and verify that suppresses native
   pause/rewind/forward UI across the device matrix (web, Roku, Xbox,
   Swiftfin) — see the suppression ruling above. Even where UI can't be
   fully confirmed suppressed, mechanically low-risk regardless
   (local-only to that viewer, bounded to the ~40s retention window,
   overridden by resume's server-clock snap and the drift-correction
   system).
3. Rewind/fast-forward host-button scope (paused-position-only vs.
   also-live) — see ruling above.

---

## 2026-08-14 — Switcher architecture reframed: single-process input-switch + raw-TS-tuner spikes queued

**Context:** after the filler-splice mechanism (spike 5) and the pointer/
private-directory generalization (spike 6, see `BroadcastManager.Pause`/
`Resume`/`SpliceTick`/`WriteServedPlaylist`/`ResolveServedFilePath`) were
built and live-tested, Jon named the general shape himself: "then we have 4
encoders: welcome, pause, movie, end. The whole thing is that we're pointing
the live stream at any one of the encoders." This conversation confirmed
that framing, then Jon pushed further on *how* to build it, reopening
whether the current approach (N independent encoders, outputs stitched by a
hand-written HLS playlist) is the right shape at all.

### Ruling: 4-source pointer model confirmed correct

Jon's "welcome, movie, pause, goodbye, plugin points the live stream at
whichever one is needed" is the right architecture. Every bug hit while
building spike 5/6 was a *lifecycle transition* race (cold-start gaps,
kill-timing races, two-writers-one-file), not a flaw in the pointer/splice
mechanism itself — which is already generic and source-count-agnostic
(`SpliceTick`/`WriteServedPlaylist` read from a generic `_activeSourceDirectory`/
`_activeSourcePrefix`, `ResolveServedFilePath` resolves any prefix to its
directory). Extending it from 2 sources to 4 is additive, not a redesign of
that core.

### Ruling: try both a single-process/multi-input switcher AND a raw
MPEG-TS tuner feed as spikes — not an either/or choice

Jon: "I feel like single process with inputs makes sense to try as does raw
mpeg tuner. No reason not to try both."

- **Single-process/multi-input switcher:** one continuous ffmpeg process
  holds multiple `-i` inputs open simultaneously (welcome loop, pause loop,
  goodbye loop, plus the movie), with a `streamselect`-style filter choosing
  which one feeds the output, controlled live via ffmpeg's `zmq`/`sendcmd`
  filter commands. This is the standard broadcast-switcher pattern (switch
  upstream of one continuous encode, not stitch together N encoders'
  outputs downstream) and would eliminate discontinuity-marker/playlist-
  stitching complexity for the three static sources entirely — one
  ever-incrementing segment sequence, zero cold starts on switch between
  those three. **Does not solve movie seek/rewind** (see below) — ffmpeg's
  `zmq`/`sendcmd` control plane only reaches filter-graph parameters on
  already-open inputs, not a live re-seek of an open input; `-ss` is a
  demuxer-open-time parameter only. This is a hard ffmpeg constraint, not a
  design choice.
- **Raw MPEG-TS tuner feed instead of HLS:** point the M3U tuner entry at a
  raw continuous MPEG-TS stream URL rather than an HLS playlist. Real
  hardware tuners (HDHomeRun, what Jellyfin's Live TV tuner subsystem was
  originally built around) serve exactly this, and Jellyfin's tuner path
  already inserts its own remux hop regardless of source type (confirmed
  earlier this session) — so it likely doesn't care whether our URL is HLS
  or raw TS. If confirmed, this eliminates the entire hand-written-HLS-
  playlist risk surface (segment-window bookkeeping, discontinuity
  sequencing) since HLS would become something only Jellyfin manages,
  downstream of us. **Unverified — flagged as a spike, not a confirmed
  fact.**

### Ruling: restart-based seek for the movie source stays as-is; a
feeder-pipe architecture is available but explicitly deferred

Jon proposed mapping host pause/rewind buttons directly to an ffmpeg
command. Corrected in-conversation: ffmpeg has no live-reseek primitive for
an open input, so real seek (pause/rewind/resume) requires restarting the
movie encoder at a new `-ss` offset regardless of which switcher
architecture is chosen — this was true before this conversation and remains
true. Named explicitly: **the restart-for-seek approach itself was never
the actual bug** — every failure to date was a race in teardown/warmup
timing around a restart, not restart latency being visible to viewers; the
already-built warm-switch discipline (never cut to a source before it's
rolling, never tear down the old one before the new one's ready) already
hides that latency.

- **Available if restart-for-seek turns out to still be a problem after the
  race bugs are fixed:** a feeder-pipe architecture — a separate, small,
  freely-restartable process decodes/seeks the movie and writes raw frames
  into a pipe that the single continuous switcher reads as one more input.
  Only the feeder restarts on a seek; the switcher itself never stops. This
  is the true baseband-switch pattern from real broadcast (decode once,
  switch on raw frames, encode once downstream of the switch).
- **Ruling: not in scope now.** Build only if restart-for-seek is proven to
  actually cause a visible problem once the current race bugs are fixed —
  not worth building preemptively.

### Ruling: native-pause-button stretch goal — one half already dead, one
half genuinely untested (not the same thing)

Corrected a conflation made mid-conversation: `SendPlaystateCommand` (host
force-pushing a pause command to a client session) was tested against
Firefox only and confirmed not honored (200 OK, no effect) — that mechanism
is dead, but only confirmed dead on the one client it was tested against.
**Native pause detection via session polling** (`PlayState.IsPaused`/
`PositionTicks` — the mechanism the stretch goal actually needs: detect a
client already paused itself, then react server-side) was only ever tested
against web. Jon: "we never tested it because I never pressed pause on
roku" — Roku's Live TV tuner session pause-reporting behavior is genuinely
unverified, not settled dead or alive, and Roku is the client this project
already treats as the pickiest tier-1 test.

**Ruling: add Roku native-pause-detection testing to the next spike round.**
Jon: "Sure."

### Spike queue (final, this conversation — Jon: "I think we have enough")

1. Single-process/multi-input switcher (`streamselect` via `zmq`) for
   welcome/pause/goodbye/movie-passthrough switching.
2. Raw MPEG-TS tuner feed instead of HLS — verify Jellyfin's M3U tuner
   accepts a raw continuous-stream URL and remuxes it the same way it does
   HLS.
3. Roku native-pause-detection via session polling (`PlayState.IsPaused`),
   specifically on Roku — not yet exercised on that client at all.

### Known gaps in the current implementation, surfaced this conversation
(not yet fixed, not blocking the spikes above)

Read directly from `BroadcastManager.cs`/`FfmpegCommandBuilder.cs`/
`MovieNightController.cs` during this conversation, not from memory:

- `WriteServedPlaylist` hardcodes `EXT-X-MEDIA-SEQUENCE:0` on every write
  instead of tracking the real rolling sequence number, and never emits
  `EXT-X-DISCONTINUITY-SEQUENCE` at all — both are real RFC 8216
  requirements for a live playlist and both are unverified against a picky
  client (Roku). Not yet caused a visible failure, but not proven safe
  either. Relevant only if the hand-written-HLS-playlist path survives the
  raw-TS-tuner spike above; moot if that spike succeeds.
- Only one non-movie encoder exists in code today (the filler/pause-card
  slot), and it's a synthetic `color=c=maroon` test pattern — no real
  welcome or goodbye content or bundled asset exists yet.
- The state machine has no concept of "stream started, no movie selected
  yet" — `GoLiveAsync` requires an `itemId` up front, conflating "start
  stream" and "select movie" into one action, not yet split into the two
  separate host buttons Jon's control-surface ruling calls for.
- No goodbye phase exists — natural end goes straight to `Ended` with no
  switch to thank-you content.
- Rewind/fast-forward buttons are unbuilt (scope still open per the
  original ruling above).

### Ecosystem grounding note

ffplayout (open source, Rust — continuous live HLS output with a
scheduled/looping source and a switchable live-ingest override) remains the
closest open-source analog to the single-process-switcher pattern and is
still unread in depth (flagged in the prior entry, still true). Reading its
splice/switch internals before finalizing the spec requires an agent with
web/repo-fetch access, not available in this conversation.

## 2026-08-14 (evening) — switcher architecture validated; resume + pause-card rulings

- **Switcher spike PASSED end-to-end** (v0.3.24.0): one continuous ffmpeg, raw MPEG-TS
  down the tuner, live input switching via ffmpeg's stdin command protocol
  (`c` + `streamselect -1 map N`) — blue→maroon→green switched live through
  Jellyfin's remux into a real client with zero seams. zmq is unavailable
  (jellyfin-ffmpeg built without libzmq) — stdin is the control channel.
- **RULING (Jon): resume must return to the movie's logged timestamp** (or a
  little before it). Teardown-and-rewarm of the movie source is acceptable if
  required. Implied design: the movie enters the switcher via a restartable
  FEEDER process (switcher itself never restarts, so viewers never see a seam);
  pause logs elapsed time, resume restarts the feeder at pausedAt−rewind and
  switches back when warm.
- **RULING (Jon): pause audio is quiet or gentle music.** Static-card-vs-short-loop
  is left open as an asset decision — architecturally identical (pause input is a
  `-stream_loop -1` file with its own audio, audio switched via `astreamselect`
  together with video). Placeholder acceptable for the next spike.
- Per-viewer-process question likely moot: Jellyfin's tuner shares one upstream
  connection across clients (`AllowStreamSharing`, "consumer count is now 2"
  observed live) — one encoder, Jellyfin fans out. Verify pid count stays 1 with
  a second real client when convenient.
