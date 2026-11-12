namespace Vaultbar.Vault;

/// <summary>Why the vault could not be read or unlocked. The UI turns these into words.</summary>
public enum VaultError
{
    DataNotFound,
    DataEmpty,
    NoAccount,
    NoMasterPassword,
    KdfMissing,
    KdfUnsupported,
    KdfOutOfRange,
    KeyFormatUnsupported,
    WrongPassword,
    QuickUnlockMismatch,
    Unreadable,
}
