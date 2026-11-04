using Microsoft.Win32;
using System;
using System.Globalization;

namespace Layer8
{
    /// <summary>
    /// The settings on Layer8 Settings, kept per Windows user in HKCU\SOFTWARE\Layer8 (the key the log level
    /// already lives in). Every read goes to the registry, so a change applies to the next action without
    /// restarting the console. A missing or unreadable value gives the default; one out of range is clamped.
    /// </summary>
    public static class Layer8Settings
    {
        public const int DefaultDevicesAtOnce = 32;
        public const int MinDevicesAtOnce = 1;
        public const int MaxDevicesAtOnce = 256;

        public const bool DefaultCheckReachable = true;

        public const int DefaultReachableSeconds = 2;
        public const int MinReachableSeconds = 1;
        public const int MaxReachableSeconds = 30;

        private const string DevicesAtOnceValue = "DevicesAtOnce";
        private const string CheckReachableValue = "CheckReachable";
        private const string ReachableSecondsValue = "ReachableSeconds";

        /// <summary>How many devices an action on many devices contacts at the same time.</summary>
        public static int DevicesAtOnce
        {
            get { return ReadInt(DevicesAtOnceValue, DefaultDevicesAtOnce, MinDevicesAtOnce, MaxDevicesAtOnce); }
            set { WriteInt(DevicesAtOnceValue, Clamp(value, MinDevicesAtOnce, MaxDevicesAtOnce)); }
        }

        /// <summary>
        /// Before connecting to a device, check that it answers on port 135 (WMI) or 445 (file sharing). A device
        /// that answers on neither is listed as offline within a couple of seconds, instead of holding one of the
        /// slots for the 20 seconds or more a WMI connection takes to give up.
        /// </summary>
        public static bool CheckReachable
        {
            get { return ReadInt(CheckReachableValue, DefaultCheckReachable ? 1 : 0, 0, 1) == 1; }
            set { WriteInt(CheckReachableValue, value ? 1 : 0); }
        }

        /// <summary>How long the offline check waits for a device to answer.</summary>
        public static int ReachableSeconds
        {
            get { return ReadInt(ReachableSecondsValue, DefaultReachableSeconds, MinReachableSeconds, MaxReachableSeconds); }
            set { WriteInt(ReachableSecondsValue, Clamp(value, MinReachableSeconds, MaxReachableSeconds)); }
        }

        /// <summary>The log level. Layer8Log reads the same value and picks a change up within a few seconds.</summary>
        public static LogLevel LogLevel
        {
            get { return Layer8Log.Level; }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Layer8Log.RegistryKeyPath))
                {
                    key.SetValue(Layer8Log.RegistryValueName, value.ToString(), RegistryValueKind.String);
                }
            }
        }

        private static int ReadInt(string name, int fallback, int min, int max)
        {
            object value = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Layer8Log.RegistryKeyPath))
                {
                    value = key?.GetValue(name);
                }
            }
            catch (Exception)
            {
                // No access to the key: use the default.
            }

            int number;
            if (value is int dword)
            {
                number = dword;
            }
            else if (!(value is string text) || !int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return fallback;
            }
            return Clamp(number, min, max);
        }

        private static void WriteInt(string name, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Layer8Log.RegistryKeyPath))
            {
                key.SetValue(name, value, RegistryValueKind.DWord);
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
