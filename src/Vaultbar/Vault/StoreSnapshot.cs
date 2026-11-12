using System.Text.Json.Nodes;

namespace Vaultbar.Vault;

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
    public string WebVaultUrl { get; init; } = "https://vault.bitwarden.com";
    public string? VaultTimeout { get; init; }
    public string ContentHash { get; init; } = "";
}
