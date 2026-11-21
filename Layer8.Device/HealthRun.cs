using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Layer8.Device
{
    /// <summary>
    /// One full Health Dashboard run on a collection, kept so the next run can say what changed: each device's issues
    /// by ResourceID, and each tile's count. Kept per Windows user in %LOCALAPPDATA%\Layer8\Dashboard, one small text
    /// file per collection, next to Layer8's log; nothing on the site changes. Every site has its own SMS00001, so the
    /// file names the site server, and a run against another site's collection of the same ID replaces it rather than
    /// being compared with it.
    /// </summary>
    internal sealed class HealthRun
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(HealthRun));
        private const string FirstLine = "Layer8 Health Dashboard run, version 1";

        /// <summary>One device as a run found it: its name then, and its issues as the tiles name them.</summary>
        internal sealed class DeviceIssues
        {
            public DeviceIssues(string name, IEnumerable<string> issues)
            {
                Name = name ?? string.Empty;
                Issues = issues.ToList();
            }

            public string Name { get; }
            public List<string> Issues { get; }
        }

        public HealthRun(string server, DateTime finished)
        {
            Server = server ?? string.Empty;
            Finished = finished;
        }

        /// <summary>The site server the console was connected to.</summary>
        public string Server { get; }

        public DateTime Finished { get; }

        /// <summary>Each device the run checked, or found offline or couldn't check, by ResourceID.</summary>
        public Dictionary<int, DeviceIssues> Devices { get; } = new Dictionary<int, DeviceIssues>();

        /// <summary>Each tile's count, by its caption.</summary>
        public Dictionary<string, int> Tiles { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where a collection's last run is kept.</summary>
        public static string PathFor(string collectionId)
        {
            string name = collectionId ?? string.Empty;
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '-');
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Layer8", "Dashboard", name + ".txt");
        }

        /// <summary>
        /// The run kept at this path, or null when there's none, it's from another site server, or it can't be read
        /// (a file that doesn't read as a run is treated as none, and the next run replaces it).
        /// </summary>
        public static HealthRun Load(string path, string server)
        {
            if (!File.Exists(path)) return null;
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != FirstLine) throw new InvalidDataException("it isn't a Layer8 dashboard run");
                string keptServer = null;
                DateTime? finished = null;
                List<string[]> rest = new List<string[]>();
                foreach (string line in lines.Skip(1))
                {
                    string[] fields = line.Split('\t');
                    if (fields[0] == "server" && fields.Length >= 2) keptServer = fields[1];
                    else if (fields[0] == "finished" && fields.Length >= 2 &&
                             DateTime.TryParseExact(fields[1], "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime when)) finished = when;
                    else rest.Add(fields);
                }
                if (!finished.HasValue) throw new InvalidDataException("it has no finish time");
                if (!string.Equals(keptServer ?? string.Empty, server ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    log.InfoFormat("The dashboard run kept in {0} is from {1}, not {2}: not compared with", path, keptServer, server);
                    return null;
                }

                HealthRun run = new HealthRun(keptServer, finished.Value);
                foreach (string[] fields in rest)
                {
                    if (fields[0] == "tile" && fields.Length >= 3 && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                    {
                        run.Tiles[fields[1]] = count;
                    }
                    else if (fields[0] == "device" && fields.Length >= 4 && int.TryParse(fields[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id))
                    {
                        run.Devices[id] = new DeviceIssues(fields[2], fields[3].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
                    }
                }
                return run;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                log.Warn(string.Format("Couldn't read the dashboard run kept in {0}; the next run replaces it", path), ex);
                return null;
            }
        }

        /// <summary>
        /// Writes the run, replacing the one kept before. Written to a temporary file first, so a console closed
        /// halfway leaves the earlier run whole. Throws IOException or UnauthorizedAccessException when it can't.
        /// </summary>
        public void Save(string path)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(FirstLine);
            text.Append("server\t").AppendLine(Server);
            text.Append("finished\t").AppendLine(Finished.ToString("s", CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, int> tile in Tiles)
            {
                text.Append("tile\t").Append(tile.Key).Append('\t').AppendLine(tile.Value.ToString(CultureInfo.InvariantCulture));
            }
            foreach (KeyValuePair<int, DeviceIssues> device in Devices.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.Append("device\t").Append(device.Key.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(Clean(device.Value.Name)).Append('\t').AppendLine(string.Join("|", device.Value.Issues.Select(Clean)));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        // Tabs, line breaks and | separate the fields; none belong in a device's name or an issue's.
        private static string Clean(string value) => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
    }
}
