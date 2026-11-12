using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static Vaultbar.Vault.DesktopStore;

namespace Vaultbar.Vault;

public enum ItemType
{
    Login = 1,
    SecureNote = 2,
    Card = 3,
    Identity = 4,
    SshKey = 5,
}

public enum FieldKind
{
    Text,
    Secret,
    Totp,
    Url,
    Multiline,
}

public sealed record ItemField(string Label, FieldKind Kind, Func<string?> Value, Func<Totp.Code?>? TotpCode = null);

public sealed record CopyAction(string Label, Func<string?> Value, bool Sensitive);

/// <summary>
/// A vault item whose searchable fields (name, username, URIs) are decrypted up front.
/// Secrets stay encrypted until an action asks for them.
/// </summary>
public sealed class VaultItem
{
    private readonly JsonObject _raw;
    private readonly SymmetricKey _key;

    internal VaultItem(JsonObject raw, SymmetricKey key, string? folder)
    {
        _raw = raw;
        _key = key;
        Id = Str(raw["id"]) ?? Guid.NewGuid().ToString();
        Type = (ItemType)(Int(raw["type"]) ?? 0);
        Name = D(raw["name"]) ?? "(no name)";
        Folder = folder;
        Favorite = raw["favorite"] is JsonValue f && f.TryGetValue<bool>(out var fav) && fav;
        Reprompt = Int(raw["reprompt"]) == 1;

        switch (Type)
        {
            case ItemType.Login:
                Username = D(Login?["username"]) ?? "";
                Uris = (Login?["uris"] as JsonArray ?? [])
                    .Select(u => D(u?["uri"]))
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u!)
                    .ToList();
                HasTotp = Str(Login?["totp"]) != null;
                HasPassword = Str(Login?["password"]) != null;
                Subtitle = Username.Length > 0 ? Username : Uris.Select(HostOf).FirstOrDefault(h => h != null) ?? "";
                break;
            case ItemType.Card:
                var brand = D(Card?["brand"]);
                Username = D(Card?["cardholderName"]) ?? "";
                var last4 = LastFour(Card?["number"]) is { } digits ? "*" + digits : null;
                Subtitle = string.Join(", ", new[] { brand, last4 }.Where(s => !string.IsNullOrEmpty(s)));
                break;
            case ItemType.Identity:
                var id = Identity;
                Username = D(id?["username"]) ?? D(id?["email"]) ?? "";
                Subtitle = string.Join(" ", new[] { D(id?["firstName"]), D(id?["lastName"]) }.Where(s => !string.IsNullOrEmpty(s)));
                break;
            case ItemType.SshKey:
                Subtitle = D(Ssh?["keyFingerprint"]) ?? "SSH key";
                break;
            default:
                Subtitle = folder ?? "Secure note";
                break;
        }

        Hosts = Uris.Select(HostOf).Where(h => h != null).Select(h => h!).Distinct().ToList();
        NameLower = Name.ToLowerInvariant();
        UserLower = Username.ToLowerInvariant();
        FolderLower = folder?.ToLowerInvariant() ?? "";
    }

    public string Id { get; }
    public ItemType Type { get; }
    public string Name { get; }
    public string Username { get; } = "";
    public string Subtitle { get; } = "";
    public string? Folder { get; }
    public bool Favorite { get; }
    public bool Reprompt { get; }
    public bool HasTotp { get; }
    public bool HasPassword { get; }
    public IReadOnlyList<string> Uris { get; } = [];
    public IReadOnlyList<string> Hosts { get; }

    internal string NameLower { get; }
    internal string UserLower { get; }
    internal string FolderLower { get; }

    public string? PrimaryHost => Hosts.FirstOrDefault();

    public string? IconHost => Hosts.FirstOrDefault(IsPublicHost);

    public WebAddress? LaunchUrl => Uris.Select(WebAddress.From).FirstOrDefault(u => u != null);

    private static readonly string[] PrivateSuffixes = ["localhost", "local", "lan", "internal", "intranet", "corp", "home", "home.arpa", "localdomain"];

    /// <summary>False for hosts that only exist on a local network; their names must not be sent to the icon service.</summary>
    public static bool IsPublicHost(string host)
    {
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.')) return false;
        foreach (var suffix in PrivateSuffixes)
            if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal)) return false;
        return true;
    }

    private JsonNode? Login => _raw["login"];
    private JsonNode? Card => _raw["card"];
    private JsonNode? Identity => _raw["identity"];
    private JsonNode? Ssh => _raw["sshKey"];

    public string? Password => D(Login?["password"]);
    public string? TotpSecret => D(Login?["totp"]);
    public string? Notes => D(_raw["notes"]);

    public Totp.Code? TotpNow()
    {
        var secret = TotpSecret;
        return secret is null ? null : Totp.Generate(secret, DateTimeOffset.UtcNow);
    }

    public string? CurrentTotp()
    {
        var secret = TotpSecret;
        return secret is null ? null : Totp.Generate(secret, DateTimeOffset.UtcNow).Value;
    }

    public CopyAction? PrimaryCopy => Type switch
    {
        ItemType.Login when HasPassword => new("Password", () => Password, true),
        ItemType.Card => new("Card number", () => D(Card?["number"]), true),
        ItemType.SecureNote => new("Note", () => Notes, true),
        ItemType.SshKey => new("Public key", () => D(Ssh?["publicKey"]), false),
        ItemType.Identity => new("Email", () => D(Identity?["email"]), false),
        _ => null,
    };

    public CopyAction? SecondaryCopy => Type switch
    {
        ItemType.Login when Username.Length > 0 => new("Username", () => Username, false),
        ItemType.Card => new("Security code", () => D(Card?["code"]), true),
        ItemType.SshKey => new("Private key", () => D(Ssh?["privateKey"]), true),
        ItemType.Identity when Username.Length > 0 => new("Username", () => Username, false),
        _ => null,
    };

    public CopyAction? TotpCopy => HasTotp ? new("One-time code", CurrentTotp, true) : null;

    public CopyAction? WebsiteCopy => Uris.Count > 0 ? new("Website", () => Uris[0], false) : null;

    public IReadOnlyList<ItemField> GetFields()
    {
        var list = new List<ItemField>();
        void Add(string label, FieldKind kind, JsonNode? node)
        {
            if (Str(node) != null) list.Add(new ItemField(label, kind, () => D(node)));
        }

        switch (Type)
        {
            case ItemType.Login:
                Add("Username", FieldKind.Text, Login?["username"]);
                Add("Password", FieldKind.Secret, Login?["password"]);
                if (HasTotp) list.Add(new ItemField("One-time code", FieldKind.Totp, CurrentTotp, TotpNow));
                foreach (var uri in Uris) list.Add(new ItemField("Website", FieldKind.Url, () => uri));
                break;
            case ItemType.Card:
                Add("Cardholder name", FieldKind.Text, Card?["cardholderName"]);
                Add("Number", FieldKind.Secret, Card?["number"]);
                Add("Brand", FieldKind.Text, Card?["brand"]);
                var month = D(Card?["expMonth"]);
                var year = D(Card?["expYear"]);
                if (month != null || year != null)
                    list.Add(new ItemField("Expiration", FieldKind.Text, () => $"{month?.PadLeft(2, '0')}/{year}"));
                Add("Security code", FieldKind.Secret, Card?["code"]);
                break;
            case ItemType.Identity:
                var id = Identity;
                var fullName = string.Join(" ", new[] { "title", "firstName", "middleName", "lastName" }.Select(k => D(id?[k])).Where(s => !string.IsNullOrEmpty(s)));
                if (fullName.Length > 0) list.Add(new ItemField("Name", FieldKind.Text, () => fullName));
                Add("Username", FieldKind.Text, id?["username"]);
                Add("Company", FieldKind.Text, id?["company"]);
                Add("Email", FieldKind.Text, id?["email"]);
                Add("Phone", FieldKind.Text, id?["phone"]);
                var address = string.Join(", ", new[] { "address1", "address2", "address3", "postalCode", "city", "state", "country" }.Select(k => D(id?[k])).Where(s => !string.IsNullOrEmpty(s)));
                if (address.Length > 0) list.Add(new ItemField("Address", FieldKind.Text, () => address));
                Add("Social security number", FieldKind.Secret, id?["ssn"]);
                Add("Passport number", FieldKind.Secret, id?["passportNumber"]);
                Add("License number", FieldKind.Secret, id?["licenseNumber"]);
                break;
            case ItemType.SshKey:
                Add("Public key", FieldKind.Text, Ssh?["publicKey"]);
                Add("Private key", FieldKind.Secret, Ssh?["privateKey"]);
                Add("Fingerprint", FieldKind.Text, Ssh?["keyFingerprint"]);
                break;
        }

        foreach (var field in _raw["fields"] as JsonArray ?? [])
        {
            var type = Int(field?["type"]);
            if (type is 3) continue;
            var name = D(field?["name"]) ?? "Field";
            var valueNode = field?["value"];
            if (Str(valueNode) is null) continue;
            list.Add(new ItemField(name, type == 1 ? FieldKind.Secret : FieldKind.Text, () => D(valueNode)));
        }

        if (Str(_raw["notes"]) != null) list.Add(new ItemField("Notes", FieldKind.Multiline, () => Notes));
        return list;
    }

    private string? D(JsonNode? node) => Str(node) is { Length: > 0 } s ? EncString.Decrypt(s, _key) : null;

    /// <summary>The end of a card number for the list, without the whole number ever becoming a string.</summary>
    private string? LastFour(JsonNode? node)
    {
        if (Str(node) is not { Length: > 0 } s) return null;
        var bytes = EncString.DecryptBytes(s, _key);
        try
        {
            return bytes.Length >= 4 ? Encoding.UTF8.GetString(bytes.AsSpan()[^4..]) : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string? HostOf(string uri)
    {
        var candidate = uri.Contains("://") ? uri : "https://" + uri;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        if (u.Scheme is not ("http" or "https")) return null;
        var host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }
}
