# Contributing

## Build

```bash
./scripts/package.sh
```

Writes `artifacts/ShadowLibrary_<version>.zip`. Unzip it into the `plugins` folder of a
Jellyfin config directory and restart the server.

The build runs the same analyzers as the official Jellyfin plugins, with warnings treated as
errors, so a build that succeeds locally is a build that passes CI. The exceptions to
`jellyfin.ruleset` are the four rules that conflict with serialized DTOs and with grouping
the small types of one exchange in a single file. Each one carries the reason next to it.

## Versions

`build.yaml` is the single source of truth. `scripts/package.sh` reads the version from it,
and so does the release pipeline through JPRM, which injects it into the assembly at build
time. Nothing in the `.csproj` carries a version.

## Releasing

1. Bump `version` in `build.yaml` and add the entry to `CHANGELOG.md`.
2. Commit and push to `main`.
3. Publish a GitHub release whose tag is the version, for instance `v1.0.0.0`.

The publish workflow builds the plugin, attaches the zip and its checksums to the release,
adds the version to `manifest.json` and pushes that back to `main`. Servers that added the
repository see the update on their next check.

## Where to look

[ARCHITECTURE.md](ARCHITECTURE.md) covers how a sync cycle runs, how playback is relayed,
what the deduplication rules are, and the Jellyfin behaviours that shaped the design.
Reading the section on library scans before touching anything around them will save time.
