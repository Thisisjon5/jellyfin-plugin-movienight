# Movie Night

Jellyfin plugin that broadcasts a single movie from your library to every
Jellyfin client as a live, owner-controlled Live TV channel. One server-side
encode; viewers join in progress, live-TV style. No SyncPlay, no sidecar
containers — install and run entirely from the Jellyfin dashboard.

Full spec, architecture, and phased build plan: [`planning/SPEC.md`](planning/SPEC.md).

**Status:** Phase 0 — scaffold only. Not yet installable.

## Build

```
dotnet build Jellyfin.Plugin.MovieNight.sln
```

Targets Jellyfin 10.10.x (`net8.0`, targetAbi `10.10.0.0`).
