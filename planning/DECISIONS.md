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
