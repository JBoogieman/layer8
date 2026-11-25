using System;
using System.Linq;
using System.Xml.Linq;

namespace Layer8.DriverManager
{
    internal class DellDriverPackage : Package
    {
        private readonly XElement packageObject;
        private readonly XNamespace ns;

        public string Url { get; private set; }
        public string ReleaseNotesUrl { get; private set; }
        /// <summary>Download size in bytes.</summary>
        public double Size { get; private set; }
        /// <summary>
        /// The pack's SHA-256 in Dell's signed catalog. Empty when it has none, and then the pack isn't used: the
        /// catalog's MD5 isn't accepted (Authenticode.RequireHash).
        /// </summary>
        public string CatalogHash { get; private set; }

        public DellDriverPackage(XElement pack)
        {
            packageObject = pack;

            ns = packageObject.GetDefaultNamespace();

            Name = Model = packageObject.Element(ns + "SupportedSystems").Element(ns + "Brand").Element(ns + "Model").Attribute("name").Value;

            Size = double.Parse(packageObject.Attribute("size").Value);

            Version = packageObject.Attribute("dellVersion").Value;
            VersionFile = Version + ".version";

            Url = packageObject.Attribute("path").Value;

            XElement sha256 = packageObject.Element(ns + "Cryptography")?.Elements(ns + "Hash")
                .FirstOrDefault(hash => string.Equals((string)hash.Attribute("algorithm"), "SHA256", StringComparison.OrdinalIgnoreCase));
            CatalogHash = ((string)sha256 ?? string.Empty).Trim();

            ReleaseNotesUrl = packageObject.Element(ns + "ImportantInfo").Attribute("URL").Value;
        }
    }
}
