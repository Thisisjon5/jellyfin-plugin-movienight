# Movie Night — project guardrails

Full spec + phased build plan: `planning/SPEC.md` (canonical — don't re-derive from scratch, read it).

## Status

Phase 0 (scaffold) complete: template renamed, builds clean on `net8.0` against
`Jellyfin.Controller`/`Jellyfin.Model` 10.10.7. Not yet installed on any dev server —
Phase 0's full gate ("plugin installs from a manifest URL via dashboard") still needs
a running Jellyfin 10.10 dev server, which is a separate decision (Docker on this
laptop vs. a throwaway NAS container — ask before spinning one up; NAS is
RAM-constrained prod, no casual containers per the homelab policy).

## Build

```
dotnet build Jellyfin.Plugin.MovieNight.sln
```

## Known gotchas

- The upstream `jellyfin/jellyfin-plugin-template` (main branch) defaults to `net9.0`
  and pins `Jellyfin.Controller`/`Jellyfin.Model` to `10.9.11` — both **wrong** for
  this project. Spec pins `net8.0` / targetAbi `10.10.0.0`; csproj packages are
  pinned to `10.10.7` (latest patch in that ABI line) to match.
- Template ships GitHub Actions that call `jellyfin/jellyfin-meta-plugins` reusable
  workflows requiring the jellyfin org's bots/secrets (`command-dispatch`,
  `command-rebase`, `sync-labels`, `publish`, `changelog`, `scan-codeql`) — deleted,
  they don't work outside the jellyfin org. Kept `build.yaml`/`test.yaml` (generic,
  no org-specific secrets) as the CI skeleton.
- Git hosting (GitHub vs. Jon's Forgejo) is an open decision, deferred to Phase 5
  packaging per spec §8 — don't assume one when wiring CI/release automation.

## Standing rules

- Follow the phase gates in `planning/SPEC.md` §12 in order — don't skip ahead
  (e.g. don't build the config UI before the Phase 1 tuner spike proves the
  architecture). If a gate fails, stop and revisit before continuing.
- No decisions on hosting, dev-server placement, or scope beyond what's already
  ruled in the spec without asking first — this repo is brand new and nothing here
  is a standing autonomy grant yet.
