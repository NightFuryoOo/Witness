using System.Security.Cryptography;
using System.Text;

namespace TheWitness;

internal static class WitnessReportEncryption
{
    private const string VersionTag = "WTN3";

    private const string RsaPublicKeyXml =
        "<RSAKeyValue><Modulus>2cQOyi3Qb0WhdNtjKzyPTYnyivtcKN+0YxZRXBVsq0R7ImEhBpy0AdGEGZ7iqmde30Biu0/qgej3bCBK0zP/LPXjuOZdLGYSaYPv4TUKEuiDmEfS0wVIuDw09VQO2Qzqm7a8MUVmDAJgRXtOD+VhGBVVqvz85Jer+sT7He6PF9gS28OCOgo07OsT3yNA4lvYtkZ187QHir8Yxt2YDTiUINft3DLrzTPXXjd2jKUjZ+qJfY1EzWgLJ/NYlB8ArM17iSCgtGkBwM/LYl1SVvPhKgbN1WBu/lbYqY6dXPLL0L+mPy/pObJJjakyVplQAj9FnNmaWJ0CCoPWcKlO0lT4mQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    internal static string Encrypt(string plainTextReport)
    {
        ArgumentNullException.ThrowIfNull(plainTextReport);

        using Aes aes = Aes.Create();
        aes.GenerateKey();
        aes.GenerateIV();

        byte[] ciphertext = EncryptBytes(Encoding.UTF8.GetBytes(plainTextReport), aes.Key, aes.IV);

        byte[] combinedKeyIv = new byte[aes.Key.Length + aes.IV.Length];
        Buffer.BlockCopy(aes.Key, 0, combinedKeyIv, 0, aes.Key.Length);
        Buffer.BlockCopy(aes.IV, 0, combinedKeyIv, aes.Key.Length, aes.IV.Length);

        using RSA rsa = RSA.Create();
        rsa.FromXmlString(RsaPublicKeyXml);
        byte[] encryptedKeyBlob = rsa.Encrypt(combinedKeyIv, RSAEncryptionPadding.Pkcs1);

        byte[] hmac = HMACSHA256.HashData(SHA256.HashData(combinedKeyIv), ciphertext);

        return string.Join(
            '|',
            VersionTag,
            Convert.ToBase64String(encryptedKeyBlob),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(hmac));
    }

    private static byte[] EncryptBytes(byte[] data, byte[] key, byte[] iv)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }
}
