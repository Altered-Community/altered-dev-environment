#!/bin/sh
set -e

# Install node deps into the (volume-backed) node_modules on first start only, then run
# the dev server. Mirrors decks/collection installing vendor/ into a volume, and uniques
# compiling into its target/ volume: the host checkout stays clean and the install is
# cached across restarts.
if [ -z "$(ls -A node_modules 2>/dev/null)" ]; then
  echo "[uniques-ui] node_modules empty — running npm ci (first start)..."
  npm ci
  echo "[uniques-ui] deps installed."
else
  echo "[uniques-ui] node_modules present — skipping install."
fi

exec "$@"
