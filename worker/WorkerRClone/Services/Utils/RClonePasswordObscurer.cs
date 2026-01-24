using System.Security.Cryptography;
using System.Text;

namespace WorkerRClone.Services.Utils;

/// <summary>
/// Utility to obscure passwords in the format expected by rclone.
/// 
/// rclone uses AES-CTR encryption with a hardcoded key (which is publicly known
/// and documented in rclone's source code). The obscuring is NOT meant to be
/// secure encryption - it just prevents casual observation of passwords in
/// config files.
/// 
/// Format: base64url_no_padding(nonce + aes_ctr_encrypt(password, key, nonce))
/// 
/// Note: rclone uses URL-safe base64 (RFC 4648) WITHOUT padding.
/// </summary>
public static class RClonePasswordObscurer
{
    /// <summary>
    /// The rclone obscure key - this is intentionally public and hardcoded in rclone's source.
    /// See: https://github.com/rclone/rclone/blob/master/fs/config/obscure/obscure.go
    /// </summary>
    private static readonly byte[] ObscureKey = new byte[]
    {
        0x9c, 0x93, 0x5b, 0x48, 0x73, 0x0a, 0x55, 0x4d,
        0x6b, 0xfd, 0x7c, 0x63, 0xc8, 0x86, 0xa9, 0x2b,
        0xd3, 0x90, 0x19, 0x8e, 0xb8, 0x12, 0x8a, 0xfb,
        0xf4, 0xde, 0x16, 0x2b, 0x8b, 0x95, 0xf6, 0x38
    };

    private const int NonceSize = 16;

    /// <summary>
    /// Obscure a password in the format expected by rclone config files.
    /// </summary>
    /// <param name="password">The plain text password to obscure</param>
    /// <returns>The obscured password string</returns>
    public static string Obscure(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return string.Empty;
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(password);
        byte[] nonce = new byte[NonceSize];
        
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(nonce);
        }

        byte[] ciphertext = AesCtrEncrypt(plaintext, ObscureKey, nonce);
        
        // Combine nonce + ciphertext
        byte[] result = new byte[nonce.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        
        return ToBase64UrlNoPadding(result);
    }

    /// <summary>
    /// Reveal (decrypt) an obscured password.
    /// </summary>
    /// <param name="obscured">The obscured password string</param>
    /// <returns>The plain text password</returns>
    public static string Reveal(string obscured)
    {
        if (string.IsNullOrEmpty(obscured))
        {
            return string.Empty;
        }

        byte[] data = FromBase64UrlNoPadding(obscured);
        
        if (data.Length < NonceSize)
        {
            throw new ArgumentException("Obscured data too short");
        }

        byte[] nonce = new byte[NonceSize];
        byte[] ciphertext = new byte[data.Length - NonceSize];
        
        Buffer.BlockCopy(data, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(data, NonceSize, ciphertext, 0, ciphertext.Length);
        
        byte[] plaintext = AesCtrDecrypt(ciphertext, ObscureKey, nonce);
        
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Convert bytes to URL-safe base64 without padding (RFC 4648).
    /// This is the format rclone uses.
    /// </summary>
    private static string ToBase64UrlNoPadding(byte[] data)
    {
        string base64 = Convert.ToBase64String(data);
        
        // Convert to URL-safe: replace + with -, / with _
        // Remove padding =
        return base64
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Convert URL-safe base64 without padding back to bytes.
    /// </summary>
    private static byte[] FromBase64UrlNoPadding(string base64Url)
    {
        // Convert from URL-safe: replace - with +, _ with /
        string base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/');
        
        // Add padding if necessary
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        
        return Convert.FromBase64String(base64);
    }

    /// <summary>
    /// AES-CTR encryption (same operation as decryption due to XOR nature of CTR mode)
    /// </summary>
    private static byte[] AesCtrEncrypt(byte[] plaintext, byte[] key, byte[] nonce)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB; // CTR mode is built on top of ECB
        aes.Padding = PaddingMode.None;

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] counter = new byte[16];
        byte[] encryptedCounter = new byte[16];
        
        Buffer.BlockCopy(nonce, 0, counter, 0, Math.Min(nonce.Length, 16));

        using var encryptor = aes.CreateEncryptor();
        
        int offset = 0;
        while (offset < plaintext.Length)
        {
            // Encrypt the counter
            encryptor.TransformBlock(counter, 0, 16, encryptedCounter, 0);
            
            // XOR plaintext with encrypted counter
            int bytesToProcess = Math.Min(16, plaintext.Length - offset);
            for (int i = 0; i < bytesToProcess; i++)
            {
                ciphertext[offset + i] = (byte)(plaintext[offset + i] ^ encryptedCounter[i]);
            }
            
            offset += 16;
            IncrementCounter(counter);
        }

        return ciphertext;
    }

    /// <summary>
    /// AES-CTR decryption (identical to encryption in CTR mode)
    /// </summary>
    private static byte[] AesCtrDecrypt(byte[] ciphertext, byte[] key, byte[] nonce)
    {
        // CTR mode decryption is the same as encryption
        return AesCtrEncrypt(ciphertext, key, nonce);
    }

    /// <summary>
    /// Increment the counter for CTR mode (big-endian increment)
    /// </summary>
    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }
}
