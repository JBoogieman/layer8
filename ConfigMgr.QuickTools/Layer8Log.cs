using System;
using System.Diagnostics;
using System.IO;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using log4net.Repository;

namespace ConfigMgr.QuickTools
{
    /// <summary>
    /// The one place Layer8 sets up logging.
    /// It loads log4net.config from the folder Layer8's own DLL lives in (not whatever folder the
    /// console process started from), falls back to built-in settings if that file is missing or
    /// broken, and writes a startup line so every log shows which Layer8 and console were running.
    /// </summary>
    public static class Layer8Log
    {
        private static readonly object Sync = new object();
        private static volatile bool configured;

        /// <summary>Per-user log folder, so console users who aren't local admins can still write it.</summary>
        public static string LogFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Layer8", "Logs");

        public static ILog GetLogger(Type type)
        {
            EnsureConfigured();
            return LogManager.GetLogger(typeof(Layer8Log).Assembly, type);
        }

        private static void EnsureConfigured()
        {
            if (configured) return;
            lock (Sync)
            {
                if (configured) return;
                configured = true;

                ILoggerRepository repository = LogManager.GetRepository(typeof(Layer8Log).Assembly);
                string configFile = Path.Combine(Path.GetDirectoryName(typeof(Layer8Log).Assembly.Location) ?? string.Empty, "log4net.config");
                bool fromFile = false;
                try
                {
                    if (File.Exists(configFile))
                    {
                        XmlConfigurator.ConfigureAndWatch(repository, new FileInfo(configFile));
                        fromFile = repository.Configured;
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("Layer8: could not load " + configFile + ": " + ex.Message);
                }

                if (!fromFile)
                {
                    ConfigureFallback(repository);
                }

                LogManager.GetLogger(typeof(Layer8Log).Assembly, typeof(Layer8Log)).InfoFormat(
                    "Layer8 {0} loaded in console {1} (.NET {2}, user {3}\\{4}); logging settings from {5}",
                    typeof(Layer8Log).Assembly.GetName().Version,
                    ConsoleVersion(),
                    Environment.Version,
                    Environment.UserDomainName,
                    Environment.UserName,
                    fromFile ? configFile : "built-in defaults");
            }
        }

        private static void ConfigureFallback(ILoggerRepository repository)
        {
            var layout = new PatternLayout("%date [%thread] %-5level %logger - %message%newline%exception");
            layout.ActivateOptions();

            var appender = new RollingFileAppender
            {
                File = Path.Combine(LogFolder, "Layer8.log"),
                AppendToFile = true,
                RollingStyle = RollingFileAppender.RollingMode.Size,
                MaximumFileSize = "2MB",
                MaxSizeRollBackups = 3,
                StaticLogFileName = true,
                LockingModel = new FileAppender.MinimalLock(),
                Layout = layout
            };
            appender.ActivateOptions();
            BasicConfigurator.Configure(repository, appender);
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
}
