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
| cards               | https://cards.alteredcore.org     | **prod** | decks reads cards from prod (`ALTERED_CORE_URL`) |

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
custom `unique-attribute` provider (pseudo uniqueness) + Altered themes; we run it
in dev mode (`start-dev`, H2) and import the realm seeded by `clean.js`.

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
next: local cards-api, collection-api, website, deckbuilder. `AlteredOwnership`
is intentionally not wired yet.
