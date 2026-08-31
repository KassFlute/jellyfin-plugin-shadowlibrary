# ShadowLibrary

[![Build](https://github.com/KassFlute/jellyfin-plugin-shadowlibrary/actions/workflows/build.yaml/badge.svg)](https://github.com/KassFlute/jellyfin-plugin-shadowlibrary/actions/workflows/build.yaml)
[![Release](https://img.shields.io/github/v/release/KassFlute/jellyfin-plugin-shadowlibrary)](https://github.com/KassFlute/jellyfin-plugin-shadowlibrary/releases)
[![Jellyfin](https://img.shields.io/badge/jellyfin-10.11-00a4dc)](https://jellyfin.org)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

Jellyfin server plugin that brings the movies and shows of a friend's Jellyfin server into
your own libraries, without copying a single file. They sit beside your own media and play
through your server, which relays the stream from the friend server on demand.

## Install

In Dashboard, Plugins, Repositories, add this repository:

```
https://raw.githubusercontent.com/KassFlute/jellyfin-plugin-shadowlibrary/main/manifest.json
```

Then install ShadowLibrary from the catalogue and restart Jellyfin. Updates show up in the
dashboard like any other plugin.

## Setup

1. Ask the friend server owner for a plain user account on their server. Browsing and
   playback on the libraries they want to share is enough. No administrator rights, no
   download, no deletion, no library management.
2. Dashboard, Plugins, ShadowLibrary. Set the root folder for imported media. It has to
   exist and be writable by the user running Jellyfin.
3. Add a friend server. Its libraries appear as soon as the credentials check out. Pick
   which of your own libraries its movies and shows should go into.
4. Dashboard, Scheduled Tasks, run **Synchronise friend servers**.

Their media shows up in your libraries, carrying a `ShadowLibrary: <name> (<url>)` tag so
you can tell it apart and filter on it. The task then runs on its own, every six hours by
default, and the schedule is yours to change.

The generated files point players back at this server, and a player often fetches them
itself, so the address they carry has to be one your players can reach. It is worked out on
its own: the published address of your Jellyfin network settings or of your container
environment when you have one, otherwise the address you opened the plugin settings through,
which is what makes an install behind a reverse proxy work without being told. The settings
page shows the address in use and lets you change it, which you need to do only if your
players connect through an address different from the one you administer through.

## Good to know

- Movies and shows only, no music.
- Media the friend server has not identified is skipped, since without a TMDb or IMDb id
  there is no way to tell it apart from what you already own.
- Media you already own is skipped. Delete your copy and the friend copy appears on the
  next cycle. Download your own copy and the friend copy goes away.
- When two friend servers hold the same film, it is imported once. The first of the two to
  claim it keeps it, and keeps it across cycles even when it goes offline for a while.
- A server only ever shares what it holds itself, never what it imported from a third
  server. This is a hard rule, not a setting.
- Playback needs the friend server to be up. When it is not, the player gets a clear error
  rather than a stalled stream.
- Renaming a friend server, or the libraries you send it to, moves nothing and loses no
  watch history. Changing the root folder does move everything, and Jellyfin treats the
  result as new items.
- A friend server that stays unreachable for 48 hours, configurable, has its items removed.
  They come back on their own once it answers again, but their watch history does not.
- Requesting tools such as Jellyseerr will count the friend media as available and stop
  offering to download it.

## Requirements

- Jellyfin 10.11.x
- .NET SDK 9.0 to build

## Build

```bash
./scripts/package.sh
```

Writes `artifacts/ShadowLibrary_<version>.zip`, ready to unzip into the plugins folder of
your Jellyfin config directory, `/var/lib/jellyfin/plugins` for the Debian package or
`/config/plugins` in Docker.

Ubuntu ships no 9.0 SDK, so install one locally if needed:

```bash
curl -sSL -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel 9.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md), how the plugin works and where to change things.
- [CONTRIBUTING.md](CONTRIBUTING.md), how to build, test and release.
- [CHANGELOG.md](CHANGELOG.md), what changed between versions.

## License

GPL-3.0, see [LICENSE](LICENSE).
