using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebDavSync.Core.Config;

/// <summary>
/// <c>data\secrets.dat</c> (§15.1): app passwords keyed by pair id, DPAPI-encrypted for the current Windows user.
/// The file holds only encrypted blobs; passwords are never logged.
/// </summary>
public sealed class SecretStore(string path)
{
    public string Path { get; } = path;

    public string? Get(string pairId)
    {
        if (!Load().TryGetValue(pairId, out var blob))
        {
            return null;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(NeedsWindows);
            }

            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy(pairId), DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Another Windows user or machine (copied app folder): the password must be entered again.
            throw new InvalidDataException($"The stored password of pair {pairId} cannot be decrypted by this Windows user; set it again.", ex);
        }
    }

    public void Set(string pairId, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(NeedsWindows);
        }

        var secrets = Load();
        secrets[pairId] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy(pairId), DataProtectionScope.CurrentUser));
        Save(secrets);
    }

    public bool Remove(string pairId)
    {
        var secrets = Load();
        if (!secrets.Remove(pairId))
        {
            return false;
        }

        Save(secrets);
        return true;
    }

    private const string NeedsWindows = "DPAPI secrets need Windows.";

    private static byte[] Entropy(string pairId) => Encoding.UTF8.GetBytes("WebDavSync/" + pairId.ToLowerInvariant());

    private Dictionary<string, string> Load() =>
        File.Exists(Path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path)) is { } d
                ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private void Save(Dictionary<string, string> secrets)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(secrets, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path, overwrite: true);
    }
}
