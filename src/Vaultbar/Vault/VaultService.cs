using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static Vaultbar.Vault.DesktopStore;

namespace Vaultbar.Vault;

public enum VaultState
{
    NotFound,
    Error,
    Locked,
    Unlocked,
}

public sealed class VaultService : IDisposable
{
    private readonly Config _config;
    private readonly object _gate = new();
    private SymmetricKey? _userKey;

    // Organisation and item keys by their wrapped form: a sync reuses the keys it already has instead of piling up copies.
    private Dictionary<string, SymmetricKey> _unwrapped = new();
    private byte[]? _userKeyHash;
    private StoreSnapshot? _snapshot;
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private readonly QuickUnlockStore _quickStore;

    public VaultService(Config config, string? quickUnlockPath = null)
    {
        _config = config;
        _quickStore = new QuickUnlockStore(quickUnlockPath ?? Path.Combine(Paths.CacheDir, "quick-unlock.dat"));
    }

    public IQuickUnlockProvider? QuickUnlock { get; set; }

    public event Action? Changed;

    public VaultState State { get; private set; } = VaultState.NotFound;
    public string? StorePath { get; private set; }
    public string Email { get; private set; } = "";
    public VaultError? Error { get; private set; }
    public IReadOnlyList<VaultItem> Items { get; private set; } = [];
    public int FailedItems { get; private set; }
    public DateTimeOffset? LastSync { get; private set; }
    public string IconsBaseUrl { get; private set; } = "https://icons.bitwarden.net";
    public string WebVaultUrl { get; private set; } = "https://vault.bitwarden.com";
    public bool ShowFavicons { get; private set; } = true;
    public TimeSpan? IdleTimeout { get; private set; }

    public bool IdleExpired => State == VaultState.Unlocked && IdleTimeout is { } t && DateTime.UtcNow - _lastActivity > t;

    public void Touch() => _lastActivity = DateTime.UtcNow;

    /// <summary>Re-reads account metadata while locked so the UI can show who to unlock.</summary>
    public void Probe()
    {
        if (State == VaultState.Unlocked) return;
        StorePath = DesktopStore.Locate(_config.DataPath);
        if (StorePath is null)
        {
            State = VaultState.NotFound;
            Error = null;
            return;
        }

        try
        {
            ApplyMetadata(DesktopStore.Read(StorePath));
            State = VaultState.Locked;
            Error = null;
        }
        catch (Exception ex)
        {
            State = VaultState.Error;
            Error = ErrorOf(ex);
            Log.Write($"probe: {Log.Shape(ex)}");
        }
    }

    /// <returns>Why the vault did not unlock, or null on success.</returns>
    public async Task<VaultError?> UnlockAsync(string password)
    {
        if (StorePath is null) return VaultError.DataNotFound;
        try
        {
            var snapshot = await Task.Run(() => DesktopStore.Read(StorePath));
            var userKeyBytes = await Task.Run(() => UnwrapWithPassword(password, snapshot));
            if (userKeyBytes is null) return VaultError.WrongPassword;
            Open(snapshot, userKeyBytes);
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"unlock: {Log.Shape(ex)}");
            return ErrorOf(ex);
        }
    }

    /// <summary>True only when the password unwraps the key the vault is unlocked with.</summary>
    public async Task<bool> VerifyMasterPasswordAsync(string password)
    {
        var snapshot = _snapshot;
        if (snapshot is null || State != VaultState.Unlocked) return false;
        var key = await Task.Run(() => UnwrapWithPassword(password, snapshot));
        return key != null && MatchesUserKey(key);
    }

    public QuickUnlockSetting QuickUnlockSetting =>
        QuickUnlock is null || _snapshot is null ? QuickUnlockSetting.Off : _quickStore.SettingFor(_snapshot.UserId);

    public bool CanQuickUnlock => QuickUnlockSetting == QuickUnlockSetting.On;

    public async Task<bool> ShouldOfferQuickUnlockAsync() =>
        State == VaultState.Unlocked && QuickUnlockSetting == QuickUnlockSetting.NotSetUp && await QuickUnlock!.IsAvailableAsync();

    public async Task<(QuickUnlockStatus Status, VaultError? Error)> QuickUnlockAsync()
    {
        var provider = QuickUnlock;
        if (provider is null || StorePath is null) return (QuickUnlockStatus.Unavailable, null);
        try
        {
            var snapshot = await Task.Run(() => DesktopStore.Read(StorePath));
            if (_quickStore.Load(snapshot.UserId) is not { } entry) return (QuickUnlockStatus.Unavailable, null);
            var (status, signature) = await provider.SignAsync(entry.Challenge, createKey: false);
            if (status == QuickUnlockStatus.Unavailable) _quickStore.Delete();
            if (status != QuickUnlockStatus.Success || signature is null) return (status, null);

            byte[] userKey;
            try
            {
                userKey = QuickUnlockStore.Unwrap(entry, signature);
                CheckUserKey(snapshot, userKey);
            }
            catch (CryptographicException ex)
            {
                Log.Write($"quick unlock: {Log.Shape(ex)}");
                _quickStore.Delete();
                return (QuickUnlockStatus.Failed, VaultError.QuickUnlockMismatch);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }

            Open(snapshot, userKey);
            return (QuickUnlockStatus.Success, null);
        }
        catch (Exception ex)
        {
            Log.Write($"quick unlock: {Log.Shape(ex)}");
            return (QuickUnlockStatus.Failed, ErrorOf(ex));
        }
    }

    private static VaultError ErrorOf(Exception ex) => ex switch
    {
        StoreException store => store.Error,
        NotSupportedException => VaultError.KeyFormatUnsupported,
        _ => VaultError.Unreadable,
    };

    public async Task<QuickUnlockStatus> SetUpQuickUnlockAsync()
    {
        var provider = QuickUnlock;
        var snapshot = _snapshot;
        byte[] userKey;
        lock (_gate)
        {
            if (provider is null || snapshot is null || _userKey is not { } key) return QuickUnlockStatus.Unavailable;
            userKey = key.ToBytes();
        }

        try
        {
            var challenge = RandomNumberGenerator.GetBytes(32);
            var (status, signature) = await provider.SignAsync(challenge, createKey: true);
            if (status == QuickUnlockStatus.Canceled) _quickStore.SaveDeclined(snapshot.UserId);
            if (status != QuickUnlockStatus.Success || signature is null) return status;
            try
            {
                _quickStore.Save(new QuickUnlockEntry(snapshot.UserId, challenge, QuickUnlockStore.Wrap(userKey, signature, challenge, snapshot.UserId)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
            return QuickUnlockStatus.Success;
        }
        catch (Exception ex)
        {
            Log.Write($"quick unlock setup: {Log.Shape(ex)}");
            return QuickUnlockStatus.Failed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(userKey);
        }
    }

    /// <summary>Confirms the user with the device key, for items with master password re-prompt.</summary>
    public async Task<QuickUnlockStatus> VerifyWithQuickUnlockAsync()
    {
        var provider = QuickUnlock;
        var snapshot = _snapshot;
        if (provider is null || snapshot is null || State != VaultState.Unlocked || _quickStore.Load(snapshot.UserId) is not { } entry)
            return QuickUnlockStatus.Unavailable;
        var (status, signature) = await provider.SignAsync(entry.Challenge, createKey: false);
        if (status != QuickUnlockStatus.Success || signature is null) return status;
        try
        {
            var key = QuickUnlockStore.Unwrap(entry, signature);
            return MatchesUserKey(key) ? QuickUnlockStatus.Success : QuickUnlockStatus.Failed;
        }
        catch (CryptographicException)
        {
            return QuickUnlockStatus.Failed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public async Task SetQuickUnlockEnabledAsync(bool enabled)
    {
        if (QuickUnlock is not { } provider || _snapshot is not { } snapshot) return;
        if (enabled)
        {
            _quickStore.Delete();
            if (State == VaultState.Unlocked) await SetUpQuickUnlockAsync();
            return;
        }
        _quickStore.SaveDeclined(snapshot.UserId);
        await provider.DeleteKeyAsync();
    }

    private static byte[]? UnwrapWithPassword(string password, StoreSnapshot snapshot)
    {
        var masterKey = MasterKey.Derive(password, snapshot.Salt, snapshot.Kdf);
        try
        {
            return UnwrapUserKey(snapshot.WrappedUserKey, masterKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private bool MatchesUserKey(byte[] userKey)
    {
        try
        {
            var expected = _userKeyHash;
            return expected != null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(userKey), expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(userKey);
        }
    }

    /// <exception cref="CryptographicException">The key does not decrypt this account's data.</exception>
    private static void CheckUserKey(StoreSnapshot snapshot, byte[] userKey)
    {
        using var key = SymmetricKey.FromBytes(userKey);
        var probe = snapshot.EncPrivateKey is { } pk && IsAuthenticated(pk) ? pk : null;
        if (probe is null)
        {
            foreach (var (_, node) in snapshot.Ciphers)
            {
                if (node is not JsonObject cipher || Str(cipher["organizationId"]) != null) continue;
                if ((Str(cipher["key"]) ?? Str(cipher["name"])) is { } s && IsAuthenticated(s))
                {
                    probe = s;
                    break;
                }
            }
        }
        if (probe != null) CryptographicOperations.ZeroMemory(EncString.DecryptBytes(probe, key));
    }

    private static bool IsAuthenticated(string encString) => EncString.TypeOf(encString) is 2 or 7;

    private void Open(StoreSnapshot snapshot, byte[] userKeyBytes)
    {
        lock (_gate)
        {
            _userKeyHash = SHA256.HashData(userKeyBytes);
            _userKey = SymmetricKey.FromBytes(userKeyBytes);
            CryptographicOperations.ZeroMemory(userKeyBytes);
            LoadItems(snapshot);
        }

        ApplyMetadata(snapshot);
        State = VaultState.Unlocked;
        Error = null;
        Touch();
        StartWatching(snapshot.Path);
        Changed?.Invoke();
    }

    public void Lock()
    {
        if (State != VaultState.Unlocked) return;
        _watcher?.Dispose();
        _watcher = null;
        _reloadTimer?.Dispose();
        _reloadTimer = null;
        lock (_gate)
        {
            Items = [];
            foreach (var key in _unwrapped.Values) key.Dispose();
            _unwrapped = new();
            _userKey?.Dispose();
            _userKey = null;
            if (_userKeyHash != null) CryptographicOperations.ZeroMemory(_userKeyHash);
            _userKeyHash = null;
        }
        State = VaultState.Locked;
        GC.Collect();
        Changed?.Invoke();
    }

    private static byte[] UnwrapUserKey(string wrapped, byte[] masterKey)
    {
        var type = EncString.TypeOf(wrapped);
        if (type == 0)
        {
            using var raw = new SymmetricKey(masterKey.ToArray(), null);
            return EncString.DecryptBytes(wrapped, raw);
        }
        if (type != 2)
            throw new NotSupportedException($"Key format type {type}");
        using var stretched = MasterKey.Stretch(masterKey);
        return EncString.DecryptBytes(wrapped, stretched);
    }

    private void ApplyMetadata(StoreSnapshot s)
    {
        _snapshot = s;
        Email = s.Email;
        LastSync = s.LastSync;
        IconsBaseUrl = s.IconsBaseUrl;
        WebVaultUrl = s.WebVaultUrl;
        ShowFavicons = s.ShowFavicons;
        IdleTimeout = _config.LockAfterMinutes is { } m
            ? (m > 0 ? TimeSpan.FromMinutes(m) : null)
            : int.TryParse(s.VaultTimeout, out var minutes) && minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
    }

    private void LoadItems(StoreSnapshot s)
    {
        var userKey = _userKey ?? throw new InvalidOperationException("locked");
        var previous = _unwrapped;
        var unwrapped = new Dictionary<string, SymmetricKey>();

        SymmetricKey Unwrap(string id, Func<byte[]> unwrap)
        {
            if (unwrapped.TryGetValue(id, out var key)) return key;
            if (!previous.Remove(id, out key))
            {
                var raw = unwrap();
                try
                {
                    key = SymmetricKey.FromBytes(raw);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
            return unwrapped[id] = key;
        }

        var orgKeys = LoadOrganizationKeys(s, userKey, Unwrap);

        var folders = new Dictionary<string, string>();
        foreach (var (id, node) in s.Folders)
        {
            try
            {
                if (Str(node?["name"]) is { } name) folders[id] = EncString.Decrypt(name, userKey);
            }
            catch (Exception ex)
            {
                Log.Write($"folder {id}: {Log.Shape(ex)}");
            }
        }

        var items = new List<VaultItem>(s.Ciphers.Count);
        var failed = 0;
        foreach (var (_, node) in s.Ciphers)
        {
            if (node is not JsonObject cipher) continue;
            if (Str(cipher["deletedDate"]) != null || Str(cipher["archivedDate"]) != null) continue;

            var orgId = Str(cipher["organizationId"]);
            var baseKey = orgId is null ? userKey : orgKeys.GetValueOrDefault(orgId);
            if (baseKey is null)
            {
                failed++;
                continue;
            }

            try
            {
                var key = baseKey;
                if (Str(cipher["key"]) is { } itemKey) key = Unwrap($"item|{orgId}|{itemKey}", () => EncString.DecryptBytes(itemKey, baseKey));
                var folder = Str(cipher["folderId"]) is { } fid ? folders.GetValueOrDefault(fid) : null;
                items.Add(new VaultItem(cipher, key, folder));
            }
            catch (Exception ex)
            {
                failed++;
                Log.Write($"cipher {Str(cipher["id"])}: {Log.Shape(ex)}");
            }
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _unwrapped = unwrapped;
        foreach (var stale in previous.Values) stale.Dispose();
        Items = items;
        FailedItems = failed;
    }

    private static Dictionary<string, SymmetricKey> LoadOrganizationKeys(StoreSnapshot s, SymmetricKey userKey, Func<string, Func<byte[]>, SymmetricKey> unwrap)
    {
        var orgKeys = new Dictionary<string, SymmetricKey>();
        if (s.OrgKeys.Count == 0 || s.EncPrivateKey is null) return orgKeys;

        using var rsa = RSA.Create();
        try
        {
            var der = EncString.DecryptBytes(s.EncPrivateKey, userKey);
            try
            {
                rsa.ImportPkcs8PrivateKey(der, out _);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(der);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or NotSupportedException or FormatException)
        {
            // A damaged private key costs the organisation items, which then count as failed, not the personal vault.
            Log.Write($"private key: {Log.Shape(ex)}");
            return orgKeys;
        }

        foreach (var (orgId, enc) in s.OrgKeys)
        {
            try
            {
                orgKeys[orgId] = unwrap($"org|{orgId}|{enc}", () => EncString.DecryptRsa(enc, rsa));
            }
            catch (Exception ex)
            {
                Log.Write($"org key {orgId}: {Log.Shape(ex)}");
            }
        }
        return orgKeys;
    }

    private void StartWatching(string path)
    {
        _watcher?.Dispose();
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        _watcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _reloadTimer?.Change(1500, Timeout.Infinite);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (_, _) => _reloadTimer?.Change(1500, Timeout.Infinite);
        _reloadTimer = new Timer(_ => Reload(path), null, Timeout.Infinite, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    private void Reload(string path)
    {
        try
        {
            var snapshot = DesktopStore.Read(path);
            if (snapshot.UserId != _snapshot?.UserId)
            {
                // The desktop app switched accounts: this key belongs to the other one.
                Lock();
                return;
            }
            if (snapshot.ContentHash == _snapshot?.ContentHash)
            {
                LastSync = snapshot.LastSync;
                return;
            }

            lock (_gate)
            {
                if (State != VaultState.Unlocked || _userKey is null) return;
                LoadItems(snapshot);
            }
            ApplyMetadata(snapshot);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"reload: {Log.Shape(ex)}");
        }
    }

    public void Dispose()
    {
        Lock();
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
    }
}
