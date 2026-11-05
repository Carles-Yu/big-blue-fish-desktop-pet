using System.Security.Cryptography;
using System.Text;

namespace BigBlueFish.Memory;

public static class SecretBox
{
    public static void Save(string path, byte[] plaintext, string? passphrase)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] stored;
        if (OperatingSystem.IsWindows())
            stored = System.Security.Cryptography.ProtectedData.Protect(plaintext, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        else
        {
            if (string.IsNullOrWhiteSpace(passphrase))
                throw new InvalidOperationException("Linux 没有可用的系统凭据库。请在记忆页设置同步口令，密钥不会明文保存。");
            stored = Encrypt(passphrase, plaintext);
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(stored);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
            if (!OperatingSystem.IsWindows()) TryRestrict(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static byte[]? TryLoad(string path, string? passphrase)
    {
        if (!File.Exists(path)) return null;
        var stored = File.ReadAllBytes(path);
        if (OperatingSystem.IsWindows())
            return System.Security.Cryptography.ProtectedData.Unprotect(stored, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        if (string.IsNullOrWhiteSpace(passphrase)) return null;
        return Decrypt(passphrase, stored);
    }

    static byte[] Encrypt(string passphrase, byte[] plaintext)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 100_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return salt.Concat(nonce).Concat(cipher).Concat(tag).ToArray();
    }

    static byte[] Decrypt(string passphrase, byte[] stored)
    {
        if (stored.Length < 16 + 12 + 16) throw new CryptographicException("凭据文件损坏");
        var salt = stored[..16];
        var nonce = stored[16..28];
        var tag = stored[^16..];
        var cipher = stored[28..^16];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 100_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    static void TryRestrict(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) when (OperatingSystem.IsWindows()) { }
    }
}
