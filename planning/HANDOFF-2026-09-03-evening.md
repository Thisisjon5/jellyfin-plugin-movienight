# Handoff — 2026-09-03 evening (step 1 is LIVE on the NAS; pause proven with one client)

Read `ROADMAP-2026-09-03.md` (plan + rulings) first, then this. Supersedes the
morning `HANDOFF-2026-09-03.md`, whose job (release, install, acceptance test)
this session did. Narrative: `ITERATION-LOG.md` arc 11. Rulings: `DECISIONS.md`
(three entries dated 2026-09-03). Repo gotchas: `CLAUDE.md`.

## Where we are

**Installed on the NAS: v0.4.5.0.** Channel **900 "Movie Night"**, tuner hosts
`m3u` + `movienight` registered, one mezzanine prepared (Afro Samurai, 4.94 GB,
no captions burned in). Everything below "Measured" happened on the real server.

Six releases today; every bug was invisible to unit tests:

| version | fixed |
|---|---|
| 0.4.1.0 | DI cycle that stopped Jellyfin starting at all (`DependencyGraphTests` guards it) |
| 0.4.2.0 | tuner registration timeout 60 s → 10 min (NAS needs ~4 min) |
| 0.4.3.0 | config page blank after hard reload (`pageshow` race) |
| 0.4.4.0 | v3 zero-viewer kill firing on the encoder's own disconnect; slate double-spawn leak |
| 0.4.5.0 | **the pause seam**: slate now synthesised to the movie's exact geometry |

## Measured

- **Xbox + Roku both DirectPlay the live ladder** off the real pipeline (own IPs
  in `Encoder.Fetches.ByRemote`, one server encoder, `UsingMezzanine: true`).
  ~10 s offset between them = buffer depth.
- **Pause/resume survives, proven with an ffmpeg-8 HLS client on the laptop**
  (200 s, pause at 179.0 s, resume from 169.0 s driven over the API):
  `EncoderRestarts` stayed 0, the client never disconnected, zero errors or
  discontinuities in its log, capture is one continuous stream, frames show
  movie → slate → movie. Log line at Go Live to look for:
  `slate will match source geometry "1080x720 @30000/1001 yuv420p sar 853:720"`.
- **NOT yet measured:** Roku and Xbox *staying attached* across the pause splice.
  Only the ffmpeg client has done it. This is the first thing to test with a
  human at the TV (Go Live → pause → both screens slate, `EncoderRestarts` 0).

## Acceptance test — status against the morning handoff's nine steps

1. Go Live from the page — ✅ (also `POST api/live/golive?itemId=`)
2. Channel appears — ✅
3. Roku + Chrome DirectPlay — ✅ Roku + Xbox measured; Chrome measured 08-19
4. Pause → slate on both within ~15 s — ⚠️ encoder side proven; Roku/Xbox unverified
5. Resume — ⚠️ same
6. Untune 2 min, retune → live edge — ❌ not attempted
7. Stop → channel gone, no orphan ffmpeg — ✅ (census clean after every stop)
8. Prepare with captions → captions visible — ❌ mezzanine was prepared with no track
9. Rungs = 2 — ❌ not attempted

## Do next, in order

1. Human at the TV: pause/resume on Roku + Xbox (step 4/5). Then 6, 8, 9.
2. Delete `/config/MovieNight_0.4.0.0.broken` inside the jellyfin container
   (parked recovery copy of the version that took the server down). Reversible
   until then; harmless.
3. Open questions for Jon, all in `DECISIONS.md`: cap the ladder at source
   resolution (bandwidth argument); drop `+faststart` on the mezzanine (pure
   waste, ~⅓ of Prepare time); the slate library / per-title slate at Prepare.

## Known-open, not blocking

- The slate feeder is still started by two callers on pause; the 0.4.4.0 guard
  kills the loser (`lost the start race, killing it` in the log). Root cause of
  the double call is unfixed; benign.
- The mid-GOP cut at the pause splice gives a frame or two of decoder
  concealment. Cosmetic.
- Ladder output is non-square-pixel when the source is (1080x720 SAR 853:720
  here). HLS players handle SAR, but it is unusual for a streaming ladder; a
  fixed square canvas is option B in DECISIONS if it ever bites a client.
- Firefox takes the server hop (`SupportsTranscoding=true`), Android phone app
  unsupported — both ruled, unchanged.

## Environment / process notes

- **`git fetch` before reading any handoff.** This session started on a local
  tree 356 h stale and spent an hour rebuilding on files the remote had deleted.
- **The lab is `POST /MovieNight/api/debug/encode-probe`.** Reproduce a seam
  offline with `concat:/tmp/a.ts|/tmp/b.ts` (write the inputs to the container's
  `/tmp` — the probe wipes its own dir each run). Jon's ruling: verify there
  before shipping, never restart-and-hope.
- An ffmpeg on the laptop reading the authed ladder URL
  (`.../stream/hls/master.m3u8?api_key=<token>`) is a valid HLS client for
  encoder/seam proofs and fetches from the laptop's LAN IP. It is NOT a proxy for
  Roku/Xbox player behaviour.
- Jellyfin API from the laptop: `http://nas.tailb1a3d2.ts.net:8096` with
  `X-Emby-Token`; startup logs from the LAN at
  `http://192.168.68.118:8096/startup/logger` when the API is down.
- Install cycle: `POST /Packages/Installed/Movie%20Night?version=X`, then
  `POST /System/Restart`; confirm on disk and in `/Plugins` — the install call
  can time out (`000`) and still have succeeded, and a first restart sometimes
  only stages (`Status: Restart`) so a second is needed.
- Recovery if a version stops Jellyfin starting (no fallback folder exists — an
  install REPLACES the old one): `tailscale ssh Jon@nas "docker exec jellyfin mv
  '/config/data/plugins/Movie Night_X' '/config/MovieNight_X.broken' && docker
  restart jellyfin"`. The harness blocks mutating SSH from Claude; Jon runs it.
