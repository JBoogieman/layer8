using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Layer8.DriverManager
{
    /// <summary>
    /// One Lenovo SCCM driver pack from Lenovo's catalog (catalogv2.xml): a model, the machine types it covers
    /// (the first four characters of a ThinkPad's or ThinkCentre's model number, such as 20XW), and the
    /// self-extracting pack for one Windows release.
    /// </summary>
    internal class LenovoDriverPackage : Package
    {
        public string Url { get; private set; }
        /// <summary>The machine types the pack covers, upper case.</summary>
        public HashSet<string> MachineTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The hash Lenovo's catalog gives for the download, or null.</summary>
        public string Crc { get; private set; }
        public string WindowsVersion { get; private set; }

        public LenovoDriverPackage(XElement model, XElement pack)
        {
            Model = Clean((string)model.Attribute("name"));
            Name = "Lenovo " + Model;
            foreach (XElement type in model.Element("Types")?.Elements("Type") ?? Enumerable.Empty<XElement>())
            {
                foreach (string machineType in type.Value.Split(','))
                {
                    if (!string.IsNullOrWhiteSpace(machineType)) MachineTypes.Add(machineType.Trim());
                }
            }

            Url = pack.Value.Trim();
            Crc = (string)pack.Attribute("crc");
            WindowsVersion = (string)pack.Attribute("version");
            // The release date when the catalog has it (every pack had one in Nov 2026), otherwise the release the file
            // name ends in (…_202312). It's also the driver package's Version once imported, so it stays short.
            string fileName = Path.GetFileNameWithoutExtension(new Uri(Url).LocalPath);
            string date = (string)pack.Attribute("date");
            Version = !string.IsNullOrWhiteSpace(date) ? date.Trim() : ReleaseIn(fileName);
            // One file per release of the pack, so a newer release shows as "New version". The importer reads the
            // version from this file's name up to the first dot, so the name has none.
            VersionFile = Version.Replace('.', '-') + ".version";
            LegacyVersionFile = fileName + ".version";
        }

        /// <summary>What Layer8 0.7.0 named the version file: the pack's file name, too long for a package's Version.</summary>
        public string LegacyVersionFile { get; private set; }

        private static string ReleaseIn(string fileName)
        {
            Match release = Regex.Match(fileName, @"_(\d{6})$");
            if (release.Success) return release.Groups[1].Value;
            string name = fileName.Replace('.', '-');
            return name.Length > 32 ? name.Substring(name.Length - 32) : name;
        }

        /// <summary>The model with its machine types, the way Lenovo's support site lists it.</summary>
        public string DisplayName => MachineTypes.Count == 0 ? Model : string.Format("{0} ({1})", Model, string.Join(", ", MachineTypes.OrderBy(t => t)));

        /// <summary>Model names become folder names: anything a folder can't be called turns into a dash.</summary>
        private static string Clean(string name)
        {
            name = (name ?? string.Empty).Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '-');
            return name;
        }
    }
}
