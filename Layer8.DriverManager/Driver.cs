using IniParser;
using IniParser.Model;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace Layer8.DriverManager
{
    internal class Driver
    {
        #region Private
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly IniData data;
        #endregion

        #region State
        public bool Import { get; set; }
        public string Version { get; private set; }
        public string Model { get; private set; }
        public IResultObject Object { get; private set; }
        public string InfLocation { get; private set; }
        public Exception Exception { get; private set; }
        public bool HasException { get { return Exception == null ? false : true; } }
        public Exception Warning { get; private set; }
        public bool HasWarning { get { return Warning == null ? false : true; } }
        #endregion

        private Driver()
        {
            Object = null;
            Import = true;
        }

        public Driver(string inf)
            : this()
        {
            InfLocation = inf;

            var parser = new FileIniDataParser();
            parser.Parser.Configuration.AllowDuplicateKeys = true;
            parser.Parser.Configuration.AllowDuplicateSections = true;
            parser.Parser.Configuration.SkipInvalidLines = true;
            parser.Parser.Configuration.CaseInsensitive = true;

            try
            {
                data = parser.ReadFile(inf);
                if (data.Sections.ContainsSection("Version") && data["Version"].ContainsKey("DriverVer"))
                {
                    Version = GetVersion();
                    Model = GetModel();
                }
                else
                {
                    Warning = new SystemException("Invalid inf file.");
                    log.Warn(string.Format("InvalidInf: {0}", InfLocation));

                    Import = false;
                }
            }
            catch (Exception ex)
            {
                Exception = ex;
                Import = false;
            }
        }

        internal void AddObject(IResultObject resultObject)
        {
            Object = resultObject;
        }

        internal bool CheckIfExists(ConnectionManagerBase connectionManager)
        {
            // would be good if we could check based on architecture here, but there is no column that supports that in SMS_Driver without digging into the xml
            // we could maybe hack in the description field and append architecture
            string query = string.Format("SELECT * FROM SMS_Driver WHERE LocalizedDisplayName='{0}' AND DriverVersion='{1}' AND DriverINFFile='{2}'", Model, Version, Path.GetFileName(InfLocation));
            IResultObject driverObject = Utility.GetFirstWMIInstance(connectionManager, query);

            if (driverObject != null)
            {
                if (!Directory.Exists(driverObject["ContentSourcePath"].StringValue))
                {
                    log.Debug("UpdateContentSourcePath: " + driverObject["LocalizedDisplayName"].StringValue);
                    try
                    {
                        // A query leaves out the driver's definition and names; load them so saving keeps them.
                        driverObject.Get();
                        driverObject["ContentSourcePath"].StringValue = Path.GetDirectoryName(InfLocation);

                        Utility.PutOnce(connectionManager, driverObject);
                        driverObject.Get();
                    }
                    catch (Exception ex) when (ex is SmsQueryException || ex is ManagementException)
                    {
                        Exception = new SystemException(DescribeSaveError(ex));
                        log.Error(string.Format("PutDriverObject: {0}, {1}, {2}", InfLocation, Utility.ProviderError(ex), Utility.ProviderSqlMessage(ex)), ex);

                        return false;
                    }
                }

                Object = driverObject;
                return true;
            }

            return false;
        }

        internal bool CreateObjectFromInfFile(ConnectionManagerBase connectionManager)
        {
            Dictionary<string, object> methodParameters = new Dictionary<string, object>
            {
                { "DriverPath", Path.GetDirectoryName(InfLocation) },
                { "INFFile", Path.GetFileName(InfLocation) }
            };

            IResultObject driverObject = null;

            try
            {
                log.Debug("CreateFromINF: " + InfLocation);
                using (IResultObject resultObject = connectionManager.ExecuteMethod("SMS_Driver", "CreateFromINF", methodParameters))
                {
                    log.Debug("CreateInstance: " + InfLocation);
                    driverObject = connectionManager.CreateInstance(resultObject["Driver"].ObjectValue);
                }
                FixPlatformNames(driverObject);
            }
            catch (SmsQueryException ex)
            {
                // error 183 = driver exist, check if source content is ok.
                if (ex.ExtendStatusErrorCode == 183)
                {
                    if (ex.InnerException is ManagementException managementException)
                    {
                        // ObjectInfo names the driver that's already there.
                        string uniqueId = null;
                        try { uniqueId = managementException.ErrorInformation?["ObjectInfo"]?.ToString(); }
                        catch (ManagementException) { }
                        if (string.IsNullOrEmpty(uniqueId))
                        {
                            Exception = new SystemException("ConfigMgr says this driver is already imported but didn't say which one it is.");
                            log.Error(string.Format("DriverExists: {0}, no ObjectInfo", InfLocation));

                            return false;
                        }

                        try
                        {
                            // update content source path if it dose not exist
                            string query = string.Format("SELECT * FROM SMS_Driver WHERE CI_UniqueID='{0}'", uniqueId.Replace("'", "''"));
                            driverObject = Utility.GetFirstWMIInstance(connectionManager, query);
                            // A query leaves out the driver's definition and names; load them so saving it below keeps them.
                            driverObject?.Get();

                            if (driverObject != null && !Directory.Exists(driverObject["ContentSourcePath"].StringValue))
                            {
                                log.Debug("UpdateContentSourcePath: " + driverObject["LocalizedDisplayName"].StringValue);
                                driverObject["ContentSourcePath"].StringValue = Path.GetDirectoryName(InfLocation);
                            }
                        }
                        catch (SmsQueryException ex1)
                        {
                            Exception = new SystemException(Utility.ProviderError(ex1));
                            log.Error(string.Format("ContentSourcePath: {0}, {1}, {2}", InfLocation, ex1.GetType().Name, Exception.Message));

                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (ex.ExtendStatusErrorCode == 13L)
                {
                    Exception = new SystemException("Invalid inf file.");
                    log.Error(string.Format("InvalidInf: {0}", InfLocation));

                    return false;
                }
                else
                {
                    Exception = new SystemException(Utility.ProviderError(ex));
                    log.Error(string.Format("CreateFromINF: {0}, {1}", InfLocation, Exception.Message));

                    return false;
                }
            }

            if (driverObject == null)
            {
                log.Debug("NoObject: " + InfLocation);

                return false;
            }

            driverObject["IsEnabled"].BooleanValue = true;
            try
            {
                Utility.PutOnce(connectionManager, driverObject);
                driverObject.Get();
            }
            catch (Exception ex) when (ex is SmsQueryException || ex is ManagementException)
            {
                Exception = new SystemException(DescribeSaveError(ex));
                log.Error(string.Format("PutDriverObject: {0}, {1}, {2}", InfLocation, Utility.ProviderError(ex), Utility.ProviderSqlMessage(ex)), ex);

                return false;
            }

            Object = driverObject;

            return true;
        }

        /// <summary>
        /// CreateFromINF writes ARM64 platforms as Platform="arm64", but the site database only accepts "ARM64" and refuses
        /// the whole driver ("The digest is not valid"). The console's own import gets this right, so match it.
        /// </summary>
        private static void FixPlatformNames(IResultObject driverObject)
        {
            string digest = driverObject?["SDMPackageXML"].StringValue;
            if (string.IsNullOrEmpty(digest))
                return;

            string fixedDigest = Regex.Replace(digest, "(<PlatformApplicabilityCondition\\b[^>]*\\bPlatform=\")arm64(\")", "$1ARM64$2");
            if (fixedDigest != digest)
            {
                log.Debug("FixPlatformNames: arm64 -> ARM64");
                driverObject["SDMPackageXML"].StringValue = fixedDigest;
            }
        }

        /// <summary>Why ConfigMgr wouldn't save a driver, with a pointer when the cause is a known one.</summary>
        private static string DescribeSaveError(Exception ex)
        {
            string reason = Utility.ProviderError(ex);
            if (reason.IndexOf("digest is not valid", StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrEmpty(Utility.ProviderSqlMessage(ex)))
            {
                reason = reason.TrimEnd('.') + ": ConfigMgr's database refused the driver. SMSProv.log on the site server " +
                    "has the database's reason. A known cause is a site database on SQL Server 2022 at compatibility level " +
                    "160, which ConfigMgr supports only from version 2603; Microsoft recommends level 150.";
            }

            return reason;
        }

        private string GetVersion()
        {
            string version = null;

            try
            {
                // set version to ini data version
                version = data["Version"]["DriverVer"];
                // check if version is a strings value
                Match match = Regex.Match(version, @"%(.+?)%");
                // if is strings value get value
                if (match.Success)
                {
                    string key = match.Groups[1].Value;
                    // get strings value
                    string strings = data["Strings"][key];
                    // remove dirty chars
                    version = Regex.Replace(version, @"%(.+?)%", Regex.Replace(strings, @"[^0-9a-zA-Z.]", ""));
                }
                // remove date from version 
                version = version.Split(',')[1].Trim();
                // version must always be w.x.y.z
                // fix version with 8.23.0203.2015 format as sccm thinks its 8.23.203.2015
                version = System.Version.Parse(version).ToString();
            }
            catch
            {
                Exception = new SystemException("Cannot get version information from inf.");
                log.Error(string.Format("GetVersion: {0}", Exception.Message));
            }

            return version;
        }

        private string GetModel()
        {
            string model = null;

            try
            {
                // set manufacturers to ini data manufacuter
                string[] manufacturers = data["Manufacturer"].FirstOrDefault().Value.Split(',');
                string manufacturer = null;
                // check if we have more then one supported os
                if (manufacturers.Length > 1)
                {
                    for (int i = 1; i < manufacturers.Length; i++)
                    {
                        // create the os manufacurer string
                        string test = manufacturers[0].Trim() + "." + manufacturers[i].Trim();
                        if (data.Sections.ContainsSection(test) && data[test].Count != 0)
                        {
                            // if we found driver name string break
                            manufacturer = test;
                            break;
                        }
                    }

                    if (manufacturer == null)
                    {
                        string test = manufacturers[0].Trim();
                        if (data.Sections.ContainsSection(test) && data[test].Count != 0)
                        {
                            manufacturer = test;
                        }
                    }
                }
                else
                {
                    // singel manufacurer string
                    manufacturer = manufacturers[0];
                }
                // get model from os section
                model = data[manufacturer].FirstOrDefault().KeyName;
                // check if model is strings value
                Match match = Regex.Match(model, @"%(.+?)%");
                if (match.Success)
                {
                    string key = match.Groups[1].Value.Trim();
                    // get strings value
                    model = data["Strings"][key].Replace('"', ' ').Trim();
                }
                else
                {
                    model = model.Replace('"', ' ').Trim();
                }
            }
            catch
            {
                Exception = new SystemException("Cannot get model information from inf.");
                log.Error(string.Format("GetModel: {0}", Exception.Message));
            }

            return model;
        }
    }
}
