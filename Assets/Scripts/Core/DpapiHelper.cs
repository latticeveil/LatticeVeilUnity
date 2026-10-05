using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LatticeVeil.Core
{
    /// <summary>
    /// Windows DPAPI implementation using crypt32.dll.
    /// 100% binary compatible with System.Security.Cryptography.ProtectedData (CurrentUser scope).
    /// </summary>
    public static class DpapiHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            StringBuilder ppszDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static byte[] Protect(byte[] userData)
        {
            if (userData == null || userData.Length == 0)
                return Array.Empty<byte>();

            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            var emptyBlob = new DATA_BLOB();
            var handle = GCHandle.Alloc(userData, GCHandleType.Pinned);

            try
            {
                inBlob.cbData = userData.Length;
                inBlob.pbData = handle.AddrOfPinnedObject();

                if (!CryptProtectData(ref inBlob, null, ref emptyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException($"DPAPI CryptProtectData failed with error code: {error}");
                }

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                handle.Free();
                if (outBlob.pbData != IntPtr.Zero)
                {
                    LocalFree(outBlob.pbData);
                }
            }
        }

        public static byte[] Unprotect(byte[] encryptedData)
        {
            if (encryptedData == null || encryptedData.Length == 0)
                return Array.Empty<byte>();

            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            var emptyBlob = new DATA_BLOB();
            var handle = GCHandle.Alloc(encryptedData, GCHandleType.Pinned);

            try
            {
                inBlob.cbData = encryptedData.Length;
                inBlob.pbData = handle.AddrOfPinnedObject();

                if (!CryptUnprotectData(ref inBlob, null, ref emptyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException($"DPAPI CryptUnprotectData failed with error code: {error}");
                }

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                handle.Free();
                if (outBlob.pbData != IntPtr.Zero)
                {
                    LocalFree(outBlob.pbData);
                }
            }
        }
    }
}
