# syntax=docker/dockerfile:1

# DEV image for the demo-ui (uniques-search-api/demo-ui), a Vite 6 + React 19 SPA.
# Kept HERE in the dev-environment (the repo ships no Dockerfile for it). Mirrors the
# decks/uniques pattern: the AppHost bind-mounts demo-ui/ at /app and we run the Vite
# dev server (HMR); node_modules lives in a volume (npm ci into it on first start).
#
# The SPA talks to the API straight from the browser — the API sets permissive CORS
# (CorsLayer::permissive) — so VITE_API_BASE_URL points at the browser-reachable API
# URL and no Vite proxy / CORS work is needed. Vite reads VITE_-prefixed vars from the
# environment (see demo-ui/README), so WithEnvironment is enough.

FROM node:22-bookworm-slim

WORKDIR /app

COPY --link --chmod=755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint

ENTRYPOINT ["docker-entrypoint"]

# Run Vite on the SAME port we publish (8004) so the HMR websocket — which the browser
# opens to the page's own host:port — lines up. --host binds 0.0.0.0; --strictPort
# fails loudly rather than drifting to another port.
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0", "--port", "8004", "--strictPort"]
