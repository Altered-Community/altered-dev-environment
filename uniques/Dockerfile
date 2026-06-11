# syntax=docker/dockerfile:1

# DEV image for uniques-search-api (the `uniques-http-api` server), kept HERE in the
# dev-environment (not in the uniques-search-api repo, which ships only a PROD-only
# Dockerfile: it targets a `runtime` stage that COPYs deployment/production.toml
# — a file the repo does not ship — and bakes the binary with no card index).
#
# Instead, mirroring decks-api/collection-api, the AppHost bind-mounts the repo
# source at /app and we `cargo run` it; the build cache lives in a volume
# (/app/target) and the ~270 MB prebuilt index is downloaded once into another
# volume (/app/build) by the entrypoint — the binary won't fetch it on its own
# (config index.source = "disk"). The dev stage copies no app source, so the
# source does not need to be in the build context.
#
# rust:1.86 matches the toolchain the upstream Dockerfile pins (the workspace is
# edition 2024).

FROM rust:1.86-bookworm

# curl + ca-certificates: download the prebuilt index from GCS in the entrypoint
# (the bookworm base is minimal on tools).
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --link --chmod=755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint

ENTRYPOINT ["docker-entrypoint"]

# `just api` equivalent. cargo checks freshness, so after the first (slow) build
# the target/ volume makes later starts near-instant.
CMD ["cargo", "run", "-p", "uniques-http-api", "--release"]
