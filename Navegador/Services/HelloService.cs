using System.Security.Cryptography;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace Navegador.Services;

/// <summary>
/// Desbloqueo con Windows Hello (huella, rostro o PIN). Windows crea una clave que vive en el chip de seguridad y solo
/// firma después de que la persona se identifica. Esa firma (siempre la misma para el mismo desafío) se usa como clave
/// para cifrar la clave de la bóveda, así que sin pasar por Windows Hello el archivo guardado no sirve para nada.
/// Si algo falla (por ejemplo, Windows reinició la clave por un cambio de PIN), se vuelve a la contraseña maestra.
/// </summary>
public sealed class HelloService
{
    private const string CredentialName = "NavegadorVault";
    private const string Challenge = "navegador-vault-unlock-v1";

    private static readonly string WrapPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Navegador", "vault.hello");

    public bool IsEnabled => File.Exists(WrapPath);

    public static async Task<bool> IsSupportedAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Activa Windows Hello: pide identificarse y guarda la clave de la bóveda protegida.</summary>
    public async Task<bool> EnableAsync(byte[] vaultKey)
    {
        try
        {
            var created = await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.ReplaceExisting);
            if (created.Status != KeyCredentialStatus.Success) return false;

            var wrapKey = await DeriveAsync(created.Credential);
            if (wrapKey is null) return false;

            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[vaultKey.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(wrapKey, 16))
                aes.Encrypt(nonce, vaultKey, cipher, tag);
            CryptographicOperations.ZeroMemory(wrapKey);

            using var ms = new MemoryStream();
            ms.Write(nonce);
            ms.Write(tag);
            ms.Write(cipher);

            Directory.CreateDirectory(Path.GetDirectoryName(WrapPath)!);
            File.WriteAllBytes(WrapPath, ms.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Pide identificarse y devuelve la clave de la bóveda, o null si se canceló o falló.</summary>
    public async Task<byte[]?> GetKeyAsync()
    {
        try
        {
            if (!File.Exists(WrapPath)) return null;
            var data = await File.ReadAllBytesAsync(WrapPath);
            if (data.Length < 12 + 16 + 1) return null;

            var opened = await KeyCredentialManager.OpenAsync(CredentialName);
            if (opened.Status != KeyCredentialStatus.Success) return null;

            var wrapKey = await DeriveAsync(opened.Credential);
            if (wrapKey is null) return null;

            var nonce = data.AsSpan(0, 12).ToArray();
            var tag = data.AsSpan(12, 16).ToArray();
            var cipher = data.AsSpan(28).ToArray();
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(wrapKey, 16);
                aes.Decrypt(nonce, cipher, tag, plain);
                return plain;
            }
            catch (CryptographicException)
            {
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapKey);
            }
        }
        catch
        {
            return null;
        }
    }

    public void Disable()
    {
        try { if (File.Exists(WrapPath)) File.Delete(WrapPath); } catch { }
        try { _ = KeyCredentialManager.DeleteAsync(CredentialName); } catch { }
    }

    private static async Task<byte[]?> DeriveAsync(KeyCredential credential)
    {
        var challenge = CryptographicBuffer.ConvertStringToBinary(Challenge, BinaryStringEncoding.Utf8);
        var result = await credential.RequestSignAsync(challenge);
        if (result.Status != KeyCredentialStatus.Success) return null;

        CryptographicBuffer.CopyToByteArray(result.Result, out var signature);
        return SHA256.HashData(signature);
    }
}
