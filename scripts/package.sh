#!/usr/bin/env bash
# Builds the plugin into artifacts/ShadowLibrary_<version>.zip, ready to be
# unzipped into <config>/plugins/ of a Jellyfin instance.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/Jellyfin.Plugin.ShadowLibrary/Jellyfin.Plugin.ShadowLibrary.csproj"

# build.yaml is the single source of truth for the version, the same file the release
# pipeline reads, so a local package and a published one never disagree
read_meta() { grep -oPm1 "(?<=^$1: \")[^\"]+" "$ROOT/build.yaml"; }
VERSION="$(read_meta version)"
TARGET_ABI="$(read_meta targetAbi)"
GUID="$(read_meta guid)"

STAGE="$ROOT/artifacts/ShadowLibrary_$VERSION"
rm -rf "$STAGE"
mkdir -p "$STAGE"

dotnet publish "$PROJECT" -c Release -p:Version="$VERSION" -o "$ROOT/artifacts/publish"

# only the plugin assembly ships, the host provides the Jellyfin dependencies
cp "$ROOT/artifacts/publish/Jellyfin.Plugin.ShadowLibrary.dll" "$STAGE/"

cat > "$STAGE/meta.json" <<META
{
    "category": "General",
    "guid": "$GUID",
    "name": "ShadowLibrary",
    "description": "Browse and stream movies and shows from other Jellyfin servers without copying any file.",
    "overview": "Library sharing between Jellyfin instances.",
    "owner": "cassien",
    "targetAbi": "$TARGET_ABI",
    "framework": "net9.0",
    "version": "$VERSION",
    "changelog": "",
    "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
META

( cd "$ROOT/artifacts" && rm -f "ShadowLibrary_$VERSION.zip" \
  && zip -qr "ShadowLibrary_$VERSION.zip" "ShadowLibrary_$VERSION" )

echo "Package: $ROOT/artifacts/ShadowLibrary_$VERSION.zip"
