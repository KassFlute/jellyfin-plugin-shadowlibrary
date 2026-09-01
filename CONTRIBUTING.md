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

Both the version and the changelog in that file are written by the release preparation
workflow, not by hand. `targetAbi` is the exception, bump it yourself when the plugin starts
needing a newer server.

## Releasing

Release Drafter keeps a draft release up to date from the pull requests merged since the last
one, and opens a `Prepare ShadowLibrary <version>` pull request carrying the same notes in
`build.yaml`, which is what the plugin catalogue shows, and in `CHANGELOG.md`. Releasing is
then two steps.

1. Merge the preparation pull request.
2. Publish the draft release.

The publish workflow builds the plugin, attaches the zip and its checksums to the release,
adds the version to `manifest.json` and pushes that back to `main`. Servers that added the
repository see the update on their next check.

Everything here reads merged pull requests, so a commit pushed straight to `main` reaches
users without ever showing up in a changelog. Their labels decide two things: the version,
`breaking` for a major and `feature` for a minor, anything else for a patch, and the section
an entry lands in. `skip-changelog` leaves a pull request out. The label set lives in
`.github/labels.yml` and the `Sync labels` workflow applies it.

## Where to look

[ARCHITECTURE.md](ARCHITECTURE.md) covers how a sync cycle runs, how playback is relayed,
what the deduplication rules are, and the Jellyfin behaviours that shaped the design.
Reading the section on library scans before touching anything around them will save time.
