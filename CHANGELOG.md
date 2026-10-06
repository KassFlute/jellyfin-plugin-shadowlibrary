# Changelog

## 1.1.0.0

### Features
- Add a plugin image to the catalogue (#24) @KassFlute

### Bug fixes
- Fix resume and seek hanging on remuxed imported films (#23) @KassFlute
- Fix the slow start of every playback of an imported item (#21) @KassFlute
- Fix the relay throttling the start of a playback (#20) @KassFlute

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
