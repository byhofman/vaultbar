using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Vaultbar.Vault;

namespace Vaultbar.Tests;

/// <summary>Builds a Bitwarden-desktop-shaped data.json encrypted the same way the real client does.</summary>
public sealed class VaultFixture
{
    public const string Email = "alex@example.com";
    public const string UserId = "9a466c6e-0000-4000-8000-000000000001";
    private readonly SymmetricKey _userKey = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
    private readonly JsonObject _ciphers = new();
    private readonly JsonObject _folders = new();
    private readonly JsonObject _orgKeys = new();
    private readonly Dictionary<string, SymmetricKey> _orgs = new();
    private readonly RSA _accountRsa = RSA.Create(2048);

    /// <summary>Where the private key lives: the current account state, or the older flat key.</summary>
    public bool LegacyPrivateKeySlot { get; set; }

    /// <summary>Replaces the private key with a value that does not decrypt, to test a damaged account.</summary>
    public string? PrivateKeyOverride { get; set; }

    public string AddOrganization()
    {
        var orgId = Guid.NewGuid().ToString();
        var raw = RandomNumberGenerator.GetBytes(64);
        _orgs[orgId] = SymmetricKey.FromBytes(raw);
        _orgKeys[orgId] = new JsonObject { ["type"] = "organization", ["key"] = WrapForAccount(raw) };
        return orgId;
    }

    /// <summary>The older store shape: the organisation key as a bare string.</summary>
    public string AddOrganizationBareKey()
    {
        var orgId = AddOrganization();
        _orgKeys[orgId] = _orgKeys[orgId]!["key"]!.GetValue<string>();
        return orgId;
    }

    /// <summary>A provider-managed organisation: its key is wrapped with a provider key Vaultbar does not read.</summary>
    public string AddProviderOrganization()
    {
        var orgId = Guid.NewGuid().ToString();
        using var providerKey = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
        var raw = RandomNumberGenerator.GetBytes(64);
        _orgs[orgId] = SymmetricKey.FromBytes(raw);
        _orgKeys[orgId] = new JsonObject { ["type"] = "provider", ["key"] = EncString.Encrypt(raw, providerKey), ["providerId"] = Guid.NewGuid().ToString() };
        return orgId;
    }

    /// <summary>A key this account cannot open, as after a membership was re-keyed for another device's key.</summary>
    public string AddOrganizationWrappedForSomeoneElse()
    {
        var orgId = Guid.NewGuid().ToString();
        using var other = RSA.Create(2048);
        var raw = RandomNumberGenerator.GetBytes(64);
        _orgs[orgId] = SymmetricKey.FromBytes(raw);
        _orgKeys[orgId] = new JsonObject { ["type"] = "organization", ["key"] = $"4.{Convert.ToBase64String(other.Encrypt(raw, RSAEncryptionPadding.OaepSHA1))}" };
        return orgId;
    }

    public string AddOrgLogin(string orgId, string name, string username, string password, bool itemKey = false, bool reprompt = false)
    {
        var id = AddCipher(_orgs[orgId], name, username, password, itemKey, reprompt);
        _ciphers[id]!["organizationId"] = orgId;
        return id;
    }

    private string WrapForAccount(byte[] raw) => $"4.{Convert.ToBase64String(_accountRsa.Encrypt(raw, RSAEncryptionPadding.OaepSHA1))}";

    private string AddCipher(SymmetricKey baseKey, string name, string username, string password, bool itemKey, bool reprompt)
    {
        var id = Guid.NewGuid().ToString();
        var key = baseKey;
        string? encItemKey = null;
        if (itemKey)
        {
            var raw = RandomNumberGenerator.GetBytes(64);
            encItemKey = EncString.Encrypt(raw, baseKey);
            key = SymmetricKey.FromBytes(raw);
        }
        _ciphers[id] = new JsonObject
        {
            ["id"] = id,
            ["type"] = 1,
            ["name"] = Enc(name, key),
            ["favorite"] = false,
            ["reprompt"] = reprompt ? 1 : 0,
            ["key"] = encItemKey,
            ["deletedDate"] = null,
            ["login"] = new JsonObject
            {
                ["username"] = Enc(username, key),
                ["password"] = Enc(password, key),
                ["uris"] = new JsonArray(),
            },
        };
        return id;
    }

    public string AddFolder(string name)
    {
        var id = Guid.NewGuid().ToString();
        _folders[id] = new JsonObject { ["id"] = id, ["name"] = Enc(name, _userKey) };
        return id;
    }

    public string AddLogin(string name, string? username, string? password, string[]? uris = null, string? totp = null,
        bool favorite = false, bool reprompt = false, bool itemKey = false, string? folderId = null, string? notes = null)
    {
        var id = Guid.NewGuid().ToString();
        var key = _userKey;
        string? encItemKey = null;
        if (itemKey)
        {
            var raw = RandomNumberGenerator.GetBytes(64);
            encItemKey = EncString.Encrypt(raw, _userKey);
            key = SymmetricKey.FromBytes(raw);
        }

        _ciphers[id] = new JsonObject
        {
            ["id"] = id,
            ["type"] = 1,
            ["name"] = Enc(name, key),
            ["favorite"] = favorite,
            ["reprompt"] = reprompt ? 1 : 0,
            ["organizationId"] = null,
            ["folderId"] = folderId,
            ["key"] = encItemKey,
            ["notes"] = Enc(notes, key),
            ["deletedDate"] = null,
            ["login"] = new JsonObject
            {
                ["username"] = Enc(username, key),
                ["password"] = Enc(password, key),
                ["totp"] = Enc(totp, key),
                ["uris"] = new JsonArray((uris ?? []).Select(u => (JsonNode)new JsonObject { ["uri"] = Enc(u, key), ["match"] = null }).ToArray()),
            },
            ["fields"] = new JsonArray(new JsonObject { ["name"] = Enc("Recovery PIN", key), ["value"] = Enc("4711", key), ["type"] = 1 }),
        };
        return id;
    }

    public string AddCard(string name, string brand, string number, string code)
    {
        var id = Guid.NewGuid().ToString();
        _ciphers[id] = new JsonObject
        {
            ["id"] = id,
            ["type"] = 3,
            ["name"] = Enc(name, _userKey),
            ["favorite"] = false,
            ["reprompt"] = 0,
            ["card"] = new JsonObject
            {
                ["cardholderName"] = Enc("Alex Example", _userKey),
                ["brand"] = Enc(brand, _userKey),
                ["number"] = Enc(number, _userKey),
                ["expMonth"] = Enc("4", _userKey),
                ["expYear"] = Enc("2029", _userKey),
                ["code"] = Enc(code, _userKey),
            },
        };
        return id;
    }

    public string AddNote(string name, string text)
    {
        var id = Guid.NewGuid().ToString();
        _ciphers[id] = new JsonObject
        {
            ["id"] = id,
            ["type"] = 2,
            ["name"] = Enc(name, _userKey),
            ["notes"] = Enc(text, _userKey),
            ["secureNote"] = new JsonObject { ["type"] = 0 },
        };
        return id;
    }

    public void AddDeleted(string name)
    {
        var id = Guid.NewGuid().ToString();
        _ciphers[id] = new JsonObject { ["id"] = id, ["type"] = 2, ["name"] = Enc(name, _userKey), ["deletedDate"] = "2026-01-01T00:00:00Z" };
    }

    public void Write(string path, string password, int iterations = 5000)
    {
        var masterKey = MasterKey.Derive(password, Email, new KdfConfig(KdfType.Pbkdf2, iterations, null, null));
        using var stretched = MasterKey.Stretch(masterKey);
        var userKeyBytes = _userKey.Enc.Concat(_userKey.Mac!).ToArray();
        const string uid = UserId;

        var root = new JsonObject
        {
            ["global_account_activeAccountId"] = uid,
            ["global_account_accounts"] = new JsonObject { [uid] = new JsonObject { ["email"] = Email } },
            [$"user_{uid}_masterPasswordUnlock_masterPasswordUnlockKey"] = new JsonObject
            {
                ["salt"] = Email,
                ["kdf"] = new JsonObject { ["kdfType"] = 0, ["iterations"] = iterations },
                ["masterKeyWrappedUserKey"] = EncString.Encrypt(userKeyBytes, stretched),
            },
            [$"user_{uid}_ciphers_ciphers"] = _ciphers.DeepClone(),
            [$"user_{uid}_folder_folders"] = _folders.DeepClone(),
            [$"user_{uid}_sync_lastSync"] = DateTimeOffset.UtcNow.AddMinutes(-3).ToString("O"),
            [$"user_{uid}_vaultTimeoutSettings_vaultTimeout"] = 15,
        };
        if (_orgKeys.Count > 0)
        {
            var privateKey = PrivateKeyOverride ?? EncString.Encrypt(_accountRsa.ExportPkcs8PrivateKey(), _userKey);
            root[$"user_{uid}_crypto_organizationKeys"] = _orgKeys.DeepClone();
            if (LegacyPrivateKeySlot) root[$"user_{uid}_crypto_privateKey"] = privateKey;
            else root[$"user_{uid}_crypto_accountCryptographicState"] = new JsonObject { ["V1"] = new JsonObject { ["private_key"] = privateKey } };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, root.ToJsonString());
    }

    private static string? Enc(string? value, SymmetricKey key) => value is null ? null : EncString.Encrypt(value, key);

    public static VaultFixture Demo()
    {
        var f = new VaultFixture();
        var work = f.AddFolder("Work");
        var personal = f.AddFolder("Personal");
        f.AddLogin("GitHub", "alex-example", "gh-Secr3t!pass", ["https://github.com/login"], "JBSWY3DPEHPK3PXP", favorite: true, folderId: work);
        f.AddLogin("Azure Portal", "alex@contoso.com", "Azure#2026", ["https://portal.azure.com"], folderId: work, itemKey: true);
        f.AddLogin("Azure DevOps", "alex@contoso.com", "Ado#2026", ["https://dev.azure.com/contoso"], folderId: work);
        f.AddLogin("Microsoft 365", "alex@contoso.com", "M365-pass", ["https://login.microsoftonline.com"], "otpauth://totp/M365?secret=JBSWY3DPEHPK3PXP&digits=6&period=30", favorite: true, folderId: work);
        f.AddLogin("Gmail", "alex.example@gmail.com", "gmail-pass", ["https://accounts.google.com"], folderId: personal);
        f.AddLogin("Netflix", "alex.example@gmail.com", "nflx-pass", ["netflix.com"], folderId: personal);
        f.AddLogin("Bank of Example", "alex123", "B4nk-vault", ["https://www.bankofexample.co.uk/login"], reprompt: true, favorite: true, folderId: personal);
        f.AddLogin("Router admin", "admin", "router-pw", ["http://192.168.1.1"], notes: "Firmware 3.2");
        f.AddLogin("Slack", "alex@contoso.com", "slack-pw", ["https://contoso.slack.com"], folderId: work);
        f.AddLogin("Docker Hub", "alexexample", "docker-pw", ["https://hub.docker.com"]);
        f.AddLogin("AWS Console", "alex", "aws-pw", ["https://signin.aws.amazon.com"], "JBSWY3DPEHPK3PXP");
        f.AddLogin("Stack Overflow", "alex@contoso.com", "so-pw", ["https://stackoverflow.com"]);
        f.AddLogin("Linear", "alex@contoso.com", null, ["https://linear.app"]);
        f.AddLogin("Café Wi-Fi portal", "gäst", "pässwörd€ñ", ["https://wifi.example.net"]);
        f.AddCard("Visa Gold", "Visa", "4111111111111111", "123");
        f.AddNote("Wi-Fi at home", "SSID: Example-5G\nPassword: correct horse battery staple");
        f.AddDeleted("Old account");
        return f;
    }
}
