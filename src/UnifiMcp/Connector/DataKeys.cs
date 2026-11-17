using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace UnifiMcp;

/// <summary>
/// The DataProtection keyring in <c>&lt;DataDir&gt;/keys</c>, each key's XML sealed with AES-256-GCM under a key derived
/// from the deployment-supplied secret <c>UNIFI_MCP_DATA_KEY</c>. A data key that does not open the existing keyring breaks the
/// public side only (503) and switches key generation off, so the files on disk are never replaced or rewritten.
/// </summary>
public static class DataKeys
{
    public const string ApplicationName = "unifi-mcp";
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("unifi-mcp keyring v1");

    /// <summary>The 32-byte keyring key: HKDF-SHA256 over the UTF-8 data key, empty salt, info <c>unifi-mcp keyring v1</c>.</summary>
    public static byte[] Derive(string dataKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(dataKey), 32, salt: [], info: Info);

    public static string KeysDir(Settings s) => Path.Combine(s.DataDir, "keys");

    /// <summary>
    /// Tries to open every <c>key-*.xml</c> with <paramref name="key"/>. Only reads: a missing or empty directory is fine
    /// (the first Protect creates the keyring); a file where the directory should be is not. The reason names the
    /// directory, never a key value. A directory that cannot be listed throws; the caller turns that into a reason.
    /// </summary>
    public static (bool Ok, string? Reason) Check(string keysDir, byte[] key)
    {
        if (!Directory.Exists(keysDir))
            return File.Exists(keysDir) ? (false, $"cannot read {keysDir} (not a directory)") : (true, null);
        foreach (var file in Directory.EnumerateFiles(keysDir, "key-*.xml"))
        {
            XElement root;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                root = XElement.Load(stream);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
            {
                return (false, $"cannot read {file}");
            }

            // DataProtection wraps each encrypted part as <encryptedSecret decryptorType="…"><aesGcm …/></encryptedSecret>
            // (encryptedSecret in its own XML namespace, ours in none). A key with no sealed part at all was not written
            // by this keyring, so this data key cannot vouch for it.
            var sealedParts = root.Descendants().Where(e => e.Name.LocalName == "encryptedSecret")
                .Select(e => e.Element(AesGcmXml.ElementName)).ToList();
            if (sealedParts.Count == 0 || sealedParts.Any(p => p is null || !AesGcmXml.TryOpen(p, key, out _)))
                return (false, $"UNIFI_MCP_DATA_KEY does not match {keysDir}");
        }
        return (true, null);
    }

    /// <summary>
    /// DataProtection for the connector: application name <c>unifi-mcp</c>, keys in <c>&lt;DataDir&gt;/keys</c>, sealed with
    /// the derived key. On a mismatch the connector is marked broken and no key is ever generated.
    /// </summary>
    public static void AddProtectedDataProtection(IServiceCollection services, Settings s, ConnectorState state)
    {
        var keysDir = KeysDir(s);
        var key = Derive(s.DataKey ?? throw new InvalidOperationException("UNIFI_MCP_DATA_KEY is not set."));
        services.AddSingleton(new KeyringKey(key));

        var dp = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
            .AddKeyManagementOptions(o => o.XmlEncryptor = new AesGcmXmlEncryptor(key));

        // A folder that cannot be listed (wrong owner, an unreadable mount) is the public side's problem only: the host
        // is still being built here, and an exception would take the tailnet socket down with it.
        bool ok;
        string? reason;
        bool unreadable;
        try
        {
            (ok, reason) = Check(keysDir, key);
            unreadable = !ok && reason!.StartsWith("cannot read ", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            (ok, reason, unreadable) = (false, $"cannot read {keysDir} ({e.GetType().Name})", true);
        }
        if (!ok)
        {
            // A data key mismatch is reported by the startup summary, as before; an unreadable keyring also gets a Warning.
            state.Broken(reason!, logAtStart: unreadable);
            dp.DisableAutomaticKeyGeneration();
        }
    }
}

/// <summary>The derived keyring key, in DI for <see cref="AesGcmXmlDecryptor"/>. ToString() prints only the type name.</summary>
public sealed class KeyringKey(byte[] key)
{
    internal byte[] Value { get; } = key;
    public override string ToString() => nameof(KeyringKey);
}

/// <summary>The sealed element: <c>&lt;aesGcm version="1"&gt;&lt;nonce/&gt;&lt;tag/&gt;&lt;ciphertext/&gt;&lt;/aesGcm&gt;</c>, each part base64.</summary>
internal static class AesGcmXml
{
    public const string ElementName = "aesGcm";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static XElement Seal(XElement plaintext, byte[] key)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);
        return new XElement(ElementName,
            new XAttribute("version", "1"),
            new XElement("nonce", Convert.ToBase64String(nonce)),
            new XElement("tag", Convert.ToBase64String(tag)),
            new XElement("ciphertext", Convert.ToBase64String(cipher)));
    }

    public static bool TryOpen(XElement sealedElement, byte[] key, out XElement? plaintext)
    {
        plaintext = null;
        try
        {
            if ((string?)sealedElement.Attribute("version") != "1") return false;
            var nonce = Convert.FromBase64String((string?)sealedElement.Element("nonce") ?? "");
            var tag = Convert.FromBase64String((string?)sealedElement.Element("tag") ?? "");
            var cipher = Convert.FromBase64String((string?)sealedElement.Element("ciphertext") ?? "");
            if (nonce.Length != NonceSize || tag.Length != TagSize) return false;
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain);
            plaintext = XElement.Parse(Encoding.UTF8.GetString(plain));
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or XmlException)
        {
            return false;
        }
    }
}

/// <summary>Seals each key's secret XML with AES-256-GCM under the derived keyring key, a fresh 12-byte nonce each time.</summary>
public sealed class AesGcmXmlEncryptor(byte[] key) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
        new(AesGcmXml.Seal(plaintextElement, key), typeof(AesGcmXmlDecryptor));
}

/// <summary>
/// Opens what <see cref="AesGcmXmlEncryptor"/> sealed. DataProtection creates decryptors itself, through a constructor
/// taking <see cref="IServiceProvider"/>, so the key comes from DI.
/// </summary>
public sealed class AesGcmXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    private readonly byte[] _key = services.GetRequiredService<KeyringKey>().Value;

    public XElement Decrypt(XElement encryptedElement) =>
        AesGcmXml.TryOpen(encryptedElement, _key, out var plain)
            ? plain!
            : throw new CryptographicException("UNIFI_MCP_DATA_KEY does not open this key.");
}
