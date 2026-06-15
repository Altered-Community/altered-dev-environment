#:sdk Aspire.AppHost.Sdk@13.3.5
#:package Aspire.Hosting.PostgreSQL@13.3.5
#:package Aspire.Hosting.MySql@13.3.5
#:package Aspire.Hosting.Redis@13.3.5
#:package Npgsql@10.0.3
#:package MySqlConnector@2.4.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Npgsql;

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
//   - appsettings.json        : committed defaults (which services are enabled)
//   - appsettings.Local.json  : NOT committed; overrides ReposDirectory + secrets
//     (plays the role of the non-committed .env). Resolved relative to this file
//     so it works regardless of the build output directory.
// ---------------------------------------------------------------------------
var appHostDir = AppHostDirectory();
builder.Configuration
    .AddJsonFile(Path.Combine(appHostDir, "appsettings.json"), optional: true, reloadOnChange: false)
    .AddJsonFile(Path.Combine(appHostDir, "appsettings.Local.json"), optional: true, reloadOnChange: false);

// Where the Altered repos live. Default: the parent of this repo (repos are
// siblings). Override via appsettings.Local.json ("ReposDirectory") or the
// ALTERED_REPOS_DIR environment variable.
var reposDir = builder.Configuration["ReposDirectory"]
    ?? Environment.GetEnvironmentVariable("ALTERED_REPOS_DIR")
    ?? Directory.GetParent(appHostDir)!.FullName;

bool Enabled(string service) => builder.Configuration.GetValue($"Services:{service}:Enabled", true);

// ---------------------------------------------------------------------------
// Repos: clone on demand, otherwise use the local checkout.
// ---------------------------------------------------------------------------
var repoUrls = new Dictionary<string, string>
{
    ["AlteredAuth"] = "https://github.com/Altered-Re-Union/AlteredAuth.git",
    ["altered-core-decks-api"] = "https://github.com/Altered-Community/altered-core-decks-api.git",
    ["altered-core-cards-api"] = "https://github.com/Altered-Community/altered-core-cards-api.git",
    ["altered-core-collection-api"] = "https://github.com/Altered-Community/altered-core-collection-api.git",
    ["AlteredOwnership"] = "https://github.com/Altered-Re-Union/AlteredOwnership.git",
    ["alteredcore-website"] = "https://github.com/Altered-Community/alteredcore-website.git",
    ["altered-deckbuilder-poc-v2"] = "https://github.com/Altered-Community/altered-deckbuilder-poc-v2.git",
    ["uniques-search-api"] = "https://github.com/Altered-Re-Union/uniques-search-api.git",
};

// Handles to cross-referenced resources, assigned as each service is mounted, so
// later services can WaitFor them only when their dependency is actually enabled.
IResourceBuilder<ContainerResource>? authApp = null;
IResourceBuilder<ContainerResource>? decksApp = null;
IResourceBuilder<ContainerResource>? collectionApp = null;

// The DB server resources, captured so DbGate can declare a relationship to each
// (graph edges) — only the enabled ones are non-null.
IResource? decksPgResource = null;
IResource? collectionPgResource = null;
IResource? websiteDbResource = null;

// The uniques API resource, captured so the demo-ui can declare a graph relationship to
// it (browser-side runtime dependency — edge only, no startup gating).
IResource? uniquesApiResource = null;

// ===========================================================================
// auth — Keycloak (local), built from AlteredAuth/build/Dockerfile so it carries
// the custom "unique-attribute" provider (pseudo uniqueness) + Altered themes.
// The image is a prod/optimized build, so we override the command to run dev mode
// with H2 and import the realm seeded by AlteredAuth/dev/clean.js.
//
// One URL for everyone — http://auth.altered.local.gd:8080 — so it works for the
// browser (login/consent + token "iss"), server-side API calls (JWKS) AND
// browser-driven OAuth started by a container (the decks /admin flow), with NO
// hosts-file edit. We namespace under `altered.local.gd` so it can't clash with
// another project also using local.gd.
//   - Browser: `*.local.gd` is public DNS resolving to 127.0.0.1 (any depth),
//     reaching the 0.0.0.0:8080 publish below on loopback.
//   - Containers: they'd also resolve it to 127.0.0.1 (themselves), so we override
//     it with --add-host -> the host gateway, also reaching 0.0.0.0:8080.
// We publish Keycloak on 0.0.0.0 (not the 127.0.0.1 the Aspire proxy would use) so
// the gateway path works. A `*.localhost` host can't be used: libc short-circuits
// it to loopback inside containers (and --add-host wouldn't override that).
// ===========================================================================
const string AuthUrl = "http://auth.altered.local.gd:18080";

if (Enabled("auth"))
{
    var authRepo = Repo("AlteredAuth");

    authApp = builder.AddDockerfile("altered-auth", Path.Combine(authRepo, "build"))
        .WithArgs("start-dev", "--import-realm")
        // Publish on 0.0.0.0 (bypassing the Aspire 127.0.0.1 proxy) so the API
        // containers can reach Keycloak via the host gateway. Host port 18080 (not
        // the common 8080) to avoid clashing with other local projects.
        .WithContainerRuntimeArgs("-p", "0.0.0.0:18080:8080")
        .WithEnvironment("KC_DB", "dev-file")               // matches the dev image's baked build (prod image bakes postgres)
        .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
        .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
        .WithEnvironment("KC_HTTP_ENABLED", "true")
        .WithEnvironment("KC_HEALTH_ENABLED", "true")
        .WithEnvironment("KC_HOSTNAME", AuthUrl)
        .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true")
        // Keep theme/template hot-reload (what start-dev used to give us): these are
        // RUNTIME options, so they work under `start --optimized`. Combined with the
        // themes bind-mount below, edits to the Altered themes show up without a
        // restart or image rebuild.
        .WithEnvironment("KC_SPI_THEME_CACHE_THEMES", "false")
        .WithEnvironment("KC_SPI_THEME_CACHE_TEMPLATES", "false")
        .WithEnvironment("KC_SPI_THEME_STATIC_MAX_AGE", "-1")
        // Health: Keycloak's management interface (port 9000, enabled by
        // KC_HEALTH_ENABLED) serves /health/ready, which only returns 200 once the
        // server is up AND the realm import has finished. Expose it as an Aspire
        // endpoint and health-check it, so the dashboard shows the resource as
        // healthy only when it's truly ready (and WaitFor dependencies wait).
        .WithHttpEndpoint(targetPort: 9000, name: "management")
        .WithHttpHealthCheck("/health/ready", endpointName: "management")
        .WithBindMount(
            Path.Combine(authRepo, "dev", "realm-export.json"),
            "/opt/keycloak/data/import/realm-export.json",
            isReadOnly: true)
        // Live theme editing: mount the host themes over the baked copy so changes
        // are read straight from disk (theme cache is disabled above).
        .WithBindMount(
            Path.Combine(authRepo, "build", "themes"),
            "/opt/keycloak/themes",
            isReadOnly: true)
        // Dashboard links (we publish via -p, so Aspire has no endpoint to show).
        .WithUrl($"{AuthUrl}/realms/players/account/", "edit profile")
        .WithUrl($"{AuthUrl}/admin/", "admin");
}

// ===========================================================================
// decks-api — altered-core-decks-api (local). Symfony/FrankenPHP, own Postgres.
// Validates Keycloak JWTs (iss must match AuthUrl) and reads cards from prod.
// ===========================================================================
// No trailing slash: both decks-api and collection-api build cards URLs as
// ALTERED_CORE_URL . "/api/cards/..." — a trailing slash here yields a double
// slash ("//api/cards/batch") that the prod cards server 404s.
const string CardsProdUrl = "https://cards.alteredcore.org";

if (Enabled("decks"))
{
    var decksRepo = Repo("altered-core-decks-api");

    var decksPgPassword = builder.AddParameter("altered-decks-db-password", "altereddev", secret: true)
        .WithInitialState(Hidden("Parameter")); // dev secret — keep it out of the graph
    var decksPg = builder.AddPostgres("altered-decks-pg", password: decksPgPassword)
        .WithDataVolume("altered-decks-pg-data"); // persist the DB across restarts
    // AddDatabase auto-creates the DB on the server; we hide its node so only the
    // Postgres instance shows in the graph (the database stays fully functional).
    var decksDb = decksPg.AddDatabase("altered-deck", "altered_deck")
        .WithInitialState(Hidden("Database"));

    // decks-api wants a libpq URL; the host is the postgres resource's network
    // alias (resource name) on the Aspire network, internal port 5432.
    const string decksDbUrl =
        "postgresql://postgres:altereddev@altered-decks-pg:5432/altered_deck?serverVersion=16&charset=utf8";

    var decksApi = builder.AddDockerfile("altered-decks-api", decksRepo, "Dockerfile", stage: "frankenphp_dev")
        .WithBindMount(decksRepo, "/app")
        // Keep vendor/ in a container-managed volume (not the host's, which may be
        // incomplete on Windows). The dev image bakes no vendor, so the entrypoint
        // runs "composer install" into this volume on first start.
        .WithVolume("altered-decks-api-vendor", "/app/vendor")
        .WithHttpEndpoint(port: 8001, targetPort: 80, name: "http")
        // Resolve the Keycloak host to the host gateway from inside the container
        // (overriding the public *.local.gd -> 127.0.0.1 record), so decks reaches
        // Keycloak (published on 0.0.0.0:8080) at the same URL the browser uses.
        .WithContainerRuntimeArgs("--add-host", "auth.altered.local.gd:host-gateway")
        .WithEnvironment("APP_ENV", "dev")
        .WithEnvironment("APP_SECRET", "c869928bd9fb7963519fc0d4bdb1501d80707aa1f4947d583e4e6d0cd06bbcb8")
        .WithEnvironment("SERVER_NAME", ":80")
        .WithEnvironment("DEFAULT_URI", "http://decks.dev.localhost:8001")
        .WithEnvironment("DATABASE_URL", decksDbUrl)
        .WithEnvironment("CORS_ALLOW_ORIGIN", "^https?://([a-z0-9-]+\\.)*(localhost|127\\.0\\.0\\.1|dev\\.localhost)(:[0-9]+)?$")
        .WithEnvironment("KEYCLOAK_BASE_URL", AuthUrl)
        .WithEnvironment("KEYCLOAK_REALM", "players")
        .WithEnvironment("KEYCLOAK_CLIENT_ID", "toxicity-deckbuilder")
        .WithEnvironment("KEYCLOAK_CLIENT_SECRET", "dev-toxicity-deckbuilder-secret")
        .WithEnvironment("DEV_AUTH_ENABLED", "true")
        .WithEnvironment("ALTERED_CORE_URL", CardsProdUrl)
        // Mercure (Caddy module) needs its JWT keys present to boot.
        .WithEnvironment("MERCURE_PUBLISHER_JWT_KEY", "!ChangeThisMercureHubJWTSecretKey!")
        .WithEnvironment("MERCURE_SUBSCRIBER_JWT_KEY", "!ChangeThisMercureHubJWTSecretKey!")
        .WithEnvironment("MERCURE_PUBLISHER_JWT_ALG", "HS256")
        .WithEnvironment("MERCURE_SUBSCRIBER_JWT_ALG", "HS256")
        .WithReference(decksDb)
        .WaitFor(decksDb)
        // Dashboard links: a friendly *.local.gd host (resolves to 127.0.0.1 ->
        // the Aspire proxy on :8001) plus a direct link to the admin login.
        .WithUrl("http://decks.altered.local.gd:8001/admin/login", "admin");

    decksApp = decksApi;

    // Group the Postgres server (and its hidden DB) under the API in the dashboard's
    // Resources tree, and render the API->DB edge in the graph. The DB node itself is
    // hidden, so parenting the visible *server* is what links/groups them.
    decksPg.WithParentRelationship(decksApi.Resource);

    // Auto-seed dev admins so they survive a DB reset and a fresh start: an
    // idempotent UPSERT keyed on the user's FIXED Keycloak id (= JWT "sub"). It
    // runs in-process from the AppHost (no extra container, so nothing shows up in
    // the dashboard masquerading as an app) once decks-api reports ready — its
    // entrypoint has applied the migrations, so the "user" table exists. Re-runs on
    // every start; ON CONFLICT keeps it a no-op when the row already exists (e.g.
    // created by a real login). Add rows here for more admins (e.g. bob = 2222...).
    const string adminSeedSql =
        "INSERT INTO \"user\" (id, keycloak_id, email, username, created_at, is_admin) " +
        "VALUES (gen_random_uuid(), '11111111-1111-1111-1111-111111111111', 'alice@example.test', 'alice', now(), true) " +
        "ON CONFLICT (keycloak_id) DO UPDATE SET is_admin = true;";

    decksApi.OnResourceReady(async (_, _, ct) =>
    {
        // Resolved from the AppHost process, this connection string points at the
        // Postgres container's host-mapped port, so we can reach it directly.
        var connectionString = await decksDb.Resource.ConnectionStringExpression.GetValueAsync(ct);

        // decks-api applies migrations in its entrypoint asynchronously, so the
        // "user" table may not exist the instant the container reports ready —
        // retry briefly before giving up.
        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(adminSeedSql, conn);
                await cmd.ExecuteNonQueryAsync(ct);
                Console.WriteLine("[altered] decks admin seed applied.");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    Console.WriteLine($"[altered] decks admin seed FAILED after {maxAttempts} attempts ({ex.Message}).");
                    return;
                }
                Console.WriteLine($"[altered] decks admin seed attempt {attempt}/{maxAttempts} failed ({ex.Message}); retrying in 3s...");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    });
}

// ===========================================================================
// collection-api — altered-core-collection-api (local). Symfony/API Platform on
// FrankenPHP + Postgres. Validates Keycloak JWTs via the realm JWKS only — no
// client/secret, no audience/issuer check (see KeycloakJwtDecoder) — so it accepts
// any token signed by the local realm (e.g. one minted for the website). Reads
// cards from prod. The FrankenPHP Dockerfile lives in this repo (collection/)
// because the upstream repo ships none; the source is bind-mounted at runtime.
// ===========================================================================
if (Enabled("collection"))
{
    var collectionRepo = Repo("altered-core-collection-api");

    var collectionPgPassword = builder.AddParameter("altered-collection-db-password", "altereddev", secret: true)
        .WithInitialState(Hidden("Parameter")); // dev secret — keep it out of the graph
    var collectionPg = builder.AddPostgres("altered-collection-pg", password: collectionPgPassword)
        .WithDataVolume("altered-collection-pg-data");
    var collectionDb = collectionPg.AddDatabase("altered-collection", "altered_collection")
        .WithInitialState(Hidden("Database")); // hide the DB node; instance stays visible

    const string collectionDbUrl =
        "postgresql://postgres:altereddev@altered-collection-pg:5432/altered_collection?serverVersion=16&charset=utf8";

    collectionApp = builder.AddDockerfile(
            "altered-collection-api", Path.Combine(appHostDir, "collection"), "Dockerfile", stage: "frankenphp_dev")
        .WithBindMount(collectionRepo, "/app")
        // vendor/ in a container-managed volume; the dev entrypoint runs composer
        // install into it on first start (the host vendor may be incomplete on
        // Windows).
        .WithVolume("altered-collection-api-vendor", "/app/vendor")
        .WithHttpEndpoint(port: 8002, targetPort: 80, name: "http")
        // Reach Keycloak (published on 0.0.0.0:18080) from inside the container at
        // the same URL the browser uses, so JWKS fetches resolve to the host
        // gateway rather than the container itself.
        .WithContainerRuntimeArgs("--add-host", "auth.altered.local.gd:host-gateway")
        .WithEnvironment("APP_ENV", "dev")
        .WithEnvironment("APP_SECRET", "ead983759a87835cc8a76efda0a01149")
        .WithEnvironment("SERVER_NAME", ":80")
        .WithEnvironment("DEFAULT_URI", "http://collection.dev.localhost:8002")
        .WithEnvironment("DATABASE_URL", collectionDbUrl)
        .WithEnvironment("CORS_ALLOW_ORIGIN", "^https?://([a-z0-9-]+\\.)*(localhost|127\\.0\\.0\\.1|dev\\.localhost)(:[0-9]+)?$")
        .WithEnvironment("KEYCLOAK_BASE_URL", AuthUrl)
        .WithEnvironment("KEYCLOAK_REALM", "players")
        // CLIENT_ID/SECRET are unused by the JWKS-only validator; set for parity
        // with the app's .env so nothing looks misconfigured.
        .WithEnvironment("KEYCLOAK_CLIENT_ID", "altered-collection")
        .WithEnvironment("KEYCLOAK_CLIENT_SECRET", "dev-altered-collection-secret")
        .WithEnvironment("DEV_AUTH_ENABLED", "true")
        .WithEnvironment("ALTERED_CORE_URL", CardsProdUrl)
        // The bundled Caddyfile declares a mercure directive (unused here — no
        // Mercure bundle) that still needs its JWT keys present to boot.
        .WithEnvironment("MERCURE_PUBLISHER_JWT_KEY", "!ChangeThisMercureHubJWTSecretKey!")
        .WithEnvironment("MERCURE_SUBSCRIBER_JWT_KEY", "!ChangeThisMercureHubJWTSecretKey!")
        .WithEnvironment("MERCURE_PUBLISHER_JWT_ALG", "HS256")
        .WithEnvironment("MERCURE_SUBSCRIBER_JWT_ALG", "HS256")
        .WithReference(collectionDb)
        .WaitFor(collectionDb);

    // Group the Postgres server (and its hidden DB) under the API — see decks.
    collectionPg.WithParentRelationship(collectionApp.Resource);
}

// ===========================================================================
// ownership — AlteredOwnership (local). ASP.NET Core (.NET 10) SPA + minimal API,
// own Postgres + Redis (session ticket store / output cache). Built from the repo's
// build/Dockerfile `app` stage (the compiled, published server — no source bind
// mount). Runs with ASPNETCORE_ENVIRONMENT=Development so it (a) applies EF Core
// migrations on startup, (b) maps the /health endpoint, and (c) accepts plain-HTTP
// Keycloak metadata + http cookies (prod requires HTTPS). Auth is OIDC code flow
// via the confidential "ownership-frontend" client; like decks/website it reaches
// Keycloak server-side at the public *.local.gd URL, so it needs the host-gateway
// override. Cards metadata is read from prod (matches decks/collection).
// ===========================================================================
if (Enabled("ownership"))
{
    var ownershipRepo = Repo("AlteredOwnership");

    var ownershipPgPassword = builder.AddParameter("altered-ownership-db-password", "altereddev", secret: true)
        .WithInitialState(Hidden("Parameter")); // dev secret — keep it out of the graph
    var ownershipPg = builder.AddPostgres("altered-ownership-pg", password: ownershipPgPassword)
        .WithDataVolume("altered-ownership-pg-data");
    // The app expects ConnectionStrings__ownershipdb; the DB name is "ownershipdb"
    // (Program.cs / Aspire conventions). Hide the database node like the others.
    var ownershipDb = ownershipPg.AddDatabase("altered-ownership-db", "ownershipdb")
        .WithInitialState(Hidden("Database"));

    // Redis: session ticket store (RedisTicketStore) + output/distributed cache.
    // Ephemeral cache, so no data volume. The app reads ConnectionStrings__cache.
    var ownershipRedis = builder.AddRedis("altered-ownership-redis");

    var ownership = builder.AddDockerfile("altered-ownership", ownershipRepo, "build/Dockerfile", stage: "app")
        .WithHttpEndpoint(port: 8003, targetPort: 8080, name: "http")
        // Reach Keycloak (published on 0.0.0.0:18080) from inside the container at
        // the same URL the browser uses, so OIDC discovery / token / JWKS resolve to
        // the host gateway rather than the container itself.
        .WithContainerRuntimeArgs("--add-host", "auth.altered.local.gd:host-gateway")
        // Development: EF migrations on startup, /health mapped, http metadata + cookies.
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        // Connection strings, resolved by Aspire to the container-network host/port
        // (and generated credentials) at run time — no hardcoded URLs.
        .WithEnvironment("ConnectionStrings__ownershipdb", ownershipDb)
        .WithEnvironment("ConnectionStrings__cache", ownershipRedis)
        // OIDC code flow against the local realm via the confidential frontend client.
        .WithEnvironment("Keycloak__Authority", $"{AuthUrl}/realms/players")
        .WithEnvironment("Keycloak__ClientId", "ownership-frontend")
        .WithEnvironment("Keycloak__ClientSecret", "preprod-ownership-frontend-secret")
        // AuthBase is browser-facing (CSP form-action + surfaced to the SPA), so it
        // must be the public *.local.gd URL. ReunionWebBase is just the "back to the
        // community site" link → point at the local website. Cards stay on prod.
        .WithEnvironment("ExternalHosts__AuthBase", AuthUrl)
        .WithEnvironment("ExternalHosts__ReunionWebBase", "http://website.altered.local.gd:18181")
        .WithEnvironment("ExternalHosts__CardsApiBase", CardsProdUrl)
        .WaitFor(ownershipDb)
        .WaitFor(ownershipRedis)
        .WithHttpHealthCheck("/health")
        // Friendly dashboard link (resolves to 127.0.0.1 -> the Aspire proxy on
        // :8003); this host is among the client's seeded redirect URIs / web origins.
        .WithUrl("http://ownership.altered.local.gd:8003/", "ownership");

    // Start after Keycloak (server-side OIDC) when it's enabled.
    if (authApp is not null) ownership.WaitFor(authApp);

    // Group the Postgres server (and its hidden DB) + the Redis cache under the app
    // in the dashboard's Resources tree (and render the edges in the graph) — see decks.
    ownershipPg.WithParentRelationship(ownership.Resource);
    ownershipRedis.WithParentRelationship(ownership.Resource);
}

// ===========================================================================
// website — alteredcore-website (local). Plain PHP 7.4 + Apache, MariaDB, no
// build step. Wired to local Keycloak (the "main-site" client) and the local
// decks-api; cards/cdn/collection stay on prod (not local yet). The site reads
// plain define() constants from config.local.php (no env support), so the
// dev-environment owns that file and bind-mounts it over the user's checkout.
// ===========================================================================
if (Enabled("website"))
{
    var websiteRepo = Repo("alteredcore-website");

    // MariaDB for the website, mirroring the repo's docker-compose. The official
    // image entrypoint creates the alteredcore user+db from the MARIADB_* vars and
    // runs the repo's docker/init-db.sh (schema + migrations, prefix "dev_") on
    // first init only; data persists in a named volume (wipe the volume to
    // re-seed). We use the typed MySQL integration (so we get a real readiness
    // health check that gates WaitFor) pointed at the mariadb image. The
    // integration sets MYSQL_ROOT_PASSWORD from the parameter; the mariadb image
    // treats that as a compatibility alias. Browse it via DbGate (see below).
    var websiteDbPassword = builder.AddParameter("altered-website-db-password", "root", secret: true)
        .WithInitialState(Hidden("Parameter")); // dev secret — keep it out of the graph
    var websiteDb = builder.AddMySql("altered-website-db", websiteDbPassword)
        .WithImage("mariadb", "10.11")
        .WithEnvironment("MARIADB_DATABASE", "alteredcore")
        .WithEnvironment("MARIADB_USER", "alteredcore")
        .WithEnvironment("MARIADB_PASSWORD", "alteredcore")
        .WithEnvironment("DB_PREFIX", "dev_") // consumed by docker/init-db.sh
        .WithBindMount(Path.Combine(websiteRepo, "sql"), "/tmp/sql", isReadOnly: true)
        .WithBindMount(
            Path.Combine(websiteRepo, "docker", "init-db.sh"),
            "/docker-entrypoint-initdb.d/01-schema.sh",
            isReadOnly: true)
        .WithDataVolume("altered-website-db-data");

    // website container — built from the repo Dockerfile (php:7.4-apache), source
    // bind-mounted like the compose. config.local.php is bind-mounted from the
    // AppHost-managed dev config (overrides the user's checkout copy), which wires
    // DB + local Keycloak + local decks-api. Needs the same Keycloak host-gateway
    // override as decks: the server-side OAuth code/token exchange calls Keycloak
    // at the public *.local.gd URL, which would otherwise resolve to the container
    // itself. Browser → website goes through the Aspire proxy on :18181, and the
    // OAuth redirect_uri (built from the browser's Host) matches the redirect URIs
    // seeded for the "main-site" client.
    var website = builder.AddDockerfile("altered-website", websiteRepo)
        .WithBindMount(websiteRepo, "/var/www/html")
        .WithBindMount(
            Path.Combine(appHostDir, "website", "config.local.php"),
            "/var/www/html/config.local.php",
            isReadOnly: true)
        .WithHttpEndpoint(port: 18181, targetPort: 80, name: "http")
        .WithUrlForEndpoint("http", url =>
        {
            url.DisplayText = "website";
            url.Url = "/";
        })
        .WithContainerRuntimeArgs("--add-host", "auth.altered.local.gd:host-gateway")
        // Only wait for its OWN database: the website talks to Keycloak, decks-api
        // and collection-api server-side only when a request comes in (login, deck
        // calls, …), never at boot — so it can start in parallel with them rather
        // than being gated on their (slower) readiness.
        .WaitFor(websiteDb);

    // Group the MariaDB server under the website in the dashboard — see decks.
    websiteDb.WithParentRelationship(website.Resource);

    // Auto-seed alice as a website admin (mirrors the decks admin seed) so it
    // survives a DB reset / fresh start: an idempotent UPSERT keyed on her FIXED
    // Keycloak sub. Admin-panel access is gated on the user's GROUP
    // (user_groups.can_access_admin), so we put her in the "Admin" group (id 1,
    // seeded by schema.sql with all perms + can_access_admin) and set the is_admin
    // flag too, matching the bundled local "admin" account. Runs in-process from
    // the AppHost (no extra container, nothing masquerading as an app in the
    // dashboard) once the DB reports ready — by then the init script has created
    // the tables and the Admin group. ON DUPLICATE KEY keeps it a no-op once
    // alice's row exists (e.g. created by a real Keycloak login). The table name
    // carries the dev_ prefix this stack runs with (see website/config.local.php).
    const string websiteAdminSeedSql =
        "INSERT INTO dev_users (kc_sub, is_admin, group_id) " +
        "VALUES ('11111111-1111-1111-1111-111111111111', 1, 1) " +
        "ON DUPLICATE KEY UPDATE is_admin = 1, group_id = 1;";

    // Plugins to auto-activate at init, reproducing the admin "Activate" action
    // (admin/plugins.php): a row in {plugins} with is_active=1, plus the plugin's
    // install.sql run once for plugins that ship one. Done here (in-process,
    // idempotent) so a fresh/clean DB comes up with these enabled instead of
    // needing manual activation in /admin. core-altered-cards ships no SQL;
    // reunion-events ships sql/install.sql whose {table} placeholders resolve to
    // `dev_plugin_re_<table>` via the website's qp() helper (DB_PREFIX=dev_,
    // table_prefix=re) — reproduced below with a regex over the real file.
    const string cardsActivateSql =
        "INSERT INTO dev_plugins (id, is_active, version, activated_at) " +
        "VALUES ('core-altered-cards', 1, '1.0.0', NOW()) " +
        "ON DUPLICATE KEY UPDATE is_active = 1;";
    const string reunionActivateSql =
        "INSERT INTO dev_plugins (id, is_active, version, sql_installed_at, activated_at) " +
        "VALUES ('reunion-events', 1, '1.0.0', NOW(), NOW()) " +
        "ON DUPLICATE KEY UPDATE is_active = 1, sql_installed_at = COALESCE(sql_installed_at, NOW());";
    var reunionInstallSqlPath = Path.Combine(websiteRepo, "plugins", "reunion-events", "sql", "install.sql");

    websiteDb.OnResourceReady(async (_, _, ct) =>
    {
        // Resolved from the AppHost process, this points at the MariaDB container's
        // host-mapped port; append the app database (the server connection string
        // has no default schema).
        var connectionString = await websiteDb.Resource.ConnectionStringExpression.GetValueAsync(ct)
            + ";Database=alteredcore";

        // Build the statement list: admin + Altered Cards, then Reunion Events
        // (its install.sql first so the settings table exists before we mark it
        // installed). All statements are idempotent (UPSERT / CREATE IF NOT EXISTS).
        var statements = new List<string> { websiteAdminSeedSql, cardsActivateSql };
        if (File.Exists(reunionInstallSqlPath))
        {
            var reunionInstallSql = Regex.Replace(
                File.ReadAllText(reunionInstallSqlPath),
                @"\{([a-z_]+)\}",
                m => $"`dev_plugin_re_{m.Groups[1].Value}`");
            statements.Add(reunionInstallSql);
            statements.Add(reunionActivateSql);
        }
        else
        {
            // No install.sql found: still activate, but don't claim SQL was installed.
            statements.Add(
                "INSERT INTO dev_plugins (id, is_active, version, activated_at) " +
                "VALUES ('reunion-events', 1, '1.0.0', NOW()) ON DUPLICATE KEY UPDATE is_active = 1;");
            Console.WriteLine($"[altered] reunion-events install.sql not found at {reunionInstallSqlPath}; activated without it.");
        }

        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var conn = new MySqlConnection(connectionString);
                await conn.OpenAsync(ct);
                // NOTE: schema migrations are applied by the website CONTAINER's
                // entrypoint (docker/entrypoint.sh -> bin/migrate.php), not here. This
                // seed only inserts rows into base-schema tables.
                foreach (var sql in statements)
                {
                    await using var cmd = new MySqlCommand(sql, conn);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                Console.WriteLine("[altered] website seed applied (alice admin + plugins: core-altered-cards, reunion-events).");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    Console.WriteLine($"[altered] website seed FAILED after {maxAttempts} attempts ({ex.Message}).");
                    return;
                }
                Console.WriteLine($"[altered] website seed attempt {attempt}/{maxAttempts} failed ({ex.Message}); retrying in 3s...");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    });
}

// ===========================================================================
// uniques — uniques-search-api (local; the Altered-Re-Union fork of Taum/rust-cards-api).
// A Rust/Axum in-memory search engine over the Altered "Unique" cards. NOTE: this is
// NOT the prod cards API — it has its own
// contract (/api/v2/cards, /api/v2/card/{ref}, /api/v2/effects) and only covers
// Unique characters, so it does NOT replace ALTERED_CORE_URL/CARDS_API_URL. It's
// the simplest service here: no DB, no Keycloak, no seed, nothing in DbGate.
//
// The repo's root Dockerfile is PROD-only (a multi-stage Cloud Run build that bakes
// the binary), so the repo ships a separate DEV image at docker/dev/Dockerfile that we
// build and bind-mount the source into like decks/collection. Config is driven by
// env + the app's own default.toml (config.rs honours a PORT override; per-env tomls
// are optional, and default.toml already ships the right index path), so no custom
// toml is needed. The
// ~270 MB prebuilt index is downloaded once into a volume by the entrypoint (the
// binary loads it from disk and won't fetch it itself). The server binds
// 0.0.0.0:$PORT, so it's reachable on the Aspire network by its resource name —
// future consumers (website, decks-api) can point at http://altered-uniques-api:8080
// with no change here. CORS is already CorsLayer::permissive in the app's http.rs, so
// even browser-direct callers (the demo-ui) work out of the box.
// ===========================================================================
if (Enabled("uniques"))
{
    var uniquesRepo = Repo("uniques-search-api");

    var uniquesApi = builder.AddDockerfile("altered-uniques-api", Path.Combine(uniquesRepo, "docker", "dev"))
        .WithBindMount(uniquesRepo, "/app")
        // Dev formats wiring: a dev-env-owned local.toml bind-mounted over the checkout's
        // config/local.toml — enables [formats] (source = /app/formats = the repo's
        // committed formats/ dir) with hot-reload polling. Env alone can't set the reload
        // interval, only the toml can.
        .WithBindMount(Path.Combine(appHostDir, "uniques", "local.toml"),
            "/app/uniques-http-api/config/local.toml", isReadOnly: true)
        // target/ (cargo build cache) and build/ (the downloaded index) in
        // container-managed volumes — they shadow the host checkout's subpaths, so
        // the first build is slow then cached, and the index persists across
        // restarts. Same idea as decks/collection's vendor/ volume.
        .WithVolume("altered-uniques-api-target", "/app/target")
        .WithVolume("altered-uniques-index", "/app/build")
        .WithHttpEndpoint(port: 8003, targetPort: 8080, name: "http")
        // PORT is honoured by config.rs (no custom toml needed). The index path is left
        // to the app's default.toml (./build/full_index.tar.zst -> /app/build/...), which
        // the entrypoint downloads into — setting INDEX_PATH too would just duplicate it
        // and log a "prefer index.path in config" warning on every start.
        .WithEnvironment("PORT", "8080")
        // Dashboard link: a friendly *.local.gd host (resolves to 127.0.0.1 -> the
        // Aspire proxy on :8003) hitting a tiny sample query.
        .WithUrl("http://uniques.altered.local.gd:8003/api/v2/cards?limit=1", "cards (sample)");

    uniquesApiResource = uniquesApi.Resource;
}

// ===========================================================================
// uniques-ui — uniques-search-api/demo-ui (local). A Vite 6 + React 19 SPA demo for
// the uniques API, run via the Vite dev server (HMR). Browser-only: it calls the API
// directly (the API sets CorsLayer::permissive), so VITE_API_BASE_URL points at the
// browser-reachable API URL — no proxy, no CORS work. The repo ships the dev image at
// demo-ui/docker/dev/ (npm ci is baked at build time). demo-ui/ is bind-mounted for
// HMR; the node_modules volume is seeded from the image (the bind-mount would otherwise
// shadow the baked node_modules) — no runtime install.
//
// We publish Vite's port directly with -p (like Keycloak), NOT through the Aspire
// proxy, and run Vite on the same port we publish (8004) so the HMR websocket — which
// the browser opens to the page's own host:port — lines up. Open it at
// http://localhost:8004 (Vite's host allowlist permits localhost; the *.local.gd host
// would need server.allowedHosts). No WaitFor: the SPA needs no backend to start
// serving, and not blocking on the API's long first build keeps the UI available fast.
// ===========================================================================
if (Enabled("uniques-ui"))
{
    var uniquesUiRepo = Repo("uniques-search-api");

    // node_modules is baked into the image; this volume only un-shadows it under the
    // source bind-mount, seeded from the image. Key the volume name to a hash of
    // package-lock.json so a dependency change automatically gets a FRESH (re-seeded)
    // volume — no manual wipe. Stale volumes from older hashes are pruned (best-effort).
    var nmLock = Path.Combine(uniquesUiRepo, "demo-ui", "package-lock.json");
    var nmTag = File.Exists(nmLock)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(nmLock)))[..8].ToLowerInvariant()
        : "default";
    var nmVolume = $"altered-uniques-ui-node-modules-{nmTag}";
    PruneStaleVolumes("altered-uniques-ui-node-modules-", keep: nmVolume);

    var uniquesUi = builder.AddDockerfile("altered-uniques-ui", Path.Combine(uniquesUiRepo, "demo-ui"), "docker/dev/Dockerfile")
        .WithBindMount(Path.Combine(uniquesUiRepo, "demo-ui"), "/app")
        .WithVolume(nmVolume, "/app/node_modules")
        // Publish Vite on 0.0.0.0:8004 directly (bypass the Aspire proxy) so the HMR
        // websocket works; internal port == published port so the HMR client lines up.
        .WithContainerRuntimeArgs("-p", "0.0.0.0:8004:8004")
        // Must be reachable FROM THE BROWSER (not the Aspire alias). Vite reads VITE_-
        // prefixed vars from the environment (see demo-ui/README).
        .WithEnvironment("VITE_API_BASE_URL", "http://localhost:8003")
        .WithUrl("http://localhost:8004/", "demo-ui");

    // Express the runtime (browser-side) dependency on the API as a graph edge ONLY —
    // no startup gating (same mechanism DbGate uses for the DBs). Null if uniques is off.
    if (uniquesApiResource is not null) uniquesUi.WithReferenceRelationship(uniquesApiResource);
}

// ===========================================================================
// dbgate — one web DB client for ALL project databases (replaces the per-service
// phpMyAdmin). It reaches each DB container over the Aspire network by its
// resource-name alias (internal ports), so no host ports need publishing on the
// DBs themselves. Connections are pre-seeded read-only via env vars; only the
// enabled services' DBs are added. Dev credentials match each DB resource.
// ===========================================================================
if (Enabled("decks") || Enabled("collection") || Enabled("ownership") || Enabled("website"))
{
    var dbgateConnections = new List<string>();
    var dbgate = builder.AddContainer("altered-dbgate", "dbgate/dbgate")
        .WithHttpEndpoint(port: 18182, targetPort: 3000, name: "http")
        .WithVolume("altered-dbgate-data", "/root/.dbgate"); // persist queries/history

    if (Enabled("decks"))
    {
        dbgateConnections.Add("decks");
        dbgate.WithEnvironment("LABEL_decks", "decks (postgres)")
            .WithEnvironment("SERVER_decks", "altered-decks-pg")
            .WithEnvironment("PORT_decks", "5432")
            .WithEnvironment("USER_decks", "postgres")
            .WithEnvironment("PASSWORD_decks", "altereddev")
            .WithEnvironment("DATABASE_decks", "altered_deck")
            .WithEnvironment("ENGINE_decks", "postgres@dbgate-plugin-postgres");
    }

    if (Enabled("collection"))
    {
        dbgateConnections.Add("collection");
        dbgate.WithEnvironment("LABEL_collection", "collection (postgres)")
            .WithEnvironment("SERVER_collection", "altered-collection-pg")
            .WithEnvironment("PORT_collection", "5432")
            .WithEnvironment("USER_collection", "postgres")
            .WithEnvironment("PASSWORD_collection", "altereddev")
            .WithEnvironment("DATABASE_collection", "altered_collection")
            .WithEnvironment("ENGINE_collection", "postgres@dbgate-plugin-postgres");
    }

    if (Enabled("ownership"))
    {
        dbgateConnections.Add("ownership");
        dbgate.WithEnvironment("LABEL_ownership", "ownership (postgres)")
            .WithEnvironment("SERVER_ownership", "altered-ownership-pg")
            .WithEnvironment("PORT_ownership", "5432")
            .WithEnvironment("USER_ownership", "postgres")
            .WithEnvironment("PASSWORD_ownership", "altereddev")
            .WithEnvironment("DATABASE_ownership", "ownershipdb")
            .WithEnvironment("ENGINE_ownership", "postgres@dbgate-plugin-postgres");
    }

    if (Enabled("website"))
    {
        dbgateConnections.Add("website");
        dbgate.WithEnvironment("LABEL_website", "website (mariadb)")
            .WithEnvironment("SERVER_website", "altered-website-db")
            .WithEnvironment("PORT_website", "3306")
            .WithEnvironment("USER_website", "root")
            .WithEnvironment("PASSWORD_website", "root")
            .WithEnvironment("DATABASE_website", "alteredcore")
            .WithEnvironment("ENGINE_website", "mariadb@dbgate-plugin-mysql");
    }

    // DbGate connects to each DB via its own CONNECTIONS env (which Aspire can't
    // infer), so it has no inferred edges. We deliberately DON'T add reference
    // relationships to the DB servers: that kept it an isolated node cluttering the
    // graph. It stays a standalone (root-level) entry in the Resources tab — it's a
    // cross-cutting tool, not owned by any one app.
    dbgate
        .WithEnvironment("CONNECTIONS", string.Join(",", dbgateConnections));
}

builder.Build().Run();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// Absolute directory containing this apphost.cs file (compile-time path).
static string AppHostDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

// Best-effort cleanup: remove docker volumes whose name starts with `prefix` except
// `keep` — i.e. stale node_modules volumes from previous package-lock hashes. Old-hash
// volumes aren't used by any running container, so removal is safe; any failure (docker
// not ready, or a volume still in use) is ignored so it never blocks startup.
static void PruneStaleVolumes(string prefix, string keep)
{
    try
    {
        using var list = Process.Start(new ProcessStartInfo("docker", $"volume ls -q --filter name={prefix}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (list is null) return;
        var names = list.StandardOutput.ReadToEnd();
        list.WaitForExit();
        foreach (var name in names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name == keep || !name.StartsWith(prefix)) continue;
            try
            {
                using var rm = Process.Start(new ProcessStartInfo("docker", $"volume rm {name}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                rm?.WaitForExit();
                if (rm?.ExitCode == 0) Console.WriteLine($"[altered] pruned stale node_modules volume {name}");
            }
            catch { /* in use or already gone — ignore */ }
        }
    }
    catch { /* docker unavailable — ignore */ }
}

// A presentational-only initial snapshot that keeps a resource OUT of the dashboard
// (graph + table) without changing its behaviour — used for the dev DB passwords and
// the auto-created database nodes. Aspire updates snapshots with record `with`
// expressions, so IsHidden set here persists through the resource lifecycle.
static CustomResourceSnapshot Hidden(string resourceType) =>
    new() { ResourceType = resourceType, Properties = [], IsHidden = true };

// Resolve a repo path, cloning it from GitHub if the local checkout is missing.
string Repo(string name)
{
    var path = Path.Combine(reposDir, name);
    if (Directory.Exists(path))
    {
        return path;
    }

    if (!repoUrls.TryGetValue(name, out var url))
    {
        throw new InvalidOperationException($"Unknown repo '{name}' and no local checkout at {path}.");
    }

    Console.WriteLine($"[altered] cloning {name} -> {path}");
    Directory.CreateDirectory(reposDir);
    var psi = new ProcessStartInfo("git", $"clone --depth 1 {url} \"{path}\"")
    {
        UseShellExecute = false,
        WorkingDirectory = reposDir,
    };
    using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
    proc.WaitForExit();
    if (proc.ExitCode != 0)
    {
        throw new InvalidOperationException($"git clone failed for {name} (exit {proc.ExitCode}).");
    }

    return path;
}
