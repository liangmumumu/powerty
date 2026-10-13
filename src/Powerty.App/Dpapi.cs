using System.Runtime.InteropServices;
using System.Text;

namespace Powerty.App;

/// <summary>Windows Data Protection API wrapper (current-user scope), used to keep the
/// AI API key out of plain-text settings files. Implemented with a direct crypt32.dll
/// P/Invoke so the project keeps its zero-NuGet-dependency property.</summary>
internal static class Dpapi
{
    public const string Prefix = "dpapi:";

    /// <summary>Returns "dpapi:&lt;base64&gt;", or the input unchanged when protection is
    /// unavailable (empty key), or null when protection failed (caller falls back).</summary>
    public static string? Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return plaintext;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var inputData = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, inputData, bytes.Length);
            var input = new DataBlob { Length = bytes.Length, Data = inputData };
            if (!CryptProtectData(ref input, "Powerty", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
            {
                return null;
            }

            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return Prefix + Convert.ToBase64String(result);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputData);
        }
    }

    /// <summary>Reverses <see cref="Protect"/>. Legacy plain-text values (no prefix) are
    /// returned unchanged; failures degrade to an empty key instead of throwing.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            var inputData = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, inputData, bytes.Length);
                var input = new DataBlob { Length = bytes.Length, Data = inputData };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                {
                    return string.Empty;
                }

                try
                {
                    var result = new byte[output.Length];
                    Marshal.Copy(output.Data, result, 0, output.Length);
                    return Encoding.UTF8.GetString(result);
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inputData);
            }
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
