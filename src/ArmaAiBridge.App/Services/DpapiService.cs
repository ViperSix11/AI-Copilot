using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ArmaAiBridge.App.Services;

public static class DpapiService
{
    private const int CryptProtectUiForbidden = 0x1;

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        DATA_BLOB input = CreateBlob(bytes);
        DATA_BLOB output = default;
        try
        {
            if (!CryptProtectData(ref input, "ArmA AI Bridge API credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Convert.ToBase64String(CopyBlob(output));
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(output, localFree: true);
        }
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return string.Empty;
        byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
        DATA_BLOB input = CreateBlob(protectedBytes);
        DATA_BLOB output = default;
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Encoding.UTF8.GetString(CopyBlob(output));
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(output, localFree: true);
        }
    }

    private static DATA_BLOB CreateBlob(byte[] data)
    {
        DATA_BLOB blob = new() { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] CopyBlob(DATA_BLOB blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero) return Array.Empty<byte>();
        byte[] data = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, data, 0, blob.cbData);
        return data;
    }

    private static void FreeBlob(DATA_BLOB blob, bool localFree = false)
    {
        if (blob.pbData == IntPtr.Zero) return;
        if (localFree) _ = LocalFree(blob.pbData); else Marshal.FreeHGlobal(blob.pbData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
