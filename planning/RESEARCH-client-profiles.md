# Research — why clients direct-play (or don't), and which ones will break

Written 2026-08-19, after the T0 gate passed and the client matrix split 3/2.
Purpose: stop discovering client behaviour one tune at a time. Jellyfin's
negotiation is deterministic and the client profiles are open source, so "which
clients will have problems" is a lookup, not an experiment.

Each claim is marked **FACT** (measured here, or read in source) or
**HYPOTHESIS** (reasoned, not yet tested).

---

## 1. How Jellyfin decides DirectPlay vs the hop

**FACT.** On `POST /Items/{id}/PlaybackInfo` the client submits a `DeviceProfile`.
The server's `StreamBuilder` matches our `MediaSourceInfo` against that profile's
`DirectPlayProfiles` — each entry being a **Container + VideoCodec + AudioCodec**
triple the device claims it can play untouched. The highest-priority mode wins,
and DirectPlay is always preferred. If nothing matches, the source is rejected
and the server falls back to its own transcode/remux — the per-client ffmpeg
that failed the 2026-08-18 soak.

`SupportsTranscoding = false` is **not** consulted first. The profile match
happens first; our flag only says what to do *after* direct play is refused.
That is why the field the design named as load-bearing (§4(G)) is not the field
that decided the gate.

## 2. The two failure reasons are NOT the same failure

This is the correction that matters most, and I had it wrong during testing.

| `TranscodeReasons` | what actually happened | fixable server-side? |
|---|---|---|
| `ContainerNotSupported` | **Negotiation refused.** No `DirectPlayProfiles` entry matched our container. The client never receives our URL and never fetches anything. | Only by declaring a container the client's profile lists |
| `DirectPlayError` | **Negotiation SUCCEEDED, playback then threw.** The server granted direct play, the client tried, its player failed, and it re-requested through the transcode path. | Only by changing the CONTENT shape |

Our recorder caught the `DirectPlayError` transition live on Firefox:

```
15:28:42  Jellyfin Web | DirectPlay | ['DirectPlayError']    <- granted
15:28:46  Jellyfin Web | Transcode  | ['DirectPlayError']    <- client fell back
```

So **Firefox's profile does advertise HLS, and Jellyfin did hand it our URL.**
Firefox's *player* could not play what it received. The earlier framing —
"Firefox's profile refuses our container" — was wrong.

## 3. jellyfin-web: what actually gates the `hls` container

**FACT**, from `jellyfin-web/src/scripts/browserDeviceProfile.js`:

```js
canPlayHls() =
    !!(media.canPlayType('application/x-mpegURL')
    || media.canPlayType('application/vnd.apple.mpegURL'))
    || window.MediaSource != null          // <- MSE alone is enough
```

`hls` is added to `DirectPlayProfiles` when `canPlayHls() && options.enableHls
!== false`, as two entries gated on codec arrays:

- `Container: 'hls'` (TS)   — needs `hlsInTsVideoCodecs.length`
- `Container: 'hls'` (fmp4) — needs `hlsInFmp4VideoCodecs.length && enableFmp4Hls`

And critically:

```js
if (canPlayH264(videoTestElement)) {
    mp4VideoCodecs.push('h264');
    hlsInTsVideoCodecs.push('h264');      // both arrays,
    hlsInFmp4VideoCodecs.push('h264');    // unconditionally
}
```

**FACT: there is no Firefox-specific branch here.** h264 lands in both arrays for
any browser that can play h264. Only HEVC has platform gating (tizen/web0s/vidaa
for TS; edgeChromium/safari/… for fmp4). Firefox has `window.MediaSource`, so
`canPlayHls()` is true, so Firefox advertises `hls` — exactly consistent with it
getting `DirectPlayError` rather than `ContainerNotSupported`.

**HYPOTHESIS (the actionable one):** Firefox fails at *playback* because our
segments are **MPEG-TS** (`-hls_segment_type mpegts`). Firefox has no native HLS
and no native TS demuxing; it depends on hls.js remuxing TS in JavaScript through
MSE, and that is where this breaks. **Test: build the ladder with
`-hls_segment_type fmp4`.** Jellyfin's own docs note Firefox Desktop supports
H.264 **fmp4**, and jellyfin-web keeps a separate fmp4 HLS profile entry for
precisely this reason. It is a one-flag change to `T0Gate.BuildLadderAsync` and
is the highest-value next experiment for client coverage.

**Caveat before acting on it:** fmp4 changes segment shape for *every* client,
including the three that currently work. Any fmp4 test must re-verify Roku and
Xbox, not just Firefox.

## 4. Android: a different problem, and a known upstream one

**FACT (measured):** the Pixel's Jellyfin app returned `ContainerNotSupported`
under `container = "ts"`, `null`, **and** `"hls"`. It never fetched our ladder
once. That is a negotiation refusal, so no change of segment format can fix it —
only a container its profile actually lists.

**FACT:** `jellyfin-android` (phone) and `jellyfin-androidtv` are different apps
with different profiles. The AndroidTV default profile *does* list `hls` among
its direct-play containers (`asf,hls,m4v,mkv,mov,mp4,ogm,ogv,ts,vob,webm,wmv,
xvid`). The phone app builds its profile in `DeviceProfileBuilder.kt` and is not
the same list.

**FACT (relevant upstream bug):** jellyfin-androidtv #5237 — *"LiveTV (MPEG-TS)
playback loops/resets every ~30s (ExoPlayer internal discontinuity)"*. Live TV +
MPEG-TS + ExoPlayer is a known-bad combination in the Jellyfin ecosystem. This is
very likely the same root cause as roadmap §1c's Android black screen, which we
had recorded as an open mystery. Another argument for fmp4.

**HYPOTHESIS:** the phone app omits `hls` from its DirectPlayProfiles. Not
confirmed — the app does not register `Capabilities.DeviceProfile` on the session
(Roku does), so it cannot be read from `GET /Sessions`. To confirm, capture the
`DeviceProfile` body of its `PlaybackInfo` request, or read
`DeviceProfileBuilder.kt` for the installed app version.

## 5. A known upstream issue describing our exact situation

**FACT:** jellyfin-web issue **#4479** — *"jellyfin is doing unnecessary HLS
transcoding"*. Reported: a `.strm` pointing at an m3u8 is detected as container
`hls` server-side, the web client doesn't advertise `hls`, so it transcodes even
though hls.js could play it directly. Labelled **confirmed**, then **stale**;
PR #4761 closed without landing.

Two consequences. First, we are not doing anything exotic — serving an HLS URL as
a media source is a recognised, imperfectly-supported path. Second, client
support here will improve or regress with upstream client releases, outside our
control. **Any client matrix we record must be stamped with the client versions
it was measured on.**

## 6. Reading list for predicting a new client

For any device that shows up, in order:

1. `GET /Sessions` → `Capabilities.DeviceProfile.DirectPlayProfiles`. If the
   client registers one (Roku does), the answer is right there: does it list a
   container we serve?
2. If it registers nothing (Android, Web), read that client's profile builder in
   its own repo — `browserDeviceProfile.js` for web, `DeviceProfileBuilder.kt`
   for jellyfin-android.
3. Check that client repo's issues for `hls` + `live tv` before assuming our bug.

**Predictions (HYPOTHESIS, all untested):**

- **Apple TV / iOS / Safari** — native HLS; most likely to direct-play. Safari is
  the only browser with real native HLS.
- **Chromecast** — native HLS; likely fine.
- **Kodi (JellyCon)** — plays through its own player; likely tolerant.
- **LG webOS / Samsung Tizen** — their profiles explicitly list HLS-in-TS (they
  are the platforms granted HEVC-in-TS), so probably fine on the current mpegts
  ladder.
- **Any ExoPlayer-based Android client on MPEG-TS** — the risk group, per #5237.

## 7. What this changes about the design

- **`DESIGN-abr-ladder.md` §4(G) is wrong about which field is load-bearing.**
  `Container` decides the gate, not `SupportsTranscoding`. Correction already
  recorded in DECISIONS.md 2026-08-19.
- **Segment type is a client-compatibility decision, not just an encoder detail.**
  The design specifies `-hls_segment_type mpegts` in §4(D) without discussing
  client support. fmp4 may widen coverage; it needs testing, and it interacts
  with the `-c copy` rung-0 mezzanine (whose segments must match).
- **Per-client `MediaSourceInfo` may be less necessary than feared.** If one
  segment format satisfies every client's profile, M4 stays simple. The
  per-client mechanism is only needed if the fleet genuinely disagrees.
- **The per-client URL problem is unaffected and still real** — that one is about
  reachability (LAN vs tailscale vs https origin), not negotiation.

---

## 8. The device sweep (2026-08-19)

Jon's list: Apple TV, iPhone, PlayStation, Samsung, LG, plus what we own.

### The organising insight

**The fleet does not split by device. It splits by PLAYER ENGINE**, and there are
only four that matter:

| engine | HLS handling | our ladder |
|---|---|---|
| **Native HLS** (Safari, AVPlayer, Roku) | first-class, TS segments fine | works |
| **Chromium + hls.js** (Chrome, Edge/WebView2, Tizen, webOS) | hls.js remuxes TS, works | works |
| **Firefox + hls.js** | same library, TS path fails in practice | **FAILS** (DirectPlayError) |
| **ExoPlayer** (Android phone/TV) | known-bad with Live TV + MPEG-TS | **FAILS** |

Every prediction below follows from which engine the client uses. This also means
**one fix can move a whole engine class at once** - the fmp4 experiment in §3
targets the Firefox row, and possibly the ExoPlayer row too.

### Per-device

| device | official client | engine | prediction | confidence |
|---|---|---|---|---|
| Roku Ultra | jellyfin-roku | native | **direct-play** | **MEASURED** |
| Xbox Series X | jellyfin-uwp | Edge WebView2 (Chromium) | **direct-play** | **MEASURED** |
| Desktop Chrome | jellyfin-web | Chromium + hls.js | **direct-play** | **MEASURED** |
| Desktop Firefox | jellyfin-web | Firefox + hls.js | hop (`DirectPlayError`) | **MEASURED** |
| Pixel / Android phone | jellyfin-android | ExoPlayer | hop (`ContainerNotSupported`) | **MEASURED** |
| **Apple TV** | **Swiftfin (tvOS)** | AVKit *or* VLCKit | **direct-play** | HIGH |
| **iPhone / iPad** | **Swiftfin (iOS)** | AVKit *or* VLCKit | **direct-play** | HIGH |
| **Samsung TV** | **jellyfin-tizen** | jellyfin-web on Tizen | **direct-play** | HIGH |
| **LG TV** | **jellyfin-webos** | jellyfin-web on webOS | **direct-play** | HIGH |
| **PlayStation 5** | **NONE EXISTS** | - | **no path** | HIGH |
| PlayStation 4 | Switchfin (3rd party) | libmpv-ish | unknown | LOW |
| Android TV / Fire TV | jellyfin-androidtv | ExoPlayer | hop, per #5237 | MEDIUM |
| Chromecast | jellyfin-chromecast | Chromium receiver | direct-play | MEDIUM |
| Kodi | JellyCon / jellyfin-kodi | Kodi's own player | direct-play | MEDIUM |

### The reasoning behind the HIGH-confidence ones

**Apple TV and iPhone — the best-case clients, both via Swiftfin (official).**
Swiftfin ships **two** players and lets the user pick:
- *Native* = AVKit/AVPlayer. Apple platforms have **real native HLS**; HLS is
  Apple's own format. This is the one engine our ladder was literally designed
  for.
- *Swiftfin* = VLCKit. Its DirectPlay profile **defines only audio codecs** -
  "basically any container/video codec should pass through for DirectPlay".
  That is the most permissive profile in the ecosystem.

Either way the negotiation should succeed. Two gotchas on record, neither fatal
for us: AVPlayer has no subtitle-track selection in Swiftfin due to HLS
incompatibilities (we ship no subtitles), and **VLCKit does not support TLS 1.3**
- relevant only if these clients come in through a reverse proxy, which for
remote viewers over tailscale they may well do.

**Samsung and LG — already covered by the Xbox result.** `jellyfin-tizen` and
`jellyfin-webos` are wrappers around **jellyfin-web**. This is not inference:
`browserDeviceProfile.js` contains explicit `browser.tizen`, `browser.web0s` and
`browser.vidaa` branches - the same file we read in §3. They run the same
`canPlayHls()` logic as Chrome, and they are among the few platforms granted
**HEVC-in-TS** direct play, i.e. their TS handling is *better* than desktop
browsers', not worse. Xbox direct-playing (Chromium/WebView2, same codebase) is
direct evidence for this whole row.

**PlayStation — there is no client, and that is the finding.** No official
Jellyfin app exists for PS5 as of 2026; the platform is not on Jellyfin's
supported list. Third-party **Switchfin** covers PC, **PS4**, PSVita and Switch,
but users report it does not work on PS5. Homebrew-browser routes are unreliable.
**A PS5 is not a movie-night client** - the answer is "use the TV's own app, or a
Roku". Worth knowing before a guest asks, not after.

### 8b. Amazon and Chromecast — and why the ExoPlayer row is now the main risk

**FACT:** Fire TV Stick (all models), Chromecast **with Google TV**, and Nvidia
Shield all run the **same `jellyfin-androidtv` app on ExoPlayer**. Fire OS is an
Android fork; per the client guides, "Fire OS doesn't change what the app does
once it's running - you get the same source, library browsing, playback and
settings as on Android TV or Chromecast with Google TV."

That collapses a lot of the device list into one row, and it is the row that
already failed:

| device | app | engine |
|---|---|---|
| Fire TV Stick / 4K / 4K Max | jellyfin-androidtv | ExoPlayer |
| Chromecast **with Google TV** | jellyfin-androidtv | ExoPlayer |
| Nvidia Shield | jellyfin-androidtv | ExoPlayer |
| Android phone / tablet | jellyfin-android | ExoPlayer |
| Android TV boxes generally | jellyfin-androidtv | ExoPlayer |

**"Chromecast" is two different things and they land in different rows.**
- **Casting TO a Chromecast** (the Cast button, old dongles) uses
  `jellyfin-chromecast`, a **Chromium CAF receiver** - same engine class as
  Chrome and the Xbox, which both direct-play. Likely fine.
- **Chromecast with Google TV** (the dongle with a remote) is an Android TV
  device running the ExoPlayer app. Same risk as a Fire Stick.

Anyone saying "I have a Chromecast" has to be asked which one.

### 8c. Revised priority

Before this, ExoPlayer looked like a one-phone edge case. It is not. **Fire
Sticks and Chromecast-with-Google-TV are among the most common streaming devices
people own**, and they are the two most likely things a guest brings. Combined
with the Android phone already measured failing and jellyfin-androidtv #5237
(LiveTV + MPEG-TS resets every ~30s under ExoPlayer), the ExoPlayer row is the
single largest threat to "a variety of devices" working.

Consequences:

1. **The fmp4 experiment (§3) is now the top priority, not a Firefox curiosity.**
   It is the one change that could move BOTH failing engine classes at once. If
   fmp4 fixes ExoPlayer, the fleet goes from "3 of 5 devices we own" to "nearly
   everything except a PS5".
2. **If fmp4 does NOT fix ExoPlayer**, then per-client `MediaSourceInfo` is
   worth building after all - because the affected population is large enough to
   justify it, which was not clear when it was just one phone.
3. **A Fire Stick should be borrowed and tested** before any real movie night.
   It is the highest-value untested device on the list, ahead of Apple TV -
   Apple is predicted to work and would be a pleasant confirmation; Fire TV is
   predicted to fail and would be an expensive surprise.

### 8d. CORRECTION — the ExoPlayer row splits in two (read from source, 2026-08-19)

§8b was wrong to treat "ExoPlayer" as one row. The two Android apps have
opposite profiles, and both were read directly from source rather than inferred.

**`jellyfin-android` (PHONE) — cannot direct-play ANY manifest. FACT.**
`DeviceProfileBuilder.kt` builds `DirectPlayProfiles` from:

```kotlin
SUPPORTED_CONTAINER_FORMATS =
    "mp4", "fmp4", "webm", "mkv", "mp3", "ogg", "wav", "mpegts", "flv", "aac", "flac", "3gp"
```

**No `hls`. No `dash`.** Those appear only in its *TranscodingProfiles*. Every
entry is a progressive single-file container. So the phone app cannot direct-play
an HLS ladder, a DASH manifest, or any adaptive stream - not because of our
fields, and not fixable by changing segment type. This fully explains
`ContainerNotSupported` under `ts`, `null` AND `hls`. **Architectural limit of
that client, not a bug in our source.**

(Note `"mpegts"` IS listed, and we tested `"ts"` - different strings. Declaring
`mpegts` might pass negotiation, but the URL is still a playlist, so it would
likely fail at playback like Firefox. Untested; low expected value.)

**`jellyfin-androidtv` (FIRE STICK, CHROMECAST W/ GOOGLE TV, SHIELD) — DOES list
hls. FACT.** From `util/profile/deviceProfile.kt`:

```kotlin
directPlayProfile {
    container(
        Codec.Container.ASF, Codec.Container.HLS, Codec.Container.M4V,
        Codec.Container.MKV, Codec.Container.MOV, Codec.Container.MP4,
        Codec.Container.OGM, Codec.Container.OGV, Codec.Container.TS,
        Codec.Container.VOB, Codec.Container.WEBM, Codec.Container.WMV,
        Codec.Container.XVID,
    )
```

`HLS` **and** `TS` are both direct-play containers there. It also declares HLS
transcoding profiles in **both** TS (`Codec.Container.TS`) and fMP4
(`Codec.Container.MP4`) flavours, so ExoPlayer-on-TV handles both segment types.

**Revised prediction: Fire Stick and Chromecast-with-Google-TV should
DIRECT-PLAY our ladder.** They were the scariest devices on the list and are now
predicted fine. The residual risk for them is androidtv #5237 (LiveTV + MPEG-TS
resets every ~30s), which is a *playback* defect rather than a negotiation one -
and its stated trigger is MPEG-TS, which is another argument for fmp4.

So the genuinely-unfixable set is **one client**: the Jellyfin Android *phone*
app. Everything else is predicted to work or already measured working.

### 8e. Captions (V1 requirement) — the constraint is already visible

From the same androidtv profile:

```kotlin
// Jellyfin server only supports WebVTT subtitles in HLS, other text subtitles
// will be converted to WebVTT which we do not want so only allow delivery over
// HLS for WebVTT subtitles
subtitleProfile(Codec.Subtitle.VTT,    embedded = true, hls = true, external = true)
subtitleProfile(Codec.Subtitle.WEBVTT, embedded = true, hls = true, external = true)
```

**FACT: WebVTT-over-HLS is the supported caption path** across this ecosystem.
For our ladder that means a `#EXT-X-MEDIA:TYPE=SUBTITLES` rendition with WebVTT
segments alongside the video rungs - standard HLS, and ffmpeg can emit it.

**Known collision to design around (FACT, from §8):** Swiftfin's *Native*
(AVPlayer) player has **no subtitle-track selection** due to HLS
incompatibilities. So on Apple TV / iPhone, captions may require the user to pick
the **VLCKit** player rather than Native. That is a documentation/UX answer, not
a server one - but it must be decided before captions are called done.

### What to actually do with this

1. **Do not build per-client `MediaSourceInfo` yet.** The predicted fleet is
   mostly direct-play. The two known failures share a plausible single cause
   (MPEG-TS segments), and the fmp4 experiment is one flag.
2. **Test fmp4 next** (§3). If Firefox flips AND Roku/Xbox/Chrome survive, the
   matrix goes from 3/5 to 4/5 with no new code, and ExoPlayer may come along.
3. **Borrow an Apple device before assuming.** HIGH confidence is not MEASURED,
   and every confident prediction this project has made about client behaviour
   has been wrong at least once.
4. **Stamp client versions** on any matrix (per §5) - measured here on Roku
   DVP-15.3, Xbox WebView2 Chrome/150, desktop Chrome/151.

## Sources

- jellyfin-web `src/scripts/browserDeviceProfile.js` (master)
- jellyfin-web issue #4479 — unnecessary HLS transcoding (confirmed, stale)
- jellyfin-androidtv issue #5237 — LiveTV MPEG-TS resets every ~30s under ExoPlayer
- jellyfin-android issue #1311 — DeviceProfileBuilder.kt codec reporting
- DeepWiki: jellyfin-web Device Profile System; jellyfin DLNA and Stream Selection
- jellyfin.org/docs/general/clients/codec-support
