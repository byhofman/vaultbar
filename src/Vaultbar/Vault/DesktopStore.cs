using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vaultbar.Vault;

public sealed class StoreException(string message) : Exception(message);

public sealed class StoreSnapshot
{
    public required string Path { get; init; }
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required string Salt { get; init; }
    public required KdfConfig Kdf { get; init; }
    public required string WrappedUserKey { get; init; }
    public string? EncPrivateKey { get; init; }
    public Dictionary<string, string> OrgKeys { get; init; } = new();
    public JsonObject Ciphers { get; init; } = new();
    public JsonObject Folders { get; init; } = new();
    public DateTimeOffset? LastSync { get; init; }
    public bool ShowFavicons { get; init; } = true;
    public string IconsBaseUrl { get; init; } = "https://icons.bitwarden.net";
    public string? VaultTimeout { get; init; }
    public string ContentHash { get; init; } = "";
}

/// <summary>Reads the Bitwarden desktop app's local (encrypted) state file.</summary>
public static class DesktopStore
{
    public static IEnumerable<string> CandidatePaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Environment.GetEnvironmentVariable("BITWARDEN_APPDATA_DIR") is { Length: > 0 } custom)
            yield return Path.Combine(custom, "data.json");

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bitwarden", "data.json");
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(packages))
                foreach (var dir in Directory.EnumerateDirectories(packages, "8bitSolutionsLLC.bitwardendesktop_*"))
                    yield return Path.Combine(dir, "LocalCache", "Roaming", "Bitwarden", "data.json");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Bitwarden", "data.json");
            yield return Path.Combine(home, "Library", "Containers", "com.bitwarden.desktop", "Data", "Library", "Application Support", "Bitwarden", "data.json");
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
            yield return Path.Combine(xdg, "Bitwarden", "data.json");
            yield return Path.Combine(home, "snap", "bitwarden", "current", ".config", "Bitwarden", "data.json");
            yield return Path.Combine(home, ".var", "app", "com.bitwarden.desktop", "config", "Bitwarden", "data.json");
        }
    }

    public static string? Locate(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? overridePath : null;
        return CandidatePaths()
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static StoreSnapshot Read(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var root = JsonNode.Parse(fs) as JsonObject ?? throw new StoreException("Bitwarden data file is empty.");
                return Parse(root, path);
            }
            catch (Exception ex) when (attempt < 5 && ex is JsonException or IOException)
            {
                // Bitwarden rewrites the file frequently; a read can race a write.
                Thread.Sleep(150);
            }
        }
    }

    private static StoreSnapshot Parse(JsonObject root, string path)
    {
        var uid = Str(root["global_account_activeAccountId"])
                  ?? throw new StoreException("No account found. Log in to the Bitwarden desktop app once.");
        var email = Str(root["global_account_accounts"]?[uid]?["email"]) ?? "";
        JsonNode? U(string key) => root[$"user_{uid}_{key}"];

        string salt, wrapped;
        KdfConfig kdf;
        var unlock = U("masterPasswordUnlock_masterPasswordUnlockKey");
        if (Str(unlock?["masterKeyWrappedUserKey"]) is { } w)
        {
            wrapped = w;
            salt = Str(unlock!["salt"]) ?? email.Trim().ToLowerInvariant();
            kdf = ParseKdf(unlock["kdf"]);
        }
        else if (Str(U("masterPassword_masterKeyEncryptedUserKey")) is { } legacy)
        {
            wrapped = legacy;
            salt = email.Trim().ToLowerInvariant();
            kdf = ParseKdf(U("kdfConfig_kdfConfig"));
        }
        else
        {
            throw new StoreException("This account has no master-password unlock data (logged out, or SSO/key-connector only).");
        }

        var crypto = U("crypto_accountCryptographicState");
        var privateKey = Str(crypto?["V1"]?["private_key"]) ?? Str(crypto?["V2"]?["private_key"]) ?? Str(U("crypto_privateKey"));

        var orgKeys = new Dictionary<string, string>();
        if (U("crypto_organizationKeys") is JsonObject orgs)
        {
            foreach (var (orgId, node) in orgs)
            {
                var key = node is JsonObject o
                    ? (Str(o["type"]) is null or "organization" ? Str(o["key"]) : null)
                    : Str(node);
                if (key != null) orgKeys[orgId] = key;
            }
        }

        var ciphers = U("ciphers_ciphers") as JsonObject ?? new JsonObject();
        var folders = U("folder_folders") as JsonObject ?? new JsonObject();

        return new StoreSnapshot
        {
            Path = path,
            UserId = uid,
            Email = email,
            Salt = salt,
            Kdf = kdf,
            WrappedUserKey = wrapped,
            EncPrivateKey = privateKey,
            OrgKeys = orgKeys,
            Ciphers = ciphers,
            Folders = folders,
            LastSync = DateTimeOffset.TryParse(Str(U("sync_lastSync")), out var ls) ? ls : null,
            ShowFavicons = root["global_domainSettings_showFavicons"] is not JsonValue sf || !sf.TryGetValue<bool>(out var show) || show,
            IconsBaseUrl = IconsUrl(U("environment_environment") ?? root["global_environment_environment"]),
            VaultTimeout = U("vaultTimeoutSettings_vaultTimeout") switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v => v.ToJsonString(),
                _ => null,
            },
            ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(ciphers.ToJsonString() + folders.ToJsonString() + string.Join(",", orgKeys.Values)))),
        };
    }

    private static KdfConfig ParseKdf(JsonNode? n)
    {
        if (n is null) throw new StoreException("KDF settings missing from Bitwarden data.");
        var type = (KdfType)(Int(n["kdfType"]) ?? 0);
        return new KdfConfig(type, Int(n["iterations"]) ?? 600000, Int(n["memory"]), Int(n["parallelism"]));
    }

    private static string IconsUrl(JsonNode? env)
    {
        var urls = env?["urls"];
        if (Str(urls?["icons"]) is { Length: > 0 } icons) return icons.TrimEnd('/');
        if (Str(urls?["base"]) is { Length: > 0 } baseUrl) return baseUrl.TrimEnd('/') + "/icons";
        return Str(env?["region"]) == "EU" ? "https://icons.bitwarden.eu" : "https://icons.bitwarden.net";
    }

    internal static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
