using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Magpie.Core.Security;

public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] cipher);
}

/// <summary>Windows DPAPI, bound to the current Windows user.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Magpie.v1");
    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Passwords, refresh tokens and API keys. Stored as DPAPI-encrypted blobs in secrets.json,
/// keyed e.g. "account:{id}:password", "account:{id}:refresh", "ai:apikey".
/// </summary>
public sealed class SecretVault
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();
    private Dictionary<string, string> _items = new();

    public SecretVault(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        try
        {
            if (File.Exists(path))
                _items = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("secrets.json unreadable", ex);
            _items = new();
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var b64)) return null;
            try { return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(b64))); }
            catch (Exception ex) { Log.Warn($"secret '{key}' could not be decrypted: {ex.Message}"); return null; }
        }
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(value)) _items.Remove(key);
            else _items[key] = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(value)));
            Persist();
        }
    }

    public void RemovePrefix(string prefix)
    {
        lock (_gate)
        {
            foreach (var k in _items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _items.Remove(k);
            Persist();
        }
    }

    private void Persist()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
        File.Move(tmp, _path, true);
    }

    public static string PasswordKey(string accountId) => $"account:{accountId}:password";
    public static string RefreshKey(string accountId) => $"account:{accountId}:refresh";
    public const string AiKey = "ai:apikey";
}
