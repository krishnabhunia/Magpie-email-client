using System.Diagnostics;
using System.Security.Cryptography;

namespace Magpie.Core.Security;

/// <summary>Where <see cref="KeychainProtector"/> keeps its key: the macOS Keychain in the app, a fake in tests.</summary>
public interface IKeyStore
{
    /// <summary>The stored key, or null when there is none yet.</summary>
    byte[]? Load();
    void Save(byte[] key);
}

/// <summary>
/// Magpie for Mac: passwords, refresh tokens and API keys in secrets.json are encrypted with AES-256-GCM. The key is
/// random, made on first use and kept in the user's macOS Keychain (see <see cref="MacKeychainStore"/>), so
/// secrets.json alone can't be read on another Mac or by another user. Blob: version byte 1 · 12-byte nonce ·
/// 16-byte tag · cipher text. A blob made with another key fails with a <see cref="CryptographicException"/>.
/// </summary>
public sealed class KeychainProtector : ISecretProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12, TagSize = 16, KeySize = 32;
    private readonly IKeyStore _store;
    private readonly object _gate = new();
    private byte[]? _key;

    public KeychainProtector(IKeyStore store) { _store = store; }

    private byte[] Key()
    {
        lock (_gate)
        {
            if (_key != null) return _key;
            var stored = _store.Load();
            if (stored is { Length: KeySize }) return _key = stored;
            if (stored != null) Log.Warn($"the Keychain key has {stored.Length} bytes instead of {KeySize}: a new one is made (saved sign-ins must be entered again)");
            var key = RandomNumberGenerator.GetBytes(KeySize);
            _store.Save(key);
            return _key = key;
        }
    }

    public byte[] Protect(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var blob = new byte[1 + NonceSize + TagSize + plain.Length];
        blob[0] = Version;
        nonce.CopyTo(blob, 1);
        using var aes = new AesGcm(Key(), TagSize);
        aes.Encrypt(nonce, plain, blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize));
        return blob;
    }

    public byte[] Unprotect(byte[] cipher)
    {
        if (cipher.Length < 1 + NonceSize + TagSize || cipher[0] != Version)
            throw new CryptographicException("This secret wasn't saved by Magpie for Mac.");
        var plain = new byte[cipher.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Decrypt(cipher.AsSpan(1, NonceSize), cipher.AsSpan(1 + NonceSize + TagSize), cipher.AsSpan(1 + NonceSize, TagSize), plain);
        return plain;
    }
}

/// <summary>
/// The key in the login Keychain, through Apple's own <c>/usr/bin/security</c> tool (account "Magpie", service
/// "Magpie secrets"). Only used on a Mac.
/// </summary>
public sealed class MacKeychainStore : IKeyStore
{
    public const string Tool = "/usr/bin/security";
    public const string Account = "Magpie";
    public const string Service = "Magpie secrets";

    public byte[]? Load()
    {
        var (code, output, error) = Run("find-generic-password", "-a", Account, "-s", Service, "-w");
        if (code == 44) return null;   // errSecItemNotFound: first start
        if (code != 0) throw new InvalidOperationException("Magpie couldn't read its key from the Keychain: " + error.Trim());
        try { return Convert.FromBase64String(output.Trim()); }
        catch (FormatException) { return null; }
    }

    public void Save(byte[] key)
    {
        var (code, _, error) = Run("add-generic-password", "-a", Account, "-s", Service, "-w", Convert.ToBase64String(key), "-U");
        if (code != 0) throw new InvalidOperationException("Magpie couldn't save its key in the Keychain: " + error.Trim());
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + Tool);
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } throw new TimeoutException("The Keychain didn't answer."); }
        return (p.ExitCode, output.Result, error.Result);
    }
}
