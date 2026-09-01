# Changelog

## 1.0.1.0

- Check out the repository before syncing the labels (#10) @KassFlute
- Take the Dependabot dependency updates (#9) @KassFlute
- Draft the release notes from the merged pull requests (#7) @KassFlute

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
