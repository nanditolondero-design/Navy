using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Navegador.Services;

public sealed class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
}

/// <summary>
/// Bóveda de contraseñas. Se guarda en un solo archivo cifrado con AES-256-GCM; la clave sale de la contraseña maestra
/// con PBKDF2-SHA256 (600.000 vueltas). La contraseña maestra no se guarda en ningún lado: si se pierde, no hay forma
/// de recuperar el contenido. Mientras está bloqueada no hay nada descifrado en memoria.
/// </summary>
public sealed class VaultService
{
    private const int DefaultIterations = 600_000;
    private const int HeaderSize = 4 + 4 + 16;   // marca + vueltas + sal
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] Magic = "NAV1"u8.ToArray();

    private static readonly string VaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Navegador", "vault.dat");

    private byte[]? _key;
    private byte[]? _salt;
    private int _iterations = DefaultIterations;
    private List<VaultEntry>? _entries;
    private DateTime _lastUse = DateTime.Now;

    /// <summary>Se dispara al bloquear, desbloquear o cambiar el contenido.</summary>
    public event Action? Changed;

    public bool Exists => File.Exists(VaultPath);
    public bool IsUnlocked => _entries is not null;
    public IReadOnlyList<VaultEntry> Entries => _entries ?? (IReadOnlyList<VaultEntry>)Array.Empty<VaultEntry>();

    // ───────── Crear, desbloquear y bloquear ─────────

    public async Task CreateAsync(string master)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = await Task.Run(() => Derive(master, salt, DefaultIterations));

        _salt = salt;
        _key = key;
        _iterations = DefaultIterations;
        _entries = new List<VaultEntry>();
        Save();
        Touch();
        Changed?.Invoke();
    }

    private sealed record Parsed(byte[] Header, byte[] Salt, int Iterations, byte[] Nonce, byte[] Tag, byte[] Cipher);

    private static async Task<Parsed?> ReadFileAsync()
    {
        if (!File.Exists(VaultPath)) return null;

        var data = await File.ReadAllBytesAsync(VaultPath);
        if (data.Length < HeaderSize + NonceSize + TagSize || !data.AsSpan(0, 4).SequenceEqual(Magic)) return null;

        var iterations = BitConverter.ToInt32(data, 4);
        if (iterations < 100_000 || iterations > 5_000_000) return null;

        return new Parsed(
            data.AsSpan(0, HeaderSize).ToArray(),
            data.AsSpan(8, 16).ToArray(),
            iterations,
            data.AsSpan(HeaderSize, NonceSize).ToArray(),
            data.AsSpan(HeaderSize + NonceSize, TagSize).ToArray(),
            data.AsSpan(HeaderSize + NonceSize + TagSize).ToArray());
    }

    /// <summary>Devuelve false si la contraseña maestra es incorrecta o el archivo no es válido.</summary>
    public async Task<bool> UnlockAsync(string master)
    {
        var file = await ReadFileAsync();
        if (file is null) return false;

        var key = await Task.Run(() => Derive(master, file.Salt, file.Iterations));
        return TryOpen(file, key);
    }

    /// <summary>Desbloquea con la clave ya derivada (la que entrega Windows Hello).</summary>
    public async Task<bool> UnlockWithKeyAsync(byte[] key)
    {
        var file = await ReadFileAsync();
        if (file is null) return false;
        return TryOpen(file, (byte[])key.Clone());
    }

    private bool TryOpen(Parsed file, byte[] key)
    {
        var plain = new byte[file.Cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(file.Nonce, file.Cipher, file.Tag, plain, file.Header);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            return false;
        }

        try
        {
            _entries = JsonSerializer.Deserialize<List<VaultEntry>>(plain) ?? new List<VaultEntry>();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        _key = key;
        _salt = file.Salt;
        _iterations = file.Iterations;
        Touch();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Una copia de la clave, para que Windows Hello la guarde protegida. Quien la reciba debe borrarla de memoria.</summary>
    public byte[]? ExportKey() => _key is null ? null : (byte[])_key.Clone();

    public void Lock()
    {
        if (!IsUnlocked) return;
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _entries = null;
        Changed?.Invoke();
    }

    /// <summary>Vuelve a pedir la contraseña maestra para acciones delicadas (exportar, cambiarla).</summary>
    public async Task<bool> VerifyMasterAsync(string master)
    {
        if (_key is null || _salt is null) return false;
        var salt = _salt;
        var iterations = _iterations;
        var candidate = await Task.Run(() => Derive(master, salt, iterations));
        var ok = CryptographicOperations.FixedTimeEquals(candidate, _key);
        CryptographicOperations.ZeroMemory(candidate);
        return ok;
    }

    public async Task<bool> ChangeMasterAsync(string oldMaster, string newMaster)
    {
        if (!IsUnlocked || !await VerifyMasterAsync(oldMaster)) return false;

        var salt = RandomNumberGenerator.GetBytes(16);
        var key = await Task.Run(() => Derive(newMaster, salt, DefaultIterations));

        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _salt = salt;
        _key = key;
        _iterations = DefaultIterations;
        Save();
        Touch();
        return true;
    }

    /// <summary>Borra la bóveda entera. Se usa cuando se olvidó la contraseña maestra.</summary>
    public void Reset()
    {
        Lock();
        try { if (File.Exists(VaultPath)) File.Delete(VaultPath); } catch { }
        Changed?.Invoke();
    }

    public void CheckIdle(TimeSpan max)
    {
        if (IsUnlocked && DateTime.Now - _lastUse > max) Lock();
    }

    private void Touch() => _lastUse = DateTime.Now;

    // ───────── Contenido ─────────

    public void Add(VaultEntry entry)
    {
        if (_entries is null) return;
        _entries.Add(entry);
        SaveAndNotify();
    }

    public void Update(VaultEntry entry)
    {
        if (_entries is null) return;
        var i = _entries.FindIndex(e => e.Id == entry.Id);
        if (i >= 0) _entries[i] = entry;
        SaveAndNotify();
    }

    public void Remove(Guid id)
    {
        if (_entries is null) return;
        _entries.RemoveAll(e => e.Id == id);
        SaveAndNotify();
    }

    /// <summary>Agrega las que no estén ya (mismo sitio, usuario y contraseña). Devuelve cuántas entraron.</summary>
    public int Import(IEnumerable<VaultEntry> entries)
    {
        if (_entries is null) return 0;
        var seen = new HashSet<string>(_entries.Select(Key));
        var added = 0;
        foreach (var e in entries)
        {
            if (!seen.Add(Key(e))) continue;
            _entries.Add(e);
            added++;
        }
        if (added > 0) SaveAndNotify();
        return added;
    }

    private static string Key(VaultEntry e) =>
        (HostFromUrl(e.Url) ?? e.Url).ToLowerInvariant() + "|" + e.Username + "|" + e.Password;

    /// <summary>Las contraseñas del mismo sitio (mismo dominio), primero las del host exacto.</summary>
    public IEnumerable<VaultEntry> MatchHost(string host)
    {
        if (_entries is null) return Enumerable.Empty<VaultEntry>();
        Touch();
        var reg = FilterEngine.RegDomain(host.ToLowerInvariant());
        return _entries
            .Where(e => HostFromUrl(e.Url) is { } h && FilterEngine.RegDomain(h.ToLowerInvariant()) == reg)
            .OrderByDescending(e => string.Equals(HostFromUrl(e.Url), host, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Modified)
            .ToList();
    }

    public static string? HostFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Contains("://") ? url : "https://" + url;
        return Uri.TryCreate(text, UriKind.Absolute, out var u) ? u.Host : null;
    }

    // ───────── Generador ─────────

    public static string GeneratePassword(int length = 20)
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+?";
        var all = lower + upper + digits + symbols;

        var chars = new List<char>
        {
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        };
        while (chars.Count < length) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        // mezcla para que los cuatro primeros no queden siempre en el mismo orden
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    // ───────── Disco ─────────

    private static byte[] Derive(string master, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(master), salt, iterations, HashAlgorithmName.SHA256, 32);

    private void SaveAndNotify()
    {
        Save();
        Touch();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_key is null || _salt is null || _entries is null) return;

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BitConverter.GetBytes(_iterations).CopyTo(header, 4);
        _salt.CopyTo(header, 8);

        var plain = JsonSerializer.SerializeToUtf8Bytes(_entries);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag, header);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(cipher);

        Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
        var temp = VaultPath + ".tmp";
        File.WriteAllBytes(temp, ms.ToArray());
        File.Move(temp, VaultPath, overwrite: true);
    }
}
