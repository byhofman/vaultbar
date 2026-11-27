using Vaultbar.Vault;

namespace Vaultbar.UI;

internal static class VaultErrorText
{
    public static string For(VaultError error, string? quickUnlockName = null) => error switch
    {
        VaultError.DataNotFound => "Bitwarden desktop data not found.",
        VaultError.DataEmpty => "The Bitwarden data file is empty.",
        VaultError.NoAccount => "No account found. Log in to the Bitwarden desktop app once.",
        VaultError.NoMasterPassword => "This account has no master-password unlock data (logged out, or SSO/key-connector only).",
        VaultError.KdfMissing => "The KDF settings are missing from the Bitwarden data.",
        VaultError.KdfUnsupported => "This account uses a KDF Vaultbar does not support.",
        VaultError.KdfOutOfRange => "The KDF settings in the Bitwarden data are outside the range Bitwarden allows.",
        VaultError.KeyFormatUnsupported => "This account uses a key format Vaultbar does not support yet.",
        VaultError.WrongPassword => "Invalid master password.",
        VaultError.QuickUnlockMismatch => $"{quickUnlockName} no longer matches this vault. Enter your master password to set it up again.",
        VaultError.AccessDenied => OperatingSystem.IsMacOS()
            ? "macOS did not let Vaultbar read the Bitwarden data. Allow Vaultbar under System Settings > Privacy & Security (Full Disk Access, or Files & Folders), then restart it. After an update the permission may need to be granted again."
            : "The operating system did not let Vaultbar read the Bitwarden data. Check that your user can read the Bitwarden data folder.",
        _ => "Could not read the Bitwarden data. The details are in vaultbar.log.",
    };
}
