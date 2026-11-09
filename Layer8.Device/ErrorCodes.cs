using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Layer8.Device
{
    /// <summary>
    /// What an error code means, in words. Three places are asked in turn:
    ///   1. ConfigMgr's own catalog, SrsResources.dll in the console's bin folder: the text the console's reports
    ///      show for its 0x87D... codes, and for many Windows, Windows Update and installer codes too.
    ///   2. A few codes Layer8 knows itself, for when SrsResources isn't there.
    ///   3. Windows' own message text (FormatMessage), for Windows and installer codes such as 1603 or 0x80070005.
    /// </summary>
    internal static class ErrorCodes
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(ErrorCodes));

        // ConfigMgr codes Windows has no text for, among the ones admins see most.
        private static readonly Dictionary<uint, string> Known = new Dictionary<uint, string>
        {
            { 0x87D00213, "Timeout occurred" },
            { 0x87D00324, "The application was not detected after installation completed" },
            { 0x87D00325, "The application was still detected after uninstall completed" },
            { 0x87D00327, "Script is not signed" },
            { 0x87D00607, "Content not found" },
            { 0x87D01106, "Failed to verify the executable file is valid or to construct the associated command line" },
        };

        private static readonly Lazy<MethodInfo> SrsLookup = new Lazy<MethodInfo>(LoadSrsResources);

        /// <summary>Small codes are installer and Windows exit codes, so they read best in decimal too: "1603 (0x643)".</summary>
        public static string Format(uint code)
        {
            return code < 0x10000 ? string.Format("{0} (0x{0:X})", code) : string.Format("0x{0:X8}", code);
        }

        /// <summary>The code's meaning, or "No description for this code".</summary>
        public static string Describe(uint code)
        {
            if (code == 0) return "Success (no error)";
            return FromConfigMgr(code) ?? (Known.TryGetValue(code, out string known) ? known : null) ?? FromWindows(code) ?? "No description for this code";
        }

        /// <summary>"0x87D00324: The application was not detected after installation completed".</summary>
        public static string FormatAndDescribe(uint code)
        {
            return Format(code) + ": " + Describe(code);
        }

        /// <summary>
        /// Reads a code however it was copied: 0x87D00324, 87D00324 (hex letters), -2016410844 (a signed HRESULT from
        /// a log or a report), 2278556452, or 1603. False when it isn't a code.
        /// </summary>
        public static bool TryParse(string text, out uint code)
        {
            code = 0;
            text = (text ?? string.Empty).Trim().Replace(" ", string.Empty);
            if (text.Length == 0) return false;

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
            }
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
            {
                if (number < int.MinValue || number > uint.MaxValue) return false;
                code = unchecked((uint)number);
                return true;
            }
            // Hex without 0x, recognisable by its letters.
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
        }

        private static string FromConfigMgr(uint code)
        {
            MethodInfo lookup = SrsLookup.Value;
            if (lookup == null) return null;
            // SrsResources takes the code as text, signed the way ConfigMgr's database stores it.
            string id = unchecked((int)code).ToString(CultureInfo.InvariantCulture);
            foreach (string language in new[] { CultureInfo.CurrentUICulture.Name, "en-US" })
            {
                if (string.IsNullOrEmpty(language)) continue;
                try
                {
                    string text = lookup.Invoke(null, new object[] { id, language }) as string;
                    if (Useful(text)) return Clean(text);
                }
                catch (Exception ex)
                {
                    log.Debug(string.Format("SrsResources has no text for 0x{0:X8} in {1}: {2}", code, language, ex.GetBaseException().Message));
                }
            }
            return null;
        }

        private static string FromWindows(uint code)
        {
            // The code as it is, then its Windows part when it's a Windows error wrapped in an HRESULT (0x80070643 is 1603).
            List<int> candidates = new List<int> { unchecked((int)code) };
            if ((code & 0xFFFF0000) == 0x80070000) candidates.Add((int)(code & 0xFFFF));
            foreach (int candidate in candidates)
            {
                string text = new Win32Exception(candidate).Message;
                if (Useful(text)) return Clean(text);
            }
            return null;
        }

        private static bool Useful(string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   !text.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                   !text.StartsWith("Unknown error", StringComparison.OrdinalIgnoreCase) &&
                   text.IndexOf("cannot find message text", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string Clean(string text)
        {
            text = text.Trim().TrimEnd('.', ' ');
            // ConfigMgr's catalog marks Windows Update Agent errors "WUA:".
            return text.StartsWith("WUA:", StringComparison.Ordinal) ? "Windows Update: " + text.Substring(4).TrimStart() : text;
        }

        /// <summary>
        /// SrsResources.Localization.GetErrorMessage(string, string) from the console's own bin folder, found by
        /// reflection so Layer8 builds without it. Null when it isn't there (Layer8 then uses the other two places).
        /// </summary>
        private static MethodInfo LoadSrsResources()
        {
            try
            {
                List<string> folders = new List<string> { AppDomain.CurrentDomain.BaseDirectory };
                string adminUi = Environment.GetEnvironmentVariable("SMS_ADMIN_UI_PATH");
                // SMS_ADMIN_UI_PATH is the console's bin\i386 folder; SrsResources.dll is in bin.
                if (!string.IsNullOrEmpty(adminUi)) folders.Add(Path.GetDirectoryName(adminUi.TrimEnd('\\')));
                foreach (string folder in folders)
                {
                    string path = Path.Combine(folder ?? string.Empty, "SrsResources.dll");
                    if (!File.Exists(path)) continue;
                    Assembly srs = Assembly.LoadFrom(path);
                    Type localization = srs.GetType("SrsResources.Localization");
                    MethodInfo method = localization?.GetMethod("GetErrorMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
                    if (method == null) continue;
                    string catalog = UseConsoleCatalog(srs, localization, folder);
                    log.Info(catalog != null ? "Error text from the console's catalog in " + catalog : "Error text from " + path);
                    return method;
                }
                log.Info("No SrsResources.dll with GetErrorMessage; error text from Layer8's list and Windows only");
            }
            catch (Exception ex)
            {
                log.Info("Couldn't load SrsResources.dll; error text from Layer8's list and Windows only: " + ex.GetBaseException().Message);
            }
            return null;
        }

        /// <summary>
        /// SrsResources reads its error catalog from wherever its resource location points. Until the console sets
        /// one, that's a report server's folder, which a console-only machine doesn't have, so every lookup comes
        /// back empty. When the current location has no catalog, point it at the console's own (bin\resources).
        /// Returns the catalog folder in use, or null when it can't tell.
        /// </summary>
        private static string UseConsoleCatalog(Assembly srs, Type localization, string folder)
        {
            try
            {
                Type locationType = srs.GetType("SrsResources.ResourceLocation");
                if (locationType == null) return null;
                PropertyInfo errorPath = locationType.GetProperty("ErrorResourcePath");
                FieldInfo current = localization.GetField("resourceLocation", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo setLocation = localization.GetMethod("SetResourceLocation", BindingFlags.Public | BindingFlags.Static, null, new[] { locationType }, null);
                if (errorPath == null || current == null || setLocation == null) return null;

                object location = current.GetValue(null);
                if (location == null)
                {
                    // Nothing has set one yet: start from SrsResources' own default, as its first lookup would.
                    location = srs.GetType("SrsResources.ResourceLocationFactory")?
                        .GetMethod("CreateResourceLocation", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?
                        .Invoke(null, null);
                    if (location == null) return null;
                    setLocation.Invoke(null, new[] { location });
                }

                string existing = errorPath.GetValue(location) as string;
                if (!string.IsNullOrEmpty(existing) && Directory.Exists(existing)) return existing;

                string catalog = Path.Combine(folder, "resources");
                if (!Directory.Exists(catalog)) return null;
                errorPath.SetValue(location, catalog);
                return catalog;
            }
            catch (Exception ex)
            {
                log.Debug("Couldn't point SrsResources at the console's error catalog: " + ex.GetBaseException().Message);
                return null;
            }
        }
    }
}
