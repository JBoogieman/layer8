using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// The Remote Tools menu: opens a Windows tool (Remote Desktop, Computer Management, ...) pointed at each
    /// selected device. The menu XML names the tool in DialogId.
    /// </summary>
    public static class RemoteTools
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(RemoteTools));

        // Opening a window per device gets out of hand quickly; ask first above this many.
        private const int ConfirmAbove = 5;

        // Device names go straight onto command lines, so only accept what a computer name can contain.
        private static readonly Regex ValidName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.\-_]{0,254}$");

        /// <summary>Console entry point: runs OpenCore behind Layer8's crash guard.</summary>
        public static void Open(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Remote tool", () => OpenCore(action, selectedResultObjects));
        }

        private static void OpenCore(ActionDescription action, IResultObject selectedResultObjects)
        {
            string tool = action.DialogId;
            List<string> names = new List<string>();
            List<string> skipped = new List<string>();
            foreach (IResultObject resultObject in selectedResultObjects)
            {
                string name = resultObject["Name"].StringValue;
                if (IsComputerName(name)) names.Add(name); else skipped.Add(name ?? "(no name)");
            }

            if (skipped.Count > 0)
            {
                log.WarnFormat("{0}: skipped names that aren't valid computer names: {1}", action.DisplayName, string.Join(", ", skipped));
            }
            if (names.Count == 0)
            {
                MessageBox.Show("None of the selected items has a name Layer8 can use as a computer name.", action.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (names.Count > ConfirmAbove &&
                MessageBox.Show(string.Format("Open {0} for all {1} selected devices? That's one window each.", action.DisplayName, names.Count),
                                action.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            if (tool == "ClientLogCMTrace")
            {
                OpenClientLogs(action, names);
                return;
            }

            foreach (string name in names)
            {
                ProcessStartInfo start = StartInfo(tool, name);
                string program = RequiredProgram(tool, start);
                if (!File.Exists(program))
                {
                    throw new FileNotFoundException(MissingToolMessage(tool, program), program);
                }
                log.InfoFormat("{0} on {1}: {2} {3}", action.DisplayName, name, start.FileName, start.Arguments);
                Process.Start(start)?.Dispose();
            }
        }

        // Client Log in CMTrace: the logs admins open most, offered in the picker. Any other name can be typed.
        private static readonly string[] CommonLogs =
        {
            "AppDiscovery", "AppEnforce", "AppIntentEval", "CAS", "CcmEval", "CcmExec", "ccmsetup", "CcmMessaging",
            "ClientIDManagerStartup", "ClientLocation", "ContentTransferManager", "DataTransferService", "InventoryAgent",
            "LocationServices", "PolicyAgent", "PolicyEvaluator", "ScanAgent", "StateMessage", "UpdatesDeployment",
            "UpdatesHandler", "UpdatesStore", "WUAHandler",
        };
        private static string lastLog = "AppEnforce";

        // A log name typed into the picker ends up in a path, so only plain file-name characters.
        private static readonly Regex ValidLogName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,99}$");

        private static void OpenClientLogs(ActionDescription action, List<string> names)
        {
            string logName = InputPrompt.AskFromList(action.DisplayName.TrimEnd('.'), "Log to open (pick one, or type the name of any log in C:\\Windows\\CCM\\Logs):", CommonLogs, lastLog);
            if (logName == null) return;
            if (logName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) logName = logName.Substring(0, logName.Length - 4);
            if (!ValidLogName.IsMatch(logName))
            {
                MessageBox.Show("\"" + logName + "\" isn't a log file name.", action.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            lastLog = logName;

            List<string> missing = new List<string>();
            foreach (string name in names)
            {
                string path = ClientLogPath(name, logName);
                if (!File.Exists(path))
                {
                    missing.Add(name);
                    continue;
                }
                string cmtrace = FindCMTrace(name);
                log.InfoFormat("Client Log in CMTrace on {0}: {1} {2}", name, cmtrace, path);
                Process.Start(Program(cmtrace, "\"" + path + "\""))?.Dispose();
            }
            if (missing.Count > 0)
            {
                MessageBox.Show(string.Format("{0}.log isn't there (or the C$ share can't be reached) on: {1}", logName, string.Join(", ", missing)),
                                action.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>A client log's path over the C$ share; ccmsetup keeps its own log folder.</summary>
        public static string ClientLogPath(string name, string log)
        {
            return string.Equals(log, "ccmsetup", StringComparison.OrdinalIgnoreCase)
                ? string.Format(@"\\{0}\C$\Windows\ccmsetup\Logs\ccmsetup.log", name)
                : string.Format(@"\\{0}\C$\Windows\CCM\Logs\{1}.log", name, log);
        }

        /// <summary>CMTrace from this computer's ConfigMgr client, or else from the device's.</summary>
        private static string FindCMTrace(string name)
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"CCM\CMTrace.exe");
            if (File.Exists(local)) return local;
            string remote = string.Format(@"\\{0}\admin$\CCM\CMTrace.exe", name);
            if (File.Exists(remote)) return remote;
            throw new FileNotFoundException("CMTrace.exe isn't on this computer (C:\\Windows\\CCM) or on " + name + ".", local);
        }

        /// <summary>
        /// True for names that can be a computer name. ConfigMgr also lists placeholder records such as
        /// "x64 Unknown Computer (x64 Unknown Computer)", which aren't machines and mustn't reach a command line.
        /// </summary>
        public static bool IsComputerName(string name)
        {
            return name != null && ValidName.IsMatch(name);
        }

        /// <summary>The program and arguments for one tool against one device. Public so it can be tested.</summary>
        public static ProcessStartInfo StartInfo(string tool, string name)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string system = NativeSystemFolder(windows);

            switch (tool)
            {
                case "Ping":
                    return Program(Path.Combine(system, "cmd.exe"), string.Format("/k title Ping {0} & ping.exe -t {0}", name));
                case "RemoteDesktop":
                    return Program(Path.Combine(system, "mstsc.exe"), "/v:" + name);
                case "RemoteAssistance":
                    // msra.exe asks for elevation, which a direct start can't give it ("requires elevation"). A hidden
                    // 64-bit cmd passes it to Windows' normal launcher, which shows the UAC prompt. Inside that cmd,
                    // System32 is the real 64-bit folder.
                    return Hidden(Path.Combine(system, "cmd.exe"),
                                  string.Format("/c start \"\" \"{0}\" /offerra {1}", Path.Combine(windows, @"System32\msra.exe"), name));
                case "PowerShell":
                    return Program(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
                                   string.Format("-NoExit -Command \"$Host.UI.RawUI.WindowTitle = 'PowerShell on {0}'; Enter-PSSession -ComputerName {0}\"", name));
                case "BrowseC":
                    return Program(Path.Combine(windows, "explorer.exe"), string.Format(@"\\{0}\C$", name));
                case "ClientLogs":
                    return Program(Path.Combine(windows, "explorer.exe"), string.Format(@"\\{0}\C$\Windows\CCM\Logs", name));
                case "BrowseCache":
                    return Program(Path.Combine(windows, "explorer.exe"), string.Format(@"\\{0}\C$\Windows\ccmcache", name));
                case "ComputerManagement":
                    return Program(Path.Combine(system, "mmc.exe"), "compmgmt.msc /computer=" + name);
                case "EventViewer":
                    // Not "mmc eventvwr.msc /computer=": Event Viewer ignores it and opens this computer's logs.
                    // eventvwr.exe takes the computer name as its argument.
                    return Program(Path.Combine(system, "eventvwr.exe"), name);
                case "Services":
                    return Program(Path.Combine(system, "mmc.exe"), "services.msc /computer=" + name);
                default:
                    throw new ArgumentException("Unknown remote tool '" + tool + "' in the menu XML.", nameof(tool));
            }
        }

        private static ProcessStartInfo Program(string fileName, string arguments)
        {
            return new ProcessStartInfo(fileName, arguments) { UseShellExecute = false };
        }

        private static ProcessStartInfo Hidden(string fileName, string arguments)
        {
            return new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true };
        }

        /// <summary>The program that must exist for the tool to work (for Remote Assistance, msra.exe rather than the cmd that starts it).</summary>
        private static string RequiredProgram(string tool, ProcessStartInfo start)
        {
            if (tool == "RemoteAssistance")
            {
                return Path.Combine(NativeSystemFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "msra.exe");
            }
            return start.FileName;
        }

        /// <summary>
        /// The real System32. The console is a 32-bit program, so on 64-bit Windows "System32" quietly means
        /// SysWOW64, which lacks some tools (msra.exe) and would start 32-bit versions of the rest.
        /// Sysnative is how a 32-bit program reaches the 64-bit folder.
        /// </summary>
        private static string NativeSystemFolder(string windows)
        {
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                string sysnative = Path.Combine(windows, "Sysnative");
                if (Directory.Exists(sysnative)) return sysnative;
            }
            return Environment.SystemDirectory;
        }

        private static string MissingToolMessage(string tool, string path)
        {
            if (tool == "RemoteAssistance")
            {
                return path + " isn't on this computer. On Windows Server, add the Remote Assistance feature (Install-WindowsFeature Remote-Assistance).";
            }
            return path + " isn't on this computer.";
        }
    }
}
