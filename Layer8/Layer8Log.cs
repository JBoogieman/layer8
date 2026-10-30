using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Layer8
{
    /// <summary>How much detail goes into Layer8's log. Each level includes the ones after it.</summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>
    /// Layer8's log: one CMTrace-format file per user, %LOCALAPPDATA%\Layer8\Logs\Layer8.log.
    ///
    /// It's written directly rather than through a logging library, so Layer8 never ships a DLL that
    /// another console extension also drops into the console's bin folder (Quick Tools ships an older
    /// log4net.dll, and only one version can live there).
    ///
    /// The level is read from HKCU\SOFTWARE\Layer8, string value LogLevel: Debug, Info, Warn or Error
    /// (Info when it's missing or not one of those). It's re-read every few seconds, so turning on Debug
    /// takes effect without restarting the console.
    ///
    /// When the file passes 2 MB it's renamed to Layer8.lo_ (replacing the previous one) and a new file
    /// is started, the same way ConfigMgr rolls its own logs.
    ///
    /// Several consoles run by the same user share the file; a named mutex keeps their writes from
    /// interleaving. Nothing here ever throws: a logging problem must never become the console's problem.
    /// </summary>
    public static class Layer8Log
    {
        public const string RegistryKeyPath = @"SOFTWARE\Layer8";
        public const string RegistryValueName = "LogLevel";
        private const long MaxFileBytes = 2L * 1024 * 1024;
        private static readonly TimeSpan LevelRefreshInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(2);
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        private static readonly object WriteSync = new object();
        private static readonly object LevelSync = new object();
        private static Mutex fileMutex;
        private static volatile int level = (int)LogLevel.Info;
        private static long levelReadTicks;   // UTC ticks of the last registry read
        private static int started;

        /// <summary>Per-user log folder, so console users who aren't local admins can still write it.</summary>
        public static string LogFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Layer8", "Logs");

        public static string LogFile => Path.Combine(LogFolder, "Layer8.log");

        /// <summary>The current level, refreshed from the registry at most every few seconds.</summary>
        public static LogLevel Level
        {
            get
            {
                RefreshLevel();
                return (LogLevel)level;
            }
        }

        public static Logger GetLogger(Type type)
        {
            EnsureStarted();
            return new Logger(type?.FullName ?? "Layer8");
        }

        internal static bool IsEnabled(LogLevel entryLevel) => entryLevel >= Level;

        internal static void Write(LogLevel entryLevel, string component, string message, Exception exception, bool always = false)
        {
            try
            {
                if (!always && !IsEnabled(entryLevel)) return;

                string text = message ?? string.Empty;
                if (exception != null) text += Environment.NewLine + exception;

                Trace.WriteLine("Layer8 " + entryLevel.ToString().ToUpperInvariant() + " " + component + " - " + text);
                Append(FormatEntry(entryLevel, component, text, DateTime.Now));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Layer8: could not write to " + LogFile + ": " + ex.Message);
            }
        }

        /// <summary>One CMTrace line. CMTrace shows type 3 in red, 2 in yellow; Debug and Info are both 1.</summary>
        internal static string FormatEntry(LogLevel entryLevel, string component, string text, DateTime localTime)
        {
            int type = entryLevel >= LogLevel.Error ? 3 : entryLevel == LogLevel.Warn ? 2 : 1;

            // CMTrace wants the bias the way Windows stores it: minutes to add to local time to get UTC
            // (+300 for US Central standard time).
            double bias = -TimeZoneInfo.Local.GetUtcOffset(localTime).TotalMinutes;

            // A message containing the closing marker would cut the entry short in CMTrace.
            string safeText = text.Replace("]LOG]!>", "] LOG]!>");

            return string.Format(
                CultureInfo.InvariantCulture,
                "<![LOG[{0}]LOG]!><time=\"{1:HH:mm:ss.fff}{2}\" date=\"{1:MM-dd-yyyy}\" component=\"{3}\" context=\"\" type=\"{4}\" thread=\"{5}\" file=\"\">",
                safeText,
                localTime,
                bias.ToString("+0;-0", CultureInfo.InvariantCulture),
                component,
                type,
                Thread.CurrentThread.ManagedThreadId);
        }

        private static void EnsureStarted()
        {
            if (Interlocked.Exchange(ref started, 1) == 1) return;

            // Always written, whatever the level, so every log shows which Layer8 and console were running.
            Write(LogLevel.Info, typeof(Layer8Log).FullName, string.Format(
                CultureInfo.InvariantCulture,
                "Layer8 {0} loaded in console {1} (.NET {2}, user {3}\\{4}); log level {5} (HKCU\\{6}, value {7})",
                typeof(Layer8Log).Assembly.GetName().Version,
                ConsoleVersion(),
                Environment.Version,
                Environment.UserDomainName,
                Environment.UserName,
                Level,
                RegistryKeyPath,
                RegistryValueName), null, always: true);
        }

        private static void RefreshLevel()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref levelReadTicks) < LevelRefreshInterval.Ticks) return;

            lock (LevelSync)
            {
                if (now - Interlocked.Read(ref levelReadTicks) < LevelRefreshInterval.Ticks) return;

                LogLevel newLevel = LogLevel.Info;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
                    {
                        string value = key?.GetValue(RegistryValueName) as string;
                        if (!string.IsNullOrWhiteSpace(value) &&
                            Enum.TryParse(value.Trim(), true, out LogLevel parsed) &&
                            Enum.IsDefined(typeof(LogLevel), parsed))
                        {
                            newLevel = parsed;
                        }
                    }
                }
                catch (Exception)
                {
                    // No registry (or no access to it): stay at Info.
                }

                level = (int)newLevel;
                Interlocked.Exchange(ref levelReadTicks, now);
            }
        }

        private static void Append(string entry)
        {
            byte[] bytes = Utf8.GetBytes(entry + "\r\n");

            lock (WriteSync)
            {
                Mutex mutex = FileMutex();
                bool owned = false;
                try
                {
                    if (mutex != null)
                    {
                        try { owned = mutex.WaitOne(MutexWait); }
                        catch (AbandonedMutexException) { owned = true; }   // another console exited mid-write; the file is still fine
                    }

                    Directory.CreateDirectory(LogFolder);
                    RollIfFull();

                    using (var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length == 0)
                        {
                            // Byte-order mark on a new file, so CMTrace and Notepad read non-English names correctly.
                            stream.Write(Utf8Bom, 0, Utf8Bom.Length);
                        }
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                finally
                {
                    if (owned) mutex.ReleaseMutex();
                }
            }
        }

        private static void RollIfFull()
        {
            try
            {
                var file = new FileInfo(LogFile);
                if (!file.Exists || file.Length < MaxFileBytes) return;

                string previous = Path.ChangeExtension(LogFile, ".lo_");
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(LogFile, previous);
            }
            catch (Exception ex)
            {
                // Usually another program has the old file open. Keep appending; we'll try again next write.
                Trace.WriteLine("Layer8: could not roll " + LogFile + ": " + ex.Message);
            }
        }

        private static Mutex FileMutex()
        {
            if (fileMutex != null) return fileMutex;
            try
            {
                // Local\ = this user's logon session, which is every console sharing this log file.
                fileMutex = new Mutex(false, @"Local\Layer8.Log");
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Layer8: log mutex unavailable, writing without it: " + ex.Message);
            }
            return fileMutex;
        }

        private static string ConsoleVersion()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileVersionInfo.FileVersion ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }

    /// <summary>
    /// A named logger, one per class (the name shows as the component in CMTrace).
    /// Get one with Layer8Log.GetLogger(typeof(YourClass)).
    /// </summary>
    public sealed class Logger
    {
        private readonly string component;

        internal Logger(string component)
        {
            this.component = component;
        }

        public bool IsDebugEnabled => Layer8Log.IsEnabled(LogLevel.Debug);

        public void Debug(string message) => Layer8Log.Write(LogLevel.Debug, component, message, null);
        public void Debug(string message, Exception exception) => Layer8Log.Write(LogLevel.Debug, component, message, exception);
        public void DebugFormat(string format, params object[] args) => WriteFormat(LogLevel.Debug, format, args);

        public void Info(string message) => Layer8Log.Write(LogLevel.Info, component, message, null);
        public void Info(string message, Exception exception) => Layer8Log.Write(LogLevel.Info, component, message, exception);
        public void InfoFormat(string format, params object[] args) => WriteFormat(LogLevel.Info, format, args);

        public void Warn(string message) => Layer8Log.Write(LogLevel.Warn, component, message, null);
        public void Warn(string message, Exception exception) => Layer8Log.Write(LogLevel.Warn, component, message, exception);
        public void WarnFormat(string format, params object[] args) => WriteFormat(LogLevel.Warn, format, args);

        public void Error(string message) => Layer8Log.Write(LogLevel.Error, component, message, null);
        public void Error(string message, Exception exception) => Layer8Log.Write(LogLevel.Error, component, message, exception);
        public void ErrorFormat(string format, params object[] args) => WriteFormat(LogLevel.Error, format, args);

        private void WriteFormat(LogLevel entryLevel, string format, object[] args)
        {
            if (!Layer8Log.IsEnabled(entryLevel)) return;   // don't pay for formatting what won't be written

            string message;
            try
            {
                message = string.Format(CultureInfo.InvariantCulture, format ?? string.Empty, args ?? new object[0]);
            }
            catch (FormatException)
            {
                message = format + " [could not format: " + string.Join(", ", args ?? new object[0]) + "]";
            }
            Layer8Log.Write(entryLevel, component, message, null);
        }
    }
}
