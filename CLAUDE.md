# Movie Night — project guardrails

Full spec + phased build plan: `planning/SPEC.md` (canonical — don't re-derive from scratch, read it).

## Status

Phase 0 (scaffold) complete: template renamed, builds clean on `net9.0` against
`Jellyfin.Controller`/`Jellyfin.Model` 10.11.6. Install target is the NAS's real
Jellyfin container ([[jellyfin]], `nas_container_inspect` confirmed image version
`10.11.6ubu2404-ls24`, host port 8096) — not a throwaway dev instance.

Repo is public on GitHub: https://github.com/Thisisjon5/jellyfin-plugin-movienight
(Jon's ruling 2026-08-13, GitHub over Forgejo). v0.1.0.0 tagged, zip released,
manifest.json committed to master and reachable at
https://raw.githubusercontent.com/Thisisjon5/jellyfin-plugin-movienight/master/manifest.json.
Still needs: Jon adds that URL as a Plugin Repository in the NAS Jellyfin dashboard,
installs, restarts — that's the one remaining GUI step only he can do — then verify
the config page renders (Phase 0 gate).

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

## Standing rules

- Follow the phase gates in `planning/SPEC.md` §12 in order — don't skip ahead
  (e.g. don't build the config UI before the Phase 1 tuner spike proves the
  architecture). If a gate fails, stop and revisit before continuing.
- No decisions on hosting, dev-server placement, or scope beyond what's already
  ruled in the spec without asking first — this repo is brand new and nothing here
  is a standing autonomy grant yet.
