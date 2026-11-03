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
    private byte[]? _masterKeyHash;
    private StoreSnapshot? _snapshot;
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private DateTime _lastActivity = DateTime.UtcNow;

    public VaultService(Config config) => _config = config;

    public event Action? Changed;

    public VaultState State { get; private set; } = VaultState.NotFound;
    public string? StorePath { get; private set; }
    public string Email { get; private set; } = "";
    public string? Error { get; private set; }
    public IReadOnlyList<VaultItem> Items { get; private set; } = [];
    public int FailedItems { get; private set; }
    public DateTimeOffset? LastSync { get; private set; }
    public string IconsBaseUrl { get; private set; } = "https://icons.bitwarden.net";
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
            var masterKey = await Task.Run(() => MasterKey.Derive(password, snapshot.Salt, snapshot.Kdf));
            byte[] userKeyBytes;
            byte[] masterKeyHash;
            try
            {
                userKeyBytes = UnwrapUserKey(snapshot.WrappedUserKey, masterKey);
                masterKeyHash = SHA256.HashData(masterKey);
            }
            catch (CryptographicException)
            {
                return "Invalid master password.";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }

            lock (_gate)
            {
                _masterKeyHash = masterKeyHash;
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

    public async Task<bool> VerifyMasterPasswordAsync(string password)
    {
        var snapshot = _snapshot;
        var expected = _masterKeyHash;
        if (snapshot is null || expected is null) return false;
        var key = await Task.Run(() => MasterKey.Derive(password, snapshot.Salt, snapshot.Kdf));
        try
        {
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
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
            if (_masterKeyHash != null) CryptographicOperations.ZeroMemory(_masterKeyHash);
            _masterKeyHash = null;
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
