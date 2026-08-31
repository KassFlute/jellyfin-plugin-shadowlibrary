# Architecture

How ShadowLibrary works, and where to look when changing it.

## The idea

The plugin never touches the Jellyfin database to create media. It writes `.strm` files, the
native Jellyfin mechanism for a media item whose content lives behind a URL, and lets the
normal library scanner pick them up. Everything downstream, metadata, artwork, playback,
transcoding, is stock Jellyfin behaviour.

A generated `.strm` never points at the friend server. It points back at the plugin's own
proxy endpoint on this server, which resolves a fresh playback URL at play time and relays
the bytes. That keeps the friend server credentials off the disk and off the client, and it
survives the friend server changing how it addresses its own files.

```
friend server                this server                        player
     |                            |                                |
     |  1. catalogue over the     |                                |
     |     standard API           |                                |
     |<---------------------------|                                |
     |                            |  writes .strm .nfo images      |
     |                            |  into <root>/<friend>/...      |
     |                            |                                |
     |                            |  2. library scan, then         |
     |                            |     ffprobe through the proxy  |
     |                            |                                |
     |                            |<-------------------------------|  3. play
     |  4. PlaybackInfo           |                                |
     |<---------------------------|                                |
     |  5. /Videos/{id}/stream    |                                |
     |============================>================================>
```

## Where things live

| What | Where |
|---|---|
| Plugin settings and friend servers | `<config>/plugins/configurations/Jellyfin.Plugin.ShadowLibrary.xml` |
| Imported item database, encryption key | `<config>/plugins/Jellyfin.Plugin.ShadowLibrary/` |
| Generated media | the configured media root, one folder per friend server |

Both plugin paths derive from the assembly name, not from the version, so they survive
plugin updates. The generated media folders are declared as extra sources of the user's own
libraries, so imported media sits beside their files rather than in a library of its own.

The folder name of a friend server is chosen once, when the entry is created, and stored in
`FolderName`. Renaming an entry afterwards changes the display name and the origin tag, never
a path on disk. Jellyfin keys an item on its path, so a folder that follows the display name
would throw away the watch history of everything imported on every rename. Changing the media
root does move the tree, and
[GeneratedPathMigrator.cs](Jellyfin.Plugin.ShadowLibrary/Sync/GeneratedPathMigrator.cs) does
it in one piece: detach, move, rewrite the stored paths, reattach.

## Components

### Entry points

- [Plugin.cs](Jellyfin.Plugin.ShadowLibrary/Plugin.cs), plugin identity and the config page.
- [PluginServiceRegistrator.cs](Jellyfin.Plugin.ShadowLibrary/PluginServiceRegistrator.cs),
  dependency injection. Also registers the named HTTP client used for relaying media, which
  carries no timeout, unlike the shared one.
- [SyncScheduledTask.cs](Jellyfin.Plugin.ShadowLibrary/Sync/SyncScheduledTask.cs), the only
  scheduled task. Every run handles every enabled friend server, so the play button in the
  dashboard does what a user expects.

### Configuration

- [PluginConfiguration.cs](Jellyfin.Plugin.ShadowLibrary/Configuration/PluginConfiguration.cs),
  global settings.
- [FriendServer.cs](Jellyfin.Plugin.ShadowLibrary/Configuration/FriendServer.cs), one entry
  per friend server, including the encrypted credentials.
- [ConfigurationStore.cs](Jellyfin.Plugin.ShadowLibrary/Configuration/ConfigurationStore.cs),
  the single way to write the configuration. Everything goes through it, otherwise two
  concurrent writers lose each other's changes.
- [configPage.html](Jellyfin.Plugin.ShadowLibrary/Configuration/configPage.html), the admin
  page. Plain DOM, no framework, talking to the plugin's own API.

### Talking to a friend server

- [FriendServerClient.cs](Jellyfin.Plugin.ShadowLibrary/Remote/FriendServerClient.cs), every
  call to a friend server. Authentication, catalogue listing, images, playback info, media
  relay.
- [FriendServerSessionProvider.cs](Jellyfin.Plugin.ShadowLibrary/Sync/FriendServerSessionProvider.cs),
  hands out session tokens, reusing the stored one and re-authenticating when it is refused.
- [SecretStore.cs](Jellyfin.Plugin.ShadowLibrary/Security/SecretStore.cs), AES-GCM over the
  stored password and token. The key sits next to the data, so this protects a config file
  read out of context, not a compromised host.

### The sync cycle

- [FriendServerSynchronizer.cs](Jellyfin.Plugin.ShadowLibrary/Sync/FriendServerSynchronizer.cs),
  the orchestration. Read this one first.
- [LocalCatalogue.cs](Jellyfin.Plugin.ShadowLibrary/Sync/LocalCatalogue.cs), what the user
  already owns, so it is never imported twice. It also hands out the claims that keep two
  friend servers holding the same film from producing two items.
- [GeneratedPathMigrator.cs](Jellyfin.Plugin.ShadowLibrary/Sync/GeneratedPathMigrator.cs),
  moves the generated tree when the media root changes.
- [LibraryAttacher.cs](Jellyfin.Plugin.ShadowLibrary/Sync/LibraryAttacher.cs), declares the
  generated folders as sources of the user's libraries.
- [MediaFileWriter.cs](Jellyfin.Plugin.ShadowLibrary/Sync/MediaFileWriter.cs), the `.strm`,
  `.nfo`, images, folder naming and metadata hashing.
- [MediaProbe.cs](Jellyfin.Plugin.ShadowLibrary/Sync/MediaProbe.cs), asks Jellyfin to look
  inside the generated files.
- [ImportedMediaCleaner.cs](Jellyfin.Plugin.ShadowLibrary/Sync/ImportedMediaCleaner.cs),
  removal, of one item, of a series, or of everything from one friend server.
- [ImportedItemStore.cs](Jellyfin.Plugin.ShadowLibrary/Storage/ImportedItemStore.cs), the
  SQLite store.

### HTTP surface

- [ShadowLibraryController.cs](Jellyfin.Plugin.ShadowLibrary/Api/ShadowLibraryController.cs),
  `ping` and `native-items`, the two endpoints another ShadowLibrary instance calls.
- [StreamController.cs](Jellyfin.Plugin.ShadowLibrary/Api/StreamController.cs), the playback
  proxy.
- [FriendServersController.cs](Jellyfin.Plugin.ShadowLibrary/Api/FriendServersController.cs),
  what the admin page uses.

## One sync cycle

[FriendServerSynchronizer.SyncAsync](Jellyfin.Plugin.ShadowLibrary/Sync/FriendServerSynchronizer.cs),
for one friend server:

1. **Authenticate**, reusing the stored token. A rejected token triggers one re-authentication
   and one retry, otherwise a revoked account would look like an unreachable server and
   eventually trip the removal threshold.
2. **Detect the mode**, by calling `/ShadowLibrary/ping` on the friend. An answer means the
   friend runs the plugin, so only the items it declares as native are eligible.
3. **List the catalogue**, three paged calls, movies then series then episodes. Virtual items,
   the metadata-only entries Jellyfin keeps for episodes it does not hold, are excluded at the
   query. Failure here aborts the cycle without deleting anything.
4. **Attach the folders** to the user's libraries, once, remembering what was attached so a
   path removed by hand is not put back.
5. **Import**, item by item. Skip anything without a TMDb or IMDb id, skip anything the user
   already owns, skip anything another friend server already provides, write files for the
   rest. An unchanged metadata hash means only the `.strm` is checked, since its URL depends
   on local settings rather than on the friend.
6. **Remove the orphans**. An item missing from a listing that succeeded is a confirmed
   deletion, whether the friend dropped it or the user now owns it.
7. **Scan and wait**, but only if something changed. The built in `RefreshLibrary` task is
   queued and the cycle waits on its completion event, so the items exist before the next
   step. Past two hours it gives up and leaves them to the next cycle.
8. **Match** the written files to the Jellyfin items the scan just created.
9. **Inspect** the new items, so their audio and subtitle tracks are known.
10. **Log** one line with the counters.

When the listing fails, none of that happens. Every item starts or continues an
unavailability countdown instead, and is only removed once it has been continuously
unreachable past the configured threshold.

## Deduplication

Three rules, applied in that order, on a key built from the TMDb or IMDb id, and for an
episode from the series key plus its season and episode numbers.

1. An item the friend server could not identify is skipped, there is nothing to compare.
2. An item the user already holds natively is skipped. `LocalCatalogue` reads the local
   libraries once per run and leaves out everything under the media root, which is how a
   previous import does not count as a local copy.
3. An item another friend server already provides is skipped. The first server of the run to
   claim a key keeps it. Ownership is stored on the row, in `claim_keys`, and seeded back at
   the start of the next run, so a friend server that happens to be unreachable today does not
   lose its items to another one and get them back tomorrow.

The same claim also catches a friend server that lists the same film twice, which would
otherwise have both copies writing to the same generated path.

## Playback

[StreamController.cs](Jellyfin.Plugin.ShadowLibrary/Api/StreamController.cs) answers
`/ShadowLibrary/stream/{id}`, resolves the item in the SQLite store, asks the friend server
for a fresh playback description, then relays `/Videos/{id}/stream?static=true`.

`Range` and `HEAD` are passed through untouched, which is what makes seeking work and what
lets ffprobe inspect the file.

The endpoint is anonymous, guarded by a key carried in the `.strm` URL. Depending on the
playback decision, that URL is fetched by the local media pipeline or handed to the player,
and neither presents a plugin session. The friend server token never appears in the `.strm`.

### The address in a .strm

It has to be reachable by players, not only by this server. `MediaInfoHelper` only takes the
server out of the path for a remote source when the user carries
`PermissionKind.ForceRemoteSourceTranscoding`, and that flag forces a full re-encode
(`allowVideoStreamCopy=false&allowAudioStreamCopy=false`). Without it, direct play of a remote
URL is allowed and the player fetches `MediaSourceInfo.Path` itself, which for an imported
item is the `.strm` content. One file carries one URL for two possible consumers, ffmpeg on
this server and a player anywhere, which is why this cannot be solved by picking a smarter
default alone.

[MediaFileWriter.ResolveBaseUrl](Jellyfin.Plugin.ShadowLibrary/Sync/MediaFileWriter.cs) works
down this list:

1. the plugin setting, when it is filled,
2. `PublishedServerUriBySubnet` from the Jellyfin network settings, scope `all` or `external`,
3. the published address the server was started with, the `--published-server-url` option or
   `JELLYFIN_PublishedServerUrl`. It is private to `ApplicationHost`, and asking
   `GetSmartApiUrl` for the address of a caller outside every private range is the only way it
   surfaces. The answer is kept only when its host is routable, since without a published
   address that call falls back to an interface of the host, which step 4 already covers,
4. `GetApiUrlForLocalAccess`, the first non-loopback interface.

Step 4 is a guess and it is wrong for the two most common deployments, a container on a bridge
network (the container address) and anything behind a reverse proxy. So when the first three
steps come up empty, the settings page adopts the address the administrator reached it
through, in
[FriendServersController.DetectStreamBaseUrl](Jellyfin.Plugin.ShadowLibrary/Api/FriendServersController.cs).
That address is written into the configuration once, and `StreamBaseUrlDetected` records that
it happened, so clearing the field afterwards is a decision and is not undone on the next
visit. A loopback address is never adopted, an administrator working on the machine itself
would otherwise pin every generated file to an address no other device can reach.

The scheme of an adopted address is guessed, in `MediaFileWriter.ResolveAdoptedScheme`. A proxy
that terminates TLS and is not declared in the `KnownProxies` of Jellyfin leaves the request
looking like plain http, so a name served on the default port is taken to be https. An address,
or a name on a port of its own, is a direct connection and is left as it is, otherwise a plain
`http://nas.local:8096` would be broken to fix a guess. A wrong guess shows up in the settings
field and is one edit away.

Note that `GetApiUrlForLocalAccess` passes no source address, and `NetworkManager.GetBindAddress`
skips `MatchesPublishedServerUrl` entirely on that path. Reading the published address is
therefore not redundant with it: without step 2 a correctly published server still ends up with
a raw interface address in its `.strm` files.

Errors are explicit. 502 when the friend server cannot be reached, 410 when it no longer
holds the item.

## Federation

Two plugin instances that know each other exchange one thing, the list of items each holds
natively. `/ShadowLibrary/native-items` returns the local movies and episodes minus
everything in the imported item store, scoped to the calling account so a friend never learns
about libraries their own account cannot see.

This stops relaying at one hop. A server shares what it owns, never what it borrowed. The
rule is enforced by the endpoint rather than by permissions, so it relies on the friend
running an honest build, which is a deliberate choice rather than an oversight.

## Data model

`shadowlibrary.db` holds two tables, both defined in
[ImportedItemStore.cs](Jellyfin.Plugin.ShadowLibrary/Storage/ImportedItemStore.cs).

`imported_items`, one row per playable item, is the authority on what the plugin considers
imported. Not the Jellyfin tags, which a user can change. It carries the plugin side id used
in the `.strm` URL, the friend server and remote id, the resolved Jellyfin id, the generated
paths, the last successful import, the start of any unavailability and a metadata hash.

`imported_series` holds the show level folders, which have no playable file of their own.

The database runs in WAL mode. A cycle writes rows while playback requests read them, and the
rollback journal would let a write make a read fail with a locked database.

The schema carries a version number. A mismatch rebuilds the tables rather than migrating.
That costs one import cycle and nothing visible, since the generated paths are deterministic,
so the files are rewritten in place and Jellyfin keeps its items and their watch history.

## Jellyfin behaviours worth knowing

Things that are not obvious from the API surface and that shaped the design. Line numbers
are from Jellyfin 10.11.0.

**A library scan never looks inside a `.strm`.** `FFProbeVideoInfo.cs` guards the probe with
`if (!item.IsShortcut || options.EnableRemoteContentProbe)`. Only two callers set that flag,
Jellyfin itself on the first playback request, and this plugin. That is why
[MediaProbe.cs](Jellyfin.Plugin.ShadowLibrary/Sync/MediaProbe.cs) exists.

**That probe needs `FullRefresh`, not a lower mode.** Below it, `MetadataService` keeps only
the providers whose `HasChanged` reports something, and the probe provider reports nothing
for a `.strm` untouched since the scan. `FullRefresh` also calls remote metadata providers,
which cannot overwrite anything: they run with `replaceData: false`, and the image refresh
mode is left at its default so local artwork is never replaced.

**Jellyfin does not merge duplicate movies.** Alternate versions exist as a data structure
but are only ever created through `POST /Videos/MergeVersions`, never by the scanner. Two
folders holding the same film give two items, which is why deduplication happens before
anything is written. Series are different, a library option merges shows spread across
several folders.

**No library scan call blocks.** `QueueLibraryScan` queues, and `ValidateMediaLibrary` queues
too. It reads as if it scanned, but the whole body is `_taskManager.CancelIfRunningAndQueue<RefreshMediaLibraryTask>()`
followed by `return Task.CompletedTask`. Awaiting it returns before a single folder has been
walked. Anything that needs the scanned items has to run the `RefreshLibrary` scheduled task
and wait on `ITaskManager.TaskCompleted`, which is what
[FriendServerSynchronizer.ScanAsync](Jellyfin.Plugin.ShadowLibrary/Sync/FriendServerSynchronizer.cs)
does. Getting this wrong is invisible on a server whose items already exist, and breaks a
fresh install: the cycle writes the files, resolves nothing because the scan has not started,
inspects nothing, and the media lands in the library without its tracks.

**A media path can be added to an existing library**, `ILibraryManager.AddMediaPath`, so the
plugin never has to create a library of its own or write into the user's folders.

## Build and package

```bash
./scripts/package.sh
```

Builds in Release and stages `artifacts/ShadowLibrary_<version>/` with the plugin assembly
and a generated `meta.json`, then zips it. Only the plugin assembly ships. Jellyfin provides
its own dependencies, and `Microsoft.Data.Sqlite` is referenced with `ExcludeAssets="runtime"`
so the copy the server already loads is used rather than a second one.

The version lives in
[Jellyfin.Plugin.ShadowLibrary.csproj](Jellyfin.Plugin.ShadowLibrary/Jellyfin.Plugin.ShadowLibrary.csproj)
and is read from there by the packaging script. Keep [build.yaml](build.yaml) in step.

## Conventions

Every log message starts with `[ShadowLibrary]`. Jellyfin logs through Serilog with a
template that shows neither the category nor the scope, so the prefix has to be in the
message itself. A decorator would not work, Serilog rebuilds the message from the original
template and ignores the formatter it is handed.
