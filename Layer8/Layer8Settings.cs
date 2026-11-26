using Microsoft.Win32;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

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

        public const int DefaultConfirmAbove = 100;
        public const int MinConfirmAbove = 0;
        public const int MaxConfirmAbove = 100000;

        public const bool DefaultCheckReachable = true;

        public const int DefaultReachableSeconds = 2;
        public const int MinReachableSeconds = 1;
        public const int MaxReachableSeconds = 30;

        public const int DefaultLowDiskGB = 10;
        public const int MinLowDiskGB = 1;
        public const int MaxLowDiskGB = 1000;

        public const int DefaultLongUptimeDays = 14;
        public const int MinLongUptimeDays = 1;
        public const int MaxLongUptimeDays = 365;

        public const int DefaultNotReportingDays = 7;
        public const int MinNotReportingDays = 1;
        public const int MaxNotReportingDays = 365;

        public const int DefaultBlueScreenDays = 14;
        public const int MinBlueScreenDays = 1;
        public const int MaxBlueScreenDays = 365;

        /// <summary>Two of ConfigMgr's default software update scan cycles (every 7 days): one late scan doesn't count.</summary>
        public const int DefaultNotScanningDays = 14;
        public const int MinNotScanningDays = 1;
        public const int MaxNotScanningDays = 365;

        /// <summary>
        /// ConfigMgr's own default for the same question, twice over: AD System Discovery's "only computers that
        /// logged on in" filter and the Delete Aged Discovery Data task both start at 90 days (read on the lab's site).
        /// </summary>
        public const int DefaultADNotSeenDays = 90;
        public const int MinADNotSeenDays = 1;
        public const int MaxADNotSeenDays = 3650;

        /// <summary>Windows 11 24H2. A build alone, or a build and update revision ("26100.4652").</summary>
        public const string DefaultWindowsBuild = "26100";
        /// <summary>Windows 10's first release: nothing older reports a build this dashboard can compare.</summary>
        public const int MinWindowsBuild = 10240;

        private const string DevicesAtOnceValue = "DevicesAtOnce";
        private const string ConfirmAboveValue = "ConfirmAbove";
        private const string CheckReachableValue = "CheckReachable";
        private const string ReachableSecondsValue = "ReachableSeconds";
        private const string LowDiskGBValue = "LowDiskGB";
        private const string LongUptimeDaysValue = "LongUptimeDays";
        private const string NotReportingDaysValue = "NotReportingDays";
        private const string BlueScreenDaysValue = "BlueScreenDays";
        private const string NotScanningDaysValue = "NotScanningDays";
        private const string WindowsBuildValue = "WindowsBuild";
        private const string ADNotSeenDaysValue = "ADNotSeenDays";

        /// <summary>How many devices an action on many devices contacts at the same time.</summary>
        public static int DevicesAtOnce
        {
            get { return ReadInt(DevicesAtOnceValue, DefaultDevicesAtOnce, MinDevicesAtOnce, MaxDevicesAtOnce); }
            set { WriteInt(DevicesAtOnceValue, Clamp(value, MinDevicesAtOnce, MaxDevicesAtOnce)); }
        }

        /// <summary>
        /// Ask before an action runs on more devices than this (0 = never ask). Machine Policy on All Systems is
        /// one click; this makes a fleet-wide run a deliberate choice.
        /// </summary>
        public static int ConfirmAbove
        {
            get { return ReadInt(ConfirmAboveValue, DefaultConfirmAbove, MinConfirmAbove, MaxConfirmAbove); }
            set { WriteInt(ConfirmAboveValue, Clamp(value, MinConfirmAbove, MaxConfirmAbove)); }
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

        /// <summary>Health Dashboard: a device with less free space than this on C: (in GB) counts as low on disk.</summary>
        public static int LowDiskGB
        {
            get { return ReadInt(LowDiskGBValue, DefaultLowDiskGB, MinLowDiskGB, MaxLowDiskGB); }
            set { WriteInt(LowDiskGBValue, Clamp(value, MinLowDiskGB, MaxLowDiskGB)); }
        }

        /// <summary>Health Dashboard: a device up this many days or more without a restart counts as up too long.</summary>
        public static int LongUptimeDays
        {
            get { return ReadInt(LongUptimeDaysValue, DefaultLongUptimeDays, MinLongUptimeDays, MaxLongUptimeDays); }
            set { WriteInt(LongUptimeDaysValue, Clamp(value, MinLongUptimeDays, MaxLongUptimeDays)); }
        }

        /// <summary>
        /// Health Dashboard: a client the site hasn't had a policy request from in this many days counts as not
        /// reporting. Seven matches ConfigMgr's own default for client activity.
        /// </summary>
        public static int NotReportingDays
        {
            get { return ReadInt(NotReportingDaysValue, DefaultNotReportingDays, MinNotReportingDays, MaxNotReportingDays); }
            set { WriteInt(NotReportingDaysValue, Clamp(value, MinNotReportingDays, MaxNotReportingDays)); }
        }

        /// <summary>Health Dashboard: a crash dump written in this many days counts as a recent blue screen.</summary>
        public static int BlueScreenDays
        {
            get { return ReadInt(BlueScreenDaysValue, DefaultBlueScreenDays, MinBlueScreenDays, MaxBlueScreenDays); }
            set { WriteInt(BlueScreenDaysValue, Clamp(value, MinBlueScreenDays, MaxBlueScreenDays)); }
        }

        /// <summary>Health Dashboard: a client whose last software update scan finished this many days ago or more counts as not scanning.</summary>
        public static int NotScanningDays
        {
            get { return ReadInt(NotScanningDaysValue, DefaultNotScanningDays, MinNotScanningDays, MaxNotScanningDays); }
            set { WriteInt(NotScanningDaysValue, Clamp(value, MinNotScanningDays, MaxNotScanningDays)); }
        }

        /// <summary>
        /// AD Cleanup Report: a computer AD hasn't seen in this many days or more (by its last logon or password
        /// change) counts as unused there, and a client the site hasn't heard from in as long counts as silent.
        /// </summary>
        public static int ADNotSeenDays
        {
            get { return ReadInt(ADNotSeenDaysValue, DefaultADNotSeenDays, MinADNotSeenDays, MaxADNotSeenDays); }
            set { WriteInt(ADNotSeenDaysValue, Clamp(value, MinADNotSeenDays, MaxADNotSeenDays)); }
        }

        /// <summary>
        /// Health Dashboard: a workstation on a Windows build older than this counts as behind. Written the way
        /// winver shows it: a build ("26100") or a build and update revision ("26100.4652"). One that doesn't read
        /// as a build gives the default.
        /// </summary>
        public static string WindowsBuild
        {
            get
            {
                string text = ReadValue(WindowsBuildValue) as string;
                return TryParseWindowsBuild(text, out _) ? text.Trim() : DefaultWindowsBuild;
            }
            set
            {
                if (!TryParseWindowsBuild(value, out _)) throw new ArgumentException("Not a Windows build: " + value, nameof(value));
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Layer8Log.RegistryKeyPath))
                {
                    key.SetValue(WindowsBuildValue, value.Trim(), RegistryValueKind.String);
                }
            }
        }

        /// <summary>
        /// Reads "26100" or "26100.4652" as a build and update revision (26100.0 for a build alone), so builds
        /// compare in order. False for anything else, or a build older than Windows 10's first.
        /// </summary>
        public static bool TryParseWindowsBuild(string text, out Version build)
        {
            build = null;
            Match match = Regex.Match(text ?? string.Empty, @"^\s*(\d{5})(?:\.(\d{1,6}))?\s*$");
            if (!match.Success) return false;
            int number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (number < MinWindowsBuild) return false;
            int revision = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            build = new Version(number, revision);
            return true;
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

        /// <summary>A value in the settings key, or null if it's missing or the key can't be read.</summary>
        private static object ReadValue(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Layer8Log.RegistryKeyPath))
                {
                    return key?.GetValue(name);
                }
            }
            catch (Exception)
            {
                // No access to the key: use the default.
                return null;
            }
        }

        private static int ReadInt(string name, int fallback, int min, int max)
        {
            object value = ReadValue(name);

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
