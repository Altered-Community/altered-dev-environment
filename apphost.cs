#:sdk Aspire.AppHost.Sdk@13.3.5
#:package Aspire.Hosting.PostgreSQL@13.3.5
#:package Aspire.Hosting.MySql@13.3.5
#:package Npgsql@10.0.3
#:package MySqlConnector@2.4.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    ["alteredcore-website"] = "https://github.com/Altered-Community/alteredcore-website.git",
    ["altered-deckbuilder-poc-v2"] = "https://github.com/Altered-Community/altered-deckbuilder-poc-v2.git",
};

// Handles to cross-referenced resources, assigned as each service is mounted, so
// later services can WaitFor them only when their dependency is actually enabled.
IResourceBuilder<ContainerResource>? authApp = null;
IResourceBuilder<ContainerResource>? decksApp = null;
IResourceBuilder<ContainerResource>? collectionApp = null;

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
        .WithEnvironment("KC_DB", "dev-file")               // H2; the image bakes KC_DB=postgres for prod
        .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
        .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
        .WithEnvironment("KC_HTTP_ENABLED", "true")
        .WithEnvironment("KC_HEALTH_ENABLED", "true")
        .WithEnvironment("KC_HOSTNAME", AuthUrl)
        .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true")
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
        // Dashboard links (we publish via -p, so Aspire has no endpoint to show).
        .WithUrl($"{AuthUrl}/realms/players/account/", "Keycloak account")
        .WithUrl($"{AuthUrl}/admin/", "Keycloak admin console");
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

    var decksPgPassword = builder.AddParameter("altered-decks-db-password", "altereddev", secret: true);
    var decksPg = builder.AddPostgres("altered-decks-pg", password: decksPgPassword)
        .WithDataVolume("altered-decks-pg-data"); // persist the DB across restarts
    var decksDb = decksPg.AddDatabase("altered-deck", "altered_deck");

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
        .WithUrl("http://decks.altered.local.gd:8001/", "decks-api")
        .WithUrl("http://decks.altered.local.gd:8001/admin/login", "decks admin");

    decksApp = decksApi;

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

    var collectionPgPassword = builder.AddParameter("altered-collection-db-password", "altereddev", secret: true);
    var collectionPg = builder.AddPostgres("altered-collection-pg", password: collectionPgPassword)
        .WithDataVolume("altered-collection-pg-data");
    var collectionDb = collectionPg.AddDatabase("altered-collection", "altered_collection");

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
        .WaitFor(collectionDb)
        .WithUrl("http://collection.altered.local.gd:8002/api", "collection-api")
        .WithUrl("http://collection.altered.local.gd:8002/api/docs", "collection docs");
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
    // health check that gates WaitFor + a one-call phpMyAdmin) pointed at the
    // mariadb image. The integration sets MYSQL_ROOT_PASSWORD from the parameter;
    // the mariadb image treats that as a compatibility alias.
    var websiteDbPassword = builder.AddParameter("altered-website-db-password", "root", secret: true);
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
        .WithDataVolume("altered-website-db-data")
        .WithPhpMyAdmin(pma => pma.WithHostPort(18182));

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
        .WithContainerRuntimeArgs("--add-host", "auth.altered.local.gd:host-gateway")
        .WaitFor(websiteDb)
        .WithUrl("http://website.altered.local.gd:18181/", "website")
        .WithUrl("http://website.altered.local.gd:18181/pages/login", "website login");

    // Start after Keycloak (server-side OAuth) and decks-api (server-side deck
    // calls) when those services are enabled.
    if (authApp is not null) website.WaitFor(authApp);
    if (decksApp is not null) website.WaitFor(decksApp);
    if (collectionApp is not null) website.WaitFor(collectionApp);

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

    websiteDb.OnResourceReady(async (_, _, ct) =>
    {
        // Resolved from the AppHost process, this points at the MariaDB container's
        // host-mapped port; append the app database (the server connection string
        // has no default schema).
        var connectionString = await websiteDb.Resource.ConnectionStringExpression.GetValueAsync(ct)
            + ";Database=alteredcore";

        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var conn = new MySqlConnection(connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new MySqlCommand(websiteAdminSeedSql, conn);
                await cmd.ExecuteNonQueryAsync(ct);
                Console.WriteLine("[altered] website admin seed applied (alice).");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    Console.WriteLine($"[altered] website admin seed FAILED after {maxAttempts} attempts ({ex.Message}).");
                    return;
                }
                Console.WriteLine($"[altered] website admin seed attempt {attempt}/{maxAttempts} failed ({ex.Message}); retrying in 3s...");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    });
}

builder.Build().Run();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// Absolute directory containing this apphost.cs file (compile-time path).
static string AppHostDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

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
