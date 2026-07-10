<?php
// ─────────────────────────────────────────────────────────────────────────────
// AppHost-managed dev config for alteredcore-website.
//
// This file is bind-mounted (read-only) over /var/www/html/config.local.php by
// apphost.cs, so it overrides whatever config.local.php the user keeps in their
// website checkout. It is the single place the dev-environment wires the website
// to the other local services. The website reads plain define() constants (no
// env support), which is why the wiring lives in a PHP file rather than env vars.
//
// NOTE: only non-prod dev secrets here (KC client secret, encryption key) — safe
// to commit, same as the dev secrets already in apphost.cs.
// ─────────────────────────────────────────────────────────────────────────────

// ─── Database ─────────────────────────────────────────────────────────────────
// Host = the Aspire MySQL/MariaDB resource name (its network alias on the Aspire
// docker network). The mariadb entrypoint creates this user+db from MARIADB_*.
define('DB_HOST', 'altered-website-db');
define('DB_NAME', 'alteredcore');
define('DB_USER', 'alteredcore');
define('DB_PASS', 'alteredcore');
define('DB_PREFIX', 'dev_'); // must match the DB_PREFIX the init script ran with

// ─── Keycloak ─────────────────────────────────────────────────────────────────
// Wired to the local Keycloak (realm "players") via the "main-site" client. That
// client's dev secret + local redirect/logout URIs are seeded by
// AlteredAuth/dev/clean.js. KC_URL is the one URL used everywhere; from inside
// this container it resolves to the host gateway (apphost adds --add-host), so the
// server-side auth-code/token exchange reaches Keycloak at the same URL the
// browser used. Scopes: openid (id_token for logout) + profile/email (pseudo +
// identity) + the decks read/write scopes so the access token works against the
// local decks-api.
define('KC_URL',           'http://auth.altered.local.gd:18080');
define('KC_REALM',         'players');
define('KC_CLIENT_ID',     'main-site');
define('KC_CLIENT_SECRET', 'dev-main-site-secret');
define('KC_SCOPES',        'openid profile email read-decks write-deck read-collection');

// ─── KC token encryption ──────────────────────────────────────────────────────
// Fixed dev AES-256 key (64-char hex). Dev only — never reuse in production.
define('ENCRYPTION_KEY', '4f3c2a1b0d9e8f70615243342536271809a1b2c3d4e5f60718293a4b5c6d7e8f');

// ─── Features ─────────────────────────────────────────────────────────────────
define('SHOW_NEWSLETTER', false);
define('COLLECTION_MODE', true);

// ─── Debug ────────────────────────────────────────────────────────────────────
define('API_RESPONSE_DEBUG', true); // local dev: log external API responses

// ─── Privacy ──────────────────────────────────────────────────────────────────
define('STORE_KC_USER_DATA', true);

// ─── Local auth (ignored while KC_URL is set) ────────────────────────────────
define('LOCAL_ALLOW_REGISTER', true);

// ─── TinyMCE ─────────────────────────────────────────────────────────────────
define('TINYMCE_API_KEY', '');

// ─── GitHub App (community feedback form — disabled in dev) ───────────────────
define('GITHUB_APP_ID',              '');
define('GITHUB_APP_INSTALLATION_ID', '');
define('GITHUB_APP_PRIVATE_KEY', '');
define('GITHUB_REPO', 'owner/community-feedback');

// ─── External APIs ────────────────────────────────────────────────────────────
// decks + collection → LOCAL apis, reached over the Aspire network by resource
// name (server-side PHP calls only; the browser never hits them directly).
// Assumes the "decks" and "collection" services are enabled. The local collection
// DB starts empty, so collection pages show no data until some is added.
// cards/cdn stay on prod (cards reads the same prod API decks-api itself uses).
define('CARDS_API_URL',     'https://cards.alteredcore.org');
define('DECKS_API_URL',     'http://altered-decks-api:80');
define('CDN_URL',           'https://cdn.alteredcore.org');
// Server-side proxy target: reach the ownership container by its Aspire network
// alias (host 8003 -> container 8080), same pattern as decks/collection above.
// The browser-facing URL (ownership.altered.local.gd:8003) can't be used here —
// inside the website container it resolves to the container itself.
define('OWNERSHIP_API_URL', 'http://altered-ownership:8080');
// Browser-facing ownership app root (the "Digital ownership" redirect link points here).
define('OWNERSHIP_WEB_URL', 'http://ownership.altered.local.gd:8003');
define('COLLECTION_API_URL', 'http://altered-collection-api:80');
define('COLLECTION_USE_API', true);
// Browser-facing uniques search API (uniques-search-api, host port 8005). The Uniques
// tab's card-search widget calls this straight from the browser (CORS is permissive),
// same pattern as OWNERSHIP_WEB_URL above. Requires the "uniques" service enabled.
// Without this define, card-search.js hides the All Uniques/Frontier format toggle
// and falls back to the old CARDS_API_URL search path.
define('UNIQUES_API_URL', 'https://search.altered.re');

// ─── Deployment ───────────────────────────────────────────────────────────────
define('BASE_URL', '');
define('SITE_NAME', 'AlteredCore');
