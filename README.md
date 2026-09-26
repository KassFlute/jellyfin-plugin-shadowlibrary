# ShadowLibrary

[![Build](https://github.com/KassFlute/jellyfin-plugin-shadowlibrary/actions/workflows/build.yaml/badge.svg)](https://github.com/KassFlute/jellyfin-plugin-shadowlibrary/actions/workflows/build.yaml)
[![Release](https://img.shields.io/github/v/release/KassFlute/jellyfin-plugin-shadowlibrary)](https://github.com/KassFlute/jellyfin-plugin-shadowlibrary/releases)
[![Jellyfin](https://img.shields.io/badge/jellyfin-10.11-00a4dc)](https://jellyfin.org)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

Watch the movies and shows of a friend's Jellyfin server from your own, without copying any
file.

## How it works

The plugin logs into the friend server with a regular user account and writes a small
placeholder for each of its movies and episodes into a folder of yours, the shadow library:
a `.strm` file, a `.nfo` and the artwork. That folder is added to your existing libraries, so
Jellyfin imports it like any other media. When you press play, your server streams the file
from the friend server.

Media you already own is skipped.

## Install

1. In Dashboard > Plugins > Repositories, add:
   ```
   https://raw.githubusercontent.com/KassFlute/jellyfin-plugin-shadowlibrary/main/manifest.json
   ```
2. Install ShadowLibrary from the catalogue and restart Jellyfin.

Requires Jellyfin 10.11.

## Usage

1. Ask your friend for a user account on their server, with access to the libraries they
   want to share. No admin rights needed.
2. In Dashboard > Plugins > ShadowLibrary, set the root folder for imported media. Jellyfin
   must be able to write to it.
3. Add the friend server, pick its libraries and the libraries of yours its movies and
   shows go into.
4. In Dashboard > Scheduled Tasks, run **Synchronise friend servers**. It then runs every
   six hours.

Imported media carries a `ShadowLibrary: <name> (<url>)` tag. Playback needs the friend
server to be online.

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md), how it works in detail.
- [CONTRIBUTING.md](CONTRIBUTING.md), build and release.
- [CHANGELOG.md](CHANGELOG.md)

## License

GPL-3.0, see [LICENSE](LICENSE).
