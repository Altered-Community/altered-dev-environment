#!/bin/sh
set -e

# Download the prebuilt card index once, into the /app/build volume, before
# starting the server. The uniques-http-api binary loads the index from disk
# (config index.source = "disk") and does NOT fetch it itself, so we provide it
# here. The named volume persists it, so this only downloads on the first start.
#
# INDEX_PATH is the same env var the AppHost passes to the server (config.rs
# honours it as a legacy override), so the file we download is exactly the file
# the server then reads. The loader reads the .tar.zst archive directly — no
# extraction (and no zstd) needed at runtime.

INDEX_FILE="${INDEX_PATH:-/app/build/full_index.tar.zst}"
INDEX_URL="https://storage.googleapis.com/taum-reunion-public/index/full_index.tar.zst"

if [ ! -f "$INDEX_FILE" ]; then
  echo "[uniques] index not found at $INDEX_FILE — downloading (~270 MB) from GCS..."
  mkdir -p "$(dirname "$INDEX_FILE")"
  # Download to a .partial then rename: an interrupted download must not leave a
  # truncated file that looks "present" (and fails to load) on the next start.
  curl -fSL "$INDEX_URL" -o "$INDEX_FILE.partial"
  mv "$INDEX_FILE.partial" "$INDEX_FILE"
  echo "[uniques] index downloaded."
else
  echo "[uniques] index present at $INDEX_FILE — skipping download."
fi

exec "$@"
