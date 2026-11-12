using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vaultbar.Vault;

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
                var root = JsonNode.Parse(fs) as JsonObject ?? throw new StoreException(VaultError.DataEmpty);
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
                  ?? throw new StoreException(VaultError.NoAccount);
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
            throw new StoreException(VaultError.NoMasterPassword);
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
        var env = U("environment_environment") ?? root["global_environment_environment"];

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
            IconsBaseUrl = IconsUrl(env),
            WebVaultUrl = WebVaultUrl(env),
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
        if (n is null) throw new StoreException(VaultError.KdfMissing);
        var kdf = new KdfConfig((KdfType)(Int(n["kdfType"]) ?? 0), Int(n["iterations"]) ?? 600000, Int(n["memory"]), Int(n["parallelism"]));
        var valid = kdf.Type switch
        {
            KdfType.Pbkdf2 => kdf.Iterations is >= 5_000 and <= 2_000_000,
            KdfType.Argon2id => kdf.Iterations is >= 2 and <= 10 && kdf.MemoryMiB is null or (>= 16 and <= 1024)
                                && kdf.Parallelism is null or (>= 1 and <= 16),
            _ => throw new StoreException(VaultError.KdfUnsupported),
        };
        if (!valid) throw new StoreException(VaultError.KdfOutOfRange);
        return kdf;
    }

    private static string WebVaultUrl(JsonNode? env)
    {
        var urls = env?["urls"];
        if (Str(urls?["webVault"]) is { Length: > 0 } web) return web.TrimEnd('/');
        if (Str(urls?["base"]) is { Length: > 0 } baseUrl) return baseUrl.TrimEnd('/');
        return Str(env?["region"]) == "EU" ? "https://vault.bitwarden.eu" : "https://vault.bitwarden.com";
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
