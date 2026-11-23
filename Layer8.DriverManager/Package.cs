using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace Layer8.DriverManager
{
    internal class Package
    {
        #region State
        public string Name { get; protected set; }
        public string Model { get; protected set; }
        public string Vendor { get; set; }
        public string Version { get; protected set; }
        public string VersionFile { get; protected set; }
        public string FileVersion { get; protected set; }
        public string Source { get; protected set; }
        public string Target { get; protected set; }
        public string FolderName { get; protected set; }
        public string Hash { get; protected set; }
        public bool Import { get; protected set; }
        public List<Exception> Exception { get; protected set; } = new List<Exception>();
        public bool HasException { get { return Exception.Count > 0 ? true : false; } }
        #endregion

        public string GetVersionFromFile()
        {
            try
            {
                string[] fileList = Directory.GetFiles(Source, "*.version");
                if (fileList.Length >= 1)
                {
                    string str = Path.GetFileName(fileList.FirstOrDefault());
                    FileVersion = str.Split('.')[0];
                }
            }
            catch
            {
                Exception.Add(new SystemException("Cannot get version from file."));
            }

            return FileVersion;
        }

        public void CreateHashFile()
        {
            try
            {
                string[] fileList = Directory.GetFiles(Source, "*.hash");
                foreach (string file in fileList)
                {
                    File.Delete(file);
                }
                File.Create(Path.Combine(Source, Hash + ".hash"));
            }
            catch
            {
                Exception.Add(new SystemException("Cannot create Hash file."));
            }
        }

        /// <summary>Whether one folder is the other or inside it, either way round.</summary>
        internal static bool FoldersOverlap(string first, string second) =>
            Utility.IsInside(first, second, true) || Utility.IsInside(second, first, true);

        /// <summary>
        /// Why target can't be emptied and filled again from source, or null when it can. The import deletes target
        /// first, so it has to be a folder inside root (the package folder setting) and apart from source: with the
        /// legacy package folder set to the driver source folder, target was source, and the source was deleted.
        /// </summary>
        internal static string TargetProblem(string root, string source, string target)
        {
            if (!Utility.IsInside(root, target))
                return string.Format("{0} isn't inside the package folder {1}, so Layer8 won't empty it", target, root);
            if (FoldersOverlap(source, target))
                return string.Format("{0} and the driver source {1} overlap, so emptying it would delete the source. Give the package folder a folder of its own in Layer8 Settings", target, source);
            return null;
        }

        public string GenerateModelFolderName(string os, string structure)
        {
            return FolderName = (string.IsNullOrEmpty(structure) ? false : Convert.ToBoolean(structure))
                ? string.Format(@"{0}\{1}\{2}", Vendor, Model, os)
                : string.Format(@"{0}-{1}-{2}", Vendor, Model, os);
        }
    }
}
