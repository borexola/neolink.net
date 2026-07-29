// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Runtime.InteropServices;
using System.Text;

namespace Neolink.Desktop;

/// <summary>
/// Windows DPAPI, straight off crypt32 — no NuGet package for forty lines of
/// interop. Protects the saved server password and session token with the
/// LOGGED-IN USER's key: the settings file is unreadable by other accounts and
/// worthless if copied to another machine.
/// Every method is null-tolerant and never throws: a settings file that cannot
/// be decrypted (restored from another PC, corrupted) must degrade to "sign in
/// again", not to a crash on startup.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CryptprotectUiForbidden = 0x1;

    /// <summary>Plaintext to a base64 DPAPI blob. Null/empty in, null out.</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = Encoding.UTF8.GetBytes(plain);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { cbData = bytes.Length, pbData = handle.AddrOfPinnedObject() };
            if (!CryptProtectData(ref input, "Neolink.NET Desktop", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptprotectUiForbidden, out var output))
                return null;
            try
            {
                var blob = new byte[output.cbData];
                Marshal.Copy(output.pbData, blob, 0, output.cbData);
                return Convert.ToBase64String(blob);
            }
            finally { LocalFree(output.pbData); }
        }
        catch { return null; }
        finally
        {
            Array.Clear(bytes);
            handle.Free();
        }
    }

    /// <summary>A base64 DPAPI blob back to plaintext. Anything unreadable —
    /// wrong user, wrong machine, junk — comes back null.</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        byte[] blob;
        try { blob = Convert.FromBase64String(protectedBase64); }
        catch { return null; }
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { cbData = blob.Length, pbData = handle.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptprotectUiForbidden, out var output))
                return null;
            try
            {
                var plain = new byte[output.cbData];
                Marshal.Copy(output.pbData, plain, 0, output.cbData);
                var text = Encoding.UTF8.GetString(plain);
                Array.Clear(plain);
                return text;
            }
            finally { LocalFree(output.pbData); }
        }
        catch { return null; }
        finally { handle.Free(); }
    }
}
