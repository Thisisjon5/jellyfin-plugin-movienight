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

**Phase 1 — in progress (2026-08-13).** Code shipped (v0.2.2.0): `TunerRegistrar`
(`IHostedService`) registers an `"m3u"` tuner + `"xmltv"` listings provider via
`ITunerHostManager.SaveTunerHost`/`IListingsManager.SaveListingProvider` (in-process,
no REST needed — confirmed against actual 10.11.11 source). `MovieNightController`
serves `playlist.m3u`/`stream/master.m3u8`/`stream/{segment}.ts`/`epg.xml`, loopback-only.
Two real bugs found and fixed along the way (see gotchas below); channel-tunes-in-Web
gate not yet confirmed — Jon installing v0.2.2.0 now.

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

## Standing rules

- Follow the phase gates in `planning/SPEC.md` §12 in order — don't skip ahead
  (e.g. don't build the config UI before the Phase 1 tuner spike proves the
  architecture). If a gate fails, stop and revisit before continuing.
- No decisions on hosting, dev-server placement, or scope beyond what's already
  ruled in the spec without asking first — this repo is brand new and nothing here
  is a standing autonomy grant yet.
