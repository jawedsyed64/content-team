using System.Security.Cryptography;
using System.Text;

namespace Qalam.App.Services;

/// <summary>Stores the Claude API key encrypted with Windows DPAPI (only your Windows account can decrypt it).</summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Qalam.ContentTeam.v1");
    private readonly string _file;
    private string? _cached;

    public SecretStore(string dataFolder) => _file = Path.Combine(dataFolder, "secret.bin");

    public bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

    public string? GetApiKey()
    {
        if (_cached != null) return _cached;
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!File.Exists(_file)) return string.IsNullOrWhiteSpace(env) ? null : env;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
            return _cached = Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return string.IsNullOrWhiteSpace(env) ? null : env;
        }
    }

    public void SetApiKey(string? key)
    {
        _cached = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (File.Exists(_file)) File.Delete(_file);
            return;
        }
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_file, data);
    }

    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "(not set)" : key.Length <= 12 ? "••••" : key[..7] + "••••" + key[^4..];
}
