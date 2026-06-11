# altered-dev-environment

Local orchestrator for the Altered community projects, wired together, built on
**.NET Aspire**. It clones the repos it needs (or reuses your local checkouts),
launches them, and gives you a single dashboard with logs and per-service
start/stop.

## Run

```pwsh
./run.ps1      # Windows / PowerShell
./run.sh       # bash
```

The scripts check the prerequisites — **.NET 10 SDK**, **Aspire CLI**, **Docker** —
and offer to install any that are missing (you confirm each one). If you already
have everything, you can also just `aspire run`.

The first run builds the Keycloak (custom) and decks-api images and installs the
PHP dependencies into a volume — it takes a few minutes. Later runs are fast.
The Aspire dashboard opens in your browser automatically once it's ready (the URL
is also printed in the console).

## What runs (milestone 1)

| Service     | URL (browser)                     | Source | Notes |
|-------------|-----------------------------------|--------|-------|
| `altered-auth`      | http://auth.altered.local.gd:18080    | local  | Keycloak, realm `players`, admin `admin`/`admin` |
| `altered-decks-api` | http://decks.altered.local.gd:8001 (or http://localhost:8001) | local | Symfony/FrankenPHP + Postgres; admin at `/admin/login` |
| `altered-collection-api` | http://collection.altered.local.gd:8002 (or http://localhost:8002) | local | Symfony/API Platform/FrankenPHP + Postgres; docs at `/api/docs` |
| `altered-website`   | http://website.altered.local.gd:18181 (or http://localhost:18181) | local | Plain PHP/Apache + MariaDB; Keycloak SSO via the `main-site` client |
| `altered-uniques-api` | http://uniques.altered.local.gd:8003 (or http://localhost:8003) | local | Rust in-memory search over **Unique** cards (`/api/v2/*`); no DB/auth. Standalone — NOT the prod cards API |
| `altered-uniques-ui` | http://localhost:8004             | local | Vite/React demo SPA for the uniques API (Vite dev server, HMR; browser → API direct) |
| `altered-dbgate`    | http://localhost:18182            | local | One web DB client for **all** project DBs (decks + collection Postgres, website MariaDB) |
| cards               | https://cards.alteredcore.org     | **prod** | decks (and the website) read cards from prod |

`*.local.gd` is public DNS that resolves to `127.0.0.1`, so the browser reaches
Keycloak with **no hosts-file edit**. `*.dev.localhost` resolves to `127.0.0.1` in
every modern browser too (RFC 6761).

### Test users (realm `players`)

`alice` / `bob`, password `TestPassword1234`, each with a fixed Keycloak id
(`sub`) and a `pseudo` attribute. Defined in `AlteredAuth/dev/clean.js`.

### decks data & admins

The decks Postgres is kept in a named volume (`altered-decks-pg-data`), so the DB
**survives restarts**. On every start the AppHost runs (in-process, once decks-api
is ready — no separate dashboard resource) an idempotent UPSERT that makes **alice**
a decks admin (keyed on her fixed Keycloak id), so even after wiping the DB you come
back to a known state. Because
the id is fixed, that row *is* the same user alice logs in as via Keycloak — so the
decks `/admin` UI (http://localhost:8001/admin/login) accepts her. Add more admins
by adding rows to the seed SQL in `apphost.cs` (e.g. bob = `2222…`).

### website

`altered-website` is plain PHP 7.4 + Apache with its own MariaDB — no framework, no
build step. The site reads plain `define()` constants from `config.local.php` (it
has no env-var support), so the dev-environment **owns** that file: a dev copy lives
at [website/config.local.php](website/config.local.php) and is bind-mounted over the
website checkout's own copy. That one file is where the wiring lives:

- **Database** — a MariaDB container (the typed Aspire MySQL integration pointed at
  the `mariadb:10.11` image, so we get a readiness health check + phpMyAdmin). The
  official image creates the `alteredcore` user+db and runs the repo's
  `docker/init-db.sh` (schema + migrations, prefix `dev_`) on **first init**; data
  persists in the `altered-website-db-data` volume. To re-seed, remove that volume.
- **Auth** — wired to the local Keycloak via the **`main-site`** client (seeded by
  `AlteredAuth/dev/clean.js` with a dev secret + the local redirect/logout URIs).
  The alice/bob test users log in here too. The site does the auth-code/token
  exchange server-side, so its container gets the same `auth.altered.local.gd`
  host-gateway override as decks.
- **decks** — `DECKS_API_URL` points at the **local** decks-api over the Aspire
  network (server-side calls only). The site forwards the user's Keycloak access
  token, which decks validates against the same realm. cards/cdn/collection stay on
  **prod** (not wired locally yet).
- **admins** — like decks, the AppHost runs (in-process, once the DB is ready — no
  separate dashboard resource) an idempotent UPSERT that makes **alice** a website
  admin, keyed on her fixed Keycloak `sub`. Admin access is group-based, so she's
  put in the seeded **Admin** group (id 1, all perms) with `is_admin` set; after a
  Keycloak login she reaches the admin panel at `/admin`. A real login never resets
  it, and it survives a DB wipe. Add more admins via the seed SQL in `apphost.cs`.
  (The bundled local `admin`/`admin` account still exists for `KC_URL`-less setups.)
- **plugins** — the same seed auto-activates the **Altered Cards** (`core-altered-cards`)
  and **Réunion Events** (`reunion-events`) plugins, reproducing the admin "Activate"
  action: an `is_active=1` row in `dev_plugins`, plus running `reunion-events`'
  `install.sql` (its `{table}` → `dev_plugin_re_*`) once. Idempotent, so it survives a
  DB wipe — no more activating them by hand in `/admin`.

Browse the website's MariaDB (and every other project DB) with **DbGate** — see
below.

> Editing the realm seed (a new client, changed URIs) means re-running
> `node dev/clean.js` in `AlteredAuth` and restarting the `altered-auth` resource so
> it re-imports (its H2 store is ephemeral, so a restart picks up the new export).

### collection-api

`altered-collection-api` is a Symfony / API Platform service on FrankenPHP with its
own Postgres — the same shape as decks-api. Its auth is simpler than the others: the
`KeycloakJwtDecoder` validates a bearer token **only against the realm JWKS**
(signature), with no client secret, audience, or issuer check, so it accepts any
token the local realm signs — including the one the website forwards. It reads cards
from **prod** (`ALTERED_CORE_URL`) and supports the `DEV_AUTH_ENABLED` HS256 dev
token like decks.

The upstream repo ships **no Dockerfile** (it's meant to run under the Symfony CLI),
so the FrankenPHP scaffolding lives here in [collection/](collection/) — the standard
`dunglas/symfony-docker` template, tuned for `pdo_pgsql`. The AppHost builds its
`frankenphp_dev` target with that folder as the build context and bind-mounts the
collection-api source at `/app`, leaving the upstream repo untouched. Migrations run
on start (entrypoint); the DB persists in the `altered-collection-pg-data` volume.

The local **website** points its `COLLECTION_API_URL` at this service (over the
Aspire network), so the collection features use it — though the DB starts empty.

### uniques (uniques-search-api)

`altered-uniques-api` is [Altered-Re-Union/uniques-search-api](https://github.com/Altered-Re-Union/uniques-search-api)
(the Altered-Re-Union fork of [Taum/rust-cards-api](https://github.com/Taum/rust-cards-api)),
a Rust in-memory search engine over the Altered **Unique** cards. It is **not** the
prod cards API: it has its own contract (`/api/v2/cards`, `/api/v2/card/{reference}`,
`/api/v2/effects`) and only covers Unique characters, so it does **not** replace the
`cards.alteredcore.org` source the other services read. It runs standalone for now —
nothing else is wired to it yet (future consumers reach it at
`http://altered-uniques-api:8080` over the Aspire network).

It's the simplest service in the stack — no database, no Keycloak, no seed, nothing
in DbGate. Two wrinkles are handled here rather than upstream:

- **Build** — the repo ships only a prod Dockerfile (it `COPY`s a
  `deployment/production.toml` that isn't committed, and bakes a no-index image), so
  the dev scaffolding lives here in [uniques/](uniques/): a thin `rust:1.86` image
  whose entrypoint downloads the index, then runs `cargo run -p uniques-http-api
  --release`. The AppHost bind-mounts the repo source at `/app` and keeps the cargo
  build cache in the `altered-uniques-api-target` volume (first build is slow, like
  decks; later starts are fast).
- **Index** — the server loads a ~270 MB prebuilt card index from disk (it doesn't
  fetch it itself). The entrypoint downloads `full_index.tar.zst` from
  `storage.googleapis.com/taum-reunion-public` into the `altered-uniques-index` volume
  on first start only; the loader reads the archive directly (no extraction). Wipe
  that volume to re-download.

Config is driven entirely by env (`PORT`, `INDEX_PATH`) — the app's per-environment
toml files are optional, so no config file is bind-mounted.

**Formats (not wired yet).** A "format" (e.g. `standard`) is a curated card subset — a
small JSON allowlist/denylist of card references, loaded from a `build/formats/` dir
(`manifest.json` + one file per format) and compiled against the index. It's **separate
from the index**: the prebuilt bundle ships none, and nothing in the repo generates one,
so the `[formats]` section stays off. It gates **only** the `format=` filter on
`/api/v2/cards` (otherwise `400 unknown format`) — plain card search and `/api/v2/effects`
work from the index alone. To enable it once we have the files: make a `build/formats/`
available to the container (mount it, or download it in the entrypoint like the index)
and turn on `[formats]` — a few lines. Open question: where those definitions come from
(hand-authored, or published somewhere by Re-Union).

To re-download the index / rebuild this service from scratch — **without touching any
project DB** — wipe its own two volumes (the downloaded index + the cargo build cache)
and restart:

```sh
docker volume rm altered-uniques-index altered-uniques-api-target
```

### uniques-ui (demo-ui)

`altered-uniques-ui` runs the repo's `demo-ui/` — a Vite + React SPA for the uniques API
— via the Vite **dev server** (HMR). The repo ships no Dockerfile for it, so the dev
image lives in [uniques-ui/](uniques-ui/): `demo-ui/` is bind-mounted, `node_modules`
lives in the `altered-uniques-ui-node-modules` volume (`npm ci` on first start), and
`npm run dev` serves it.

The SPA calls the API **straight from the browser** (the API sets `CorsLayer::permissive`),
so `VITE_API_BASE_URL` points at the browser-reachable API (`http://localhost:8003`) — no
Vite proxy, no CORS work. Vite's port is published directly (`-p`, like Keycloak) on the
same internal/external port (8004) so the HMR websocket lines up. **Open it at
http://localhost:8004** — Vite's host allowlist permits `localhost` but not the
`*.local.gd` host (that would need `server.allowedHosts`). Needs the `uniques` service
running to have an API to talk to.

### dbgate

`altered-dbgate` (http://localhost:18182) is a single web DB client for **every**
database in the stack — the decks and collection Postgres instances and the website
MariaDB — so you don't need a separate tool per engine. Connections are pre-seeded
(read-only) from env in `apphost.cs` and reached over the Aspire network by each
DB's resource-name alias; only the **enabled** services' DBs appear. Dev credentials
are filled in already, so the connections are ready on open. (It replaces the
per-service phpMyAdmin.)

To keep the dashboard **Graph** tidy, `apphost.cs` hides the dev DB-password
parameters and the auto-created `database` nodes (only the Postgres/MariaDB
**instances** show — the databases are still created and fully functional, just
not drawn), and declares a relationship from DbGate to each DB instance so it
appears linked rather than as an island. Hiding is purely presentational
(`WithInitialState(... IsHidden = true)`).

## Configuration

- `appsettings.json` — committed defaults; toggle services under `Services:*:Enabled`.
- `appsettings.Local.json` — **not committed** (copy from `appsettings.Local.json.example`).
  This is the "`.env`" of the repo: override `ReposDirectory` (where the Altered
  repos live / get cloned, default: this repo's parent folder) and local secrets.

## How the auth wiring works

Keycloak uses one URL everywhere — `http://auth.altered.local.gd:18080` (set as
`KC_HOSTNAME` and as each API's `KEYCLOAK_BASE_URL`). It works from both the browser
and the API containers — with no hosts-file edit — which is what lets browser-driven
flows started *by* a container (the decks `/admin` login) work too:

- **Browser**: `*.local.gd` is public DNS resolving to `127.0.0.1` → the
  `0.0.0.0:18080` publish, on loopback.
- **Containers**: they'd resolve `auth.altered.local.gd` to `127.0.0.1` (themselves),
  so we override it with `--add-host auth.altered.local.gd:host-gateway` → host gateway → the same
  `0.0.0.0:18080` publish. Keycloak is published on `0.0.0.0` (not the `127.0.0.1`
  the Aspire proxy would use) via an explicit `-p` in `apphost.cs`.

A `*.localhost` host can't be used here: libc short-circuits `*.localhost` to
loopback *inside* containers (and `--add-host` wouldn't override that).

The `auth` image is built from `AlteredAuth/build/Dockerfile`, so it carries the
custom `unique-attribute` provider (pseudo uniqueness) + Altered themes. We build
its **`dev` stage** — the optimized prod build re-augmented for the embedded H2 DB —
and run `start --optimized` (not `start-dev`). That skips Keycloak's slow per-start
augmentation ("installing your custom providers…") while keeping the ephemeral H2 +
realm re-import from `clean.js` on every start. Theme/template **hot-reload** is kept
via runtime options (`KC_SPI_THEME_CACHE_THEMES/TEMPLATES=false`,
`KC_SPI_THEME_STATIC_MAX_AGE=-1`) plus a bind-mount of `build/themes`, so theme edits
show up without a restart or rebuild.

## Getting a token for manual API testing

`decks-api` uses the confidential client `toxicity-deckbuilder`, which requires
user consent — so the CLI password grant doesn't work for it. Get a token via the
browser authorization-code flow (login as `alice`, approve consent), or run the
client app locally and let it drive the login.

`decks-api` also supports `DEV_AUTH_ENABLED=true`: a token with `iss: "dev"` signed
(HS256) with `APP_SECRET` is accepted, bypassing Keycloak — handy for quick tests.

## Adding / removing a service

Set `Services:<name>:Enabled` to `false` in `appsettings.Local.json` to skip a
service, or add a new resource in `apphost.cs` (clone URL in `repoUrls`). Coming
next: local cards-api, deckbuilder. `AlteredOwnership` is intentionally not wired
yet.
