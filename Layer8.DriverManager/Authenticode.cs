using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Layer8.DriverManager
{
    /// <summary>
    /// Checks a downloaded file's Authenticode signature before Layer8 runs it: Windows has to trust the signature
    /// (WinVerifyTrust, the same check as a file's Digital Signatures tab), and the signer has to be the expected
    /// vendor. A file swapped in on the way (a tampered download, or a file left in the download folder) fails.
    /// </summary>
    internal static class Authenticode
    {
        /// <summary>HP signs its SoftPaqs as "HP Inc." (older ones "Hewlett-Packard Company").</summary>
        public static readonly Regex HPSigner = new Regex(@"^(HP Inc\.?|HP Development Company.*|Hewlett[- ]Packard.*)$", RegexOptions.IgnoreCase);

        /// <summary>Lenovo signs its driver packs as "Lenovo" (or a Lenovo company such as "Lenovo (United States) Inc.").</summary>
        public static readonly Regex LenovoSigner = new Regex(@"^Lenovo\b.*$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Throws unless Windows trusts the file's signature and its signer's name matches <paramref name="signer"/>.
        /// </summary>
        public static void RequireSignedBy(string path, Regex signer, string vendor)
        {
            int result = Verify(path);
            if (result != 0)
            {
                throw new InvalidOperationException(string.Format(
                    "{0} isn't validly signed (WinVerifyTrust 0x{1:X8}), so Layer8 won't run it. Delete it and download it again.",
                    path, result));
            }

            string name;
            try
            {
                using (X509Certificate2 certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    name = certificate.GetNameInfo(X509NameType.SimpleName, false);
                }
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new InvalidOperationException(path + " has no readable signature, so Layer8 won't run it: " + ex.Message, ex);
            }
            if (!signer.IsMatch(name ?? string.Empty))
            {
                throw new InvalidOperationException(string.Format(
                    "{0} is signed by \"{1}\", not {2}, so Layer8 won't run it.", path, name, vendor));
            }
        }

        /// <summary>True for an absolute http or https link: safe to hand to the shell to open in a browser.</summary>
        public static bool IsWebLink(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri link) &&
                   (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp);
        }

        private static int Verify(string path)
        {
            WINTRUST_FILE_INFO file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = path
            };
            IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(file, filePointer, false);
                WINTRUST_DATA data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = filePointer,
                    dwStateAction = WTD_STATEACTION_VERIFY
                };
                int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);

                // Release what the verify step kept open.
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure(filePointer, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(filePointer);
            }
        }

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            [MarshalAs(UnmanagedType.LPWStr)] public string pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
