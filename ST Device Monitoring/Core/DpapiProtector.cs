using System.Runtime.InteropServices;
using System.Text;

namespace ST_Device_Monitoring.Core;

/// <summary>
/// Protects small secrets (the SMTP password) with the Windows DPAPI for the current user,
/// so devices.json never contains a clear-text password. Uses crypt32 directly - no NuGet package.
/// The value can only be read back by the same Windows user on the same machine.
/// </summary>
public static class DpapiProtector
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Returns a base64 string, or "" for empty input.</summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var input = new DATA_BLOB();
        var output = new DATA_BLOB();
        try
        {
            input.pbData = Marshal.AllocHGlobal(bytes.Length);
            input.cbData = bytes.Length;
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);

            if (!CryptProtectData(ref input, "ST Device Monitoring", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output))
                return string.Empty;

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return Convert.ToBase64String(result);
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    /// <summary>Returns the clear text, or "" if it cannot be decrypted (other user/machine).</summary>
    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return string.Empty;

        var input = new DATA_BLOB();
        var output = new DATA_BLOB();
        try
        {
            var bytes = Convert.FromBase64String(protectedBase64);
            input.pbData = Marshal.AllocHGlobal(bytes.Length);
            input.cbData = bytes.Length;
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output))
                return string.Empty;

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return Encoding.UTF8.GetString(result);
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
