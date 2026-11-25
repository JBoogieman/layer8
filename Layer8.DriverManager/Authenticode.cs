using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

        /// <summary>Dell signs its catalog as "Dell Inc" (or another Dell company name).</summary>
        public static readonly Regex DellSigner = new Regex(@"^Dell\b.*$", RegexOptions.IgnoreCase);

        /// <summary>Lenovo signs its driver packs as "Lenovo" (or a Lenovo company such as "Lenovo (United States) Inc.").</summary>
        public static readonly Regex LenovoSigner = new Regex(@"^Lenovo\b.*$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Opens the file so that nothing can change, replace, rename or delete it until the handle is closed. Other
        /// programs can still read and run it. Held from the checks until the vendor's program has finished, so the
        /// file that runs is the one that was checked, even in a download folder other accounts can write to.
        /// </summary>
        public static FileStream Lock(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        /// <summary>
        /// Throws unless Windows trusts the file's signature and its signer's name matches <paramref name="signer"/>.
        /// </summary>
        public static void RequireSignedBy(string path, Regex signer, string vendor, string refusal = "run it")
        {
            int result = Verify(path);
            if (result != 0)
            {
                throw new InvalidOperationException(string.Format(
                    "{0} isn't validly signed (WinVerifyTrust 0x{1:X8}), so Layer8 won't {2}. Delete it and download it again.",
                    path, result, refusal));
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
                throw new InvalidOperationException(path + " has no readable signature, so Layer8 won't " + refusal + ": " + ex.Message, ex);
            }
            if (!signer.IsMatch(name ?? string.Empty))
            {
                throw new InvalidOperationException(string.Format(
                    "{0} is signed by \"{1}\", not {2}, so Layer8 won't {3}.", path, name, vendor, refusal));
            }
        }

        /// <summary>
        /// Unpacks a vendor's catalog .cab once its signature checks out: the list of packs, and for Dell the hash
        /// each pack is checked against, come from it. Locked between the check and the unpacking.
        /// </summary>
        public static string[] ExtractSignedCab(string cabPath, string destinationFolder, Regex signer, string vendor)
        {
            using (Lock(cabPath))
            {
                RequireSignedBy(cabPath, signer, vendor, "use it");
                return Utility.ExtractCab(cabPath, destinationFolder, clearDestination: true);
            }
        }

        /// <summary>
        /// Deletes a catalog copy that failed its check, so the wizard downloads it again next time instead of
        /// finding the same copy.
        /// </summary>
        public static void Discard(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Compares a download with the hash in the vendor's catalog: SHA-256 (64 hex digits) or MD5 (32). A catalog
        /// entry without a hash passes unless <paramref name="required"/>, for packs nothing else checks.
        /// </summary>
        public static void RequireHash(string path, string expected, string vendor, bool required)
        {
            expected = (expected ?? string.Empty).Trim();
            HashAlgorithm algorithm = expected.Length == 64 ? (HashAlgorithm)SHA256.Create() : expected.Length == 32 ? MD5.Create() : null;
            if (algorithm == null)
            {
                if (required) throw new InvalidOperationException(string.Format("{0}'s catalog has no hash for {1}, so Layer8 can't check it and won't use it.", vendor, path));
                return;
            }
            using (algorithm)
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string actual = BitConverter.ToString(algorithm.ComputeHash(file)).Replace("-", string.Empty);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Format("{0} doesn't match the hash in {1}'s catalog, so Layer8 won't use it. Delete it and download it again.", path, vendor));
                }
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

        // From System32 only. The default search looks in the console's folder and Layer8's own first, so a
        // wintrust.dll put there would be the one that decides whether a download is trusted.
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
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
