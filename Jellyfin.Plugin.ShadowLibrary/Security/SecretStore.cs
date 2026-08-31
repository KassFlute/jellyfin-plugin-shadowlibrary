using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Security;

/// <summary>
/// Encrypts friend server credentials before they are written to the plugin configuration.
/// </summary>
/// <remarks>
/// The key lives next to the data, in the plugin data folder with mode 600. That covers a
/// config file read out of context (a backup, a copied file, a shared log). It does not
/// protect against someone who already has filesystem access to the server.
/// </remarks>
public class SecretStore
{
    private const string KeyFileName = "secret.key";
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly ILogger<SecretStore> _logger;
    private readonly Lock _keyLock = new();
    private readonly string _dataFolderPath;
    private byte[]? _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretStore"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public SecretStore(ILogger<SecretStore> logger)
    {
        _logger = logger;
        _dataFolderPath = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("The plugin is not initialised.");
    }

    /// <summary>
    /// Encrypts a value.
    /// </summary>
    /// <param name="plainText">Value to encrypt. An empty value yields an empty string.</param>
    /// <returns>The base64 encoded ciphertext.</returns>
    public string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(GetKey(), TagSizeBytes))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        var payload = new byte[NonceSizeBytes + TagSizeBytes + cipherBytes.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSizeBytes);
        cipherBytes.CopyTo(payload, NonceSizeBytes + TagSizeBytes);

        return Convert.ToBase64String(payload);
    }

    /// <summary>
    /// Decrypts a value produced by <see cref="Protect"/>.
    /// </summary>
    /// <param name="cipherText">Base64 encoded ciphertext.</param>
    /// <returns>The plain text, or null when decryption fails.</returns>
    public string? Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return null;
        }

        try
        {
            var payload = Convert.FromBase64String(cipherText);
            if (payload.Length < NonceSizeBytes + TagSizeBytes)
            {
                return null;
            }

            var nonce = payload.AsSpan(0, NonceSizeBytes);
            var tag = payload.AsSpan(NonceSizeBytes, TagSizeBytes);
            var cipherBytes = payload.AsSpan(NonceSizeBytes + TagSizeBytes);
            var plainBytes = new byte[cipherBytes.Length];

            using (var aes = new AesGcm(GetKey(), TagSizeBytes))
            {
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            _logger.LogError(ex, "[ShadowLibrary] Could not decrypt a stored value. Did {KeyFile} change?", KeyFileName);
            return null;
        }
    }

    private byte[] GetKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        lock (_keyLock)
        {
            if (_key is not null)
            {
                return _key;
            }

            Directory.CreateDirectory(_dataFolderPath);
            var keyPath = Path.Combine(_dataFolderPath, KeyFileName);

            if (File.Exists(keyPath))
            {
                var existing = File.ReadAllBytes(keyPath);
                if (existing.Length == KeySizeBytes)
                {
                    _key = existing;
                    return _key;
                }

                _logger.LogWarning(
                    "[ShadowLibrary] Key file {KeyPath} is invalid, generating a new one. Stored credentials must be entered again.",
                    keyPath);
            }

            var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
            File.WriteAllBytes(keyPath, key);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _logger.LogInformation("[ShadowLibrary] Generated a new encryption key in {KeyPath}.", keyPath);
            _key = key;
            return _key;
        }
    }
}
