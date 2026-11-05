using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BigBlueFish.Memory;

public static class MemoryCrypto
{
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public static (string Ciphertext, string Nonce) Encrypt(byte[] key, Guid profileId, Guid recordId, long revision, long epoch, string json)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(json);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Aad(profileId, recordId, revision, epoch));
        return (Convert.ToBase64String(cipher.Concat(tag).ToArray()), Convert.ToBase64String(nonce));
    }

    public static string Decrypt(byte[] key, Guid profileId, Guid recordId, long revision, long epoch, string ciphertext, string nonce)
    {
        var packed = Convert.FromBase64String(ciphertext);
        if (packed.Length < 16) throw new CryptographicException("密文太短");
        var cipher = packed[..^16];
        var tag = packed[^16..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(Convert.FromBase64String(nonce), cipher, tag, plain, Aad(profileId, recordId, revision, epoch));
        return Encoding.UTF8.GetString(plain);
    }

    public static string WrapKey(string transferCode, byte[] archiveKey)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(transferCode), 32, Encoding.UTF8.GetBytes("bigbluefish-key-envelope"));
        var (cipher, nonce) = Encrypt(key, Guid.Empty, Guid.Empty, 0, 0, Convert.ToBase64String(archiveKey));
        return nonce + "." + cipher;
    }

    public static byte[] UnwrapKey(string transferCode, string wrapped)
    {
        var parts = wrapped.Split('.', 2);
        if (parts.Length != 2) throw new CryptographicException("传递包格式不对");
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(transferCode), 32, Encoding.UTF8.GetBytes("bigbluefish-key-envelope"));
        return Convert.FromBase64String(Decrypt(key, Guid.Empty, Guid.Empty, 0, 0, parts[1], parts[0]));
    }

    static byte[] Aad(Guid profileId, Guid recordId, long revision, long epoch) =>
        Encoding.UTF8.GetBytes($"{profileId:N}|{recordId:N}|{revision}|{epoch}");

    public static string Payload(MemoryRecord record) => JsonSerializer.Serialize(new
    {
        record.Kind, record.Content, record.Tags, record.Source, record.SourceId, record.Confirmed,
        record.ExpiresAt, record.Scope, record.AllowCloudModel, record.Pinned, record.UpdatedAt
    }, Sync.SyncClient.Json);
}
