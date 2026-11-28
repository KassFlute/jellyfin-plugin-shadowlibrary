# Changelog

## 1.1.0.0

### Features
- Support Jellyfin 12 (#36) @KassFlute
- Point at the scheduled task after adding a friend server (#34) @KassFlute
- Add an option not to import titles and artwork (#33) @KassFlute
- Check the folder for imported media on save (#31) @KassFlute
- Add a plugin image to the catalogue (#24) @KassFlute

### Bug fixes
- Stop importing collection ids from friend servers (#37) @KassFlute
- Fill in tracks and keyframes under every mount (#35) @KassFlute
- Show the real progress of the synchronisation task (#32) @KassFlute
- Never touch files ShadowLibrary did not generate (#30) @KassFlute
- Give local builds the version of build.yaml (#29) @KassFlute
- Fix imports removed when the media root is mounted twice (#28) @KassFlute
- Fix resume and seek hanging on remuxed imported films (#23) @KassFlute
- Fix the slow start of every playback of an imported item (#21) @KassFlute
- Fix the relay throttling the start of a playback (#20) @KassFlute

### Documentation
- Replace the plugin image with a plain wordmark (#39) @KassFlute
- Reword the settings page (#38) @KassFlute

### Dependencies
- Bump release-drafter/release-drafter/autolabeler from 7.7.0 to 7.10.0 (#27) [@dependabot[bot]](https://github.com/apps/dependabot), @KassFlute
- Bump release-drafter/release-drafter from 7.7.0 to 7.10.0 (#26) [@dependabot[bot]](https://github.com/apps/dependabot), @KassFlute

## 1.0.1.0

### Bug fixes
- Fix slow playback starts and link saturation through the relay (#14) @KassFlute
- Fix network saturation during the library scan (#13) @KassFlute
- Fix the stored library choice in the friend server editor (#12) @KassFlute
- Fix Microsoft.Data.Sqlite failing to load on Jellyfin 10.11.11 (#11) @KassFlute

## 1.0.0.0

First public release.

- Import the movies and shows of another Jellyfin server as `.strm` entries, with their
  metadata, artwork and an origin tag, into the libraries you already have.
- Play them through a local proxy endpoint, so the friend server credentials never reach
  the disk or the client, and seeking works through relayed range requests.
- Skip anything the friend server could not identify, anything you already own, and
  anything another friend server already provides.
- Federated mode between two servers running the plugin, where sharing stops at one hop.
- Remove items a friend server no longer holds, and items of a server that stays
  unreachable past a configurable threshold.
- Encrypt the stored friend server password and session token with AES-GCM.
