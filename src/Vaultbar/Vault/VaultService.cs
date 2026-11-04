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
    private readonly List<SymmetricKey> _keys = new();
    private SymmetricKey? _userKey;
    private Dictionary<string, SymmetricKey> _orgKeys = new();
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
    public string? Error { get; private set; }
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
            Error = ex is StoreException ? ex.Message : $"Could not read Bitwarden data: {ex.Message}";
            Log.Write($"probe: {ex}");
        }
    }

    /// <returns>An error message, or null on success.</returns>
    public async Task<string?> UnlockAsync(string password)
    {
        if (StorePath is null) return "Bitwarden desktop data not found.";
        try
        {
            var snapshot = await Task.Run(() => DesktopStore.Read(StorePath));
            var userKeyBytes = await Task.Run(() => UnwrapWithPassword(password, snapshot));
            if (userKeyBytes is null) return "Invalid master password.";
            Open(snapshot, userKeyBytes);
            return null;
        }
        catch (NotSupportedException ex)
        {
            Log.Write($"unlock: {ex}");
            return ex.Message;
        }
        catch (Exception ex)
        {
            Log.Write($"unlock: {ex}");
            return ex is StoreException ? ex.Message : $"Unlock failed: {ex.Message}";
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

    public async Task<(QuickUnlockStatus Status, string? Error)> QuickUnlockAsync()
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
                Log.Write($"quick unlock: {ex.Message}");
                _quickStore.Delete();
                return (QuickUnlockStatus.Failed, $"{provider.Name} no longer matches this vault. Enter your master password to set it up again.");
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
            Log.Write($"quick unlock: {ex}");
            return (QuickUnlockStatus.Failed, ex is StoreException ? ex.Message : $"{provider.Name} unlock failed: {ex.Message}");
        }
    }

    public async Task<QuickUnlockStatus> SetUpQuickUnlockAsync()
    {
        var provider = QuickUnlock;
        var snapshot = _snapshot;
        byte[] userKey;
        lock (_gate)
        {
            if (provider is null || snapshot is null || _userKey is not { } key) return QuickUnlockStatus.Unavailable;
            userKey = key.Mac is null ? key.Enc.ToArray() : [.. key.Enc, .. key.Mac];
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
            Log.Write($"quick unlock setup: {ex}");
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
        var probe = snapshot.EncPrivateKey is { } pk && EncString.TypeOf(pk) == 2 ? pk : null;
        if (probe is null)
        {
            foreach (var (_, node) in snapshot.Ciphers)
            {
                if (node is not JsonObject cipher || Str(cipher["organizationId"]) != null) continue;
                if ((Str(cipher["key"]) ?? Str(cipher["name"])) is { } s && EncString.TypeOf(s) == 2)
                {
                    probe = s;
                    break;
                }
            }
        }
        if (probe != null) CryptographicOperations.ZeroMemory(EncString.DecryptBytes(probe, key));
    }

    private void Open(StoreSnapshot snapshot, byte[] userKeyBytes)
    {
        lock (_gate)
        {
            _userKeyHash = SHA256.HashData(userKeyBytes);
            _userKey = SymmetricKey.FromBytes(userKeyBytes);
            CryptographicOperations.ZeroMemory(userKeyBytes);
            _keys.Add(_userKey);
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
            foreach (var k in _keys) k.Dispose();
            _keys.Clear();
            _orgKeys = new();
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
            throw new NotSupportedException($"This account uses a key format (type {type}) Vaultbar does not support yet.");
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

        var orgKeys = new Dictionary<string, SymmetricKey>();
        if (s.OrgKeys.Count > 0 && s.EncPrivateKey != null)
        {
            var der = EncString.DecryptBytes(s.EncPrivateKey, userKey);
            using var rsa = RSA.Create();
            try
            {
                rsa.ImportPkcs8PrivateKey(der, out _);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(der);
            }

            foreach (var (orgId, enc) in s.OrgKeys)
            {
                try
                {
                    var raw = EncString.DecryptRsa(enc, rsa);
                    var key = SymmetricKey.FromBytes(raw);
                    CryptographicOperations.ZeroMemory(raw);
                    orgKeys[orgId] = key;
                    _keys.Add(key);
                }
                catch (Exception ex)
                {
                    Log.Write($"org key {orgId}: {ex.Message}");
                }
            }
        }

        var folders = new Dictionary<string, string>();
        foreach (var (id, node) in s.Folders)
        {
            try
            {
                if (Str(node?["name"]) is { } name) folders[id] = EncString.Decrypt(name, userKey);
            }
            catch (Exception ex)
            {
                Log.Write($"folder {id}: {ex.Message}");
            }
        }

        var items = new List<VaultItem>(s.Ciphers.Count);
        var failed = 0;
        foreach (var (_, node) in s.Ciphers)
        {
            if (node is not JsonObject cipher) continue;
            if (Str(cipher["deletedDate"]) != null || Str(cipher["archivedDate"]) != null) continue;

            var baseKey = Str(cipher["organizationId"]) is { } orgId
                ? orgKeys.GetValueOrDefault(orgId)
                : userKey;
            if (baseKey is null)
            {
                failed++;
                continue;
            }

            try
            {
                var key = baseKey;
                if (Str(cipher["key"]) is { } itemKey)
                {
                    var raw = EncString.DecryptBytes(itemKey, baseKey);
                    key = SymmetricKey.FromBytes(raw);
                    CryptographicOperations.ZeroMemory(raw);
                    _keys.Add(key);
                }
                var folder = Str(cipher["folderId"]) is { } fid ? folders.GetValueOrDefault(fid) : null;
                items.Add(new VaultItem(cipher, key, folder));
            }
            catch (Exception ex)
            {
                failed++;
                Log.Write($"cipher {Str(cipher["id"])}: {ex.Message}");
            }
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _orgKeys = orgKeys;
        Items = items;
        FailedItems = failed;
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
            Log.Write($"reload: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Lock();
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
    }
}
