using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CommunityRelay;

public sealed class SecretStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private readonly string _path;
    private readonly object _sync = new();

    public SecretStore(string? path = null)
    {
        _path = path ?? AppPaths.SecretsFile;
    }

    public string? GetClientSecret() => Load().ClientSecret;
    public string? GetRefreshToken() => Load().RefreshToken;

    public void SetClientSecret(string? value)
    {
        lock (_sync)
        {
            var bundle = LoadUnsafe();
            bundle.ClientSecret = NullIfWhiteSpace(value);
            SaveUnsafe(bundle);
        }
    }

    public void SetRefreshToken(string? value)
    {
        lock (_sync)
        {
            var bundle = LoadUnsafe();
            bundle.RefreshToken = NullIfWhiteSpace(value);
            SaveUnsafe(bundle);
        }
    }

    public void ClearAuthorization()
    {
        lock (_sync)
        {
            SecretBundle bundle;
            try
            {
                bundle = LoadUnsafe();
            }
            catch (InvalidOperationException)
            {
                // An unreadable DPAPI blob cannot be edited safely. Removing it is the
                // only way to guarantee that no stale authorization remains.
                File.Delete(_path);
                return;
            }
            bundle.RefreshToken = null;
            SaveUnsafe(bundle);
        }
    }

    public void EnsureReadable() => _ = Load();

    public void ResetAll()
    {
        lock (_sync)
        {
            File.Delete(_path);
        }
    }

    public bool HasClientSecret => !string.IsNullOrWhiteSpace(GetClientSecret());
    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(GetRefreshToken());

    private SecretBundle Load()
    {
        lock (_sync)
        {
            return LoadUnsafe();
        }
    }

    private SecretBundle LoadUnsafe()
    {
        if (!File.Exists(_path))
        {
            return new SecretBundle();
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var json = System.Text.Encoding.UTF8.GetString(Unprotect(protectedBytes));
            return JsonSerializer.Deserialize<SecretBundle>(json) ?? new SecretBundle();
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or CryptographicException or Win32Exception)
        {
            throw new InvalidOperationException(
                "The encrypted authorization store could not be read by this Windows user. " +
                "Reset the local authorization store and authorize again.",
                exception);
        }
    }

    private void SaveUnsafe(SecretBundle bundle)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(bundle);
        var protectedBytes = Protect(System.Text.Encoding.UTF8.GetBytes(json));
        AtomicFiles.WriteAllBytes(_path, protectedBytes);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Community Relay secrets require Windows DPAPI.");
        }

        using var input = NativeBlob.FromBytes(plaintext);
        if (!CryptProtectData(
                ref input.Blob,
                "Community Relay POC OAuth secrets",
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            return CopyBlob(output);
        }
        finally
        {
            LocalFree(output.Data);
        }
    }

    private static byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Community Relay secrets require Windows DPAPI.");
        }

        using var input = NativeBlob.FromBytes(ciphertext);
        if (!CryptUnprotectData(
                ref input.Blob,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            return CopyBlob(output);
        }
        finally
        {
            LocalFree(output.Data);
        }
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        var bytes = new byte[blob.Length];
        if (blob.Length > 0)
        {
            Marshal.Copy(blob.Data, bytes, 0, blob.Length);
        }
        return bytes;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    private sealed class NativeBlob : IDisposable
    {
        public DataBlob Blob;

        private NativeBlob(byte[] bytes)
        {
            Blob = new DataBlob
            {
                Length = bytes.Length,
                Data = Marshal.AllocHGlobal(bytes.Length)
            };
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, Blob.Data, bytes.Length);
            }
        }

        public static NativeBlob FromBytes(byte[] bytes) => new(bytes);

        public void Dispose()
        {
            if (Blob.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Blob.Data);
                Blob.Data = IntPtr.Zero;
                Blob.Length = 0;
            }
        }
    }

    private sealed class SecretBundle
    {
        public string? ClientSecret { get; set; }
        public string? RefreshToken { get; set; }
    }
}
