using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.IO;
using System.Collections;

namespace Layer8.DriverManager
{
    internal class DriverPackage : Package
    {
        #region Private
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ModifyRegistry registry = new ModifyRegistry();
        private readonly ConnectionManagerBase connectionManager;
        private IResultObject packageObject;
        private IResultObject categoryObject;
        #endregion

        #region State
        public string[] Infs { get; private set; }
        public Dictionary<string, Driver> Drivers { get; private set; } = new Dictionary<string, Driver>();
        public Dictionary<string, string> ImportError { get; private set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ImportWarning { get; private set; } = new Dictionary<string, string>();
        public bool HasDriverError { get { return ImportError.Count > 0 ? true : false; } }
        public bool HasDriverWarning { get { return ImportWarning.Count > 0 ? true : false; } }

        public IResultObject Category
        {
            get
            {
                if (categoryObject == null)
                    CreateDriverCategory();
                return categoryObject;
            }
        }

        public IResultObject Package
        {
            get
            {
                if (packageObject == null)
                    CreateDriverPackge();
                return packageObject;
            }
        }
        #endregion

        public DriverPackage(ConnectionManagerBase connection, string name, string source, string target)
        {
            connectionManager = connection;
            Name = name;
            Source = source;
            Target = target;

            GetVersionFromFile();

            Hash = Utility.CreateHashForFolder(Source);
            Import = !File.Exists(Path.Combine(Source, Hash + ".hash")) || CheckVersion();

            string[] infFiles = Directory.GetFiles(Source, "*.inf", SearchOption.AllDirectories);
            Infs = infFiles.Where(x => Path.GetFileName(x) != "autorun.inf").ToArray();
        }

        public bool Create()
        {
            return !CreateDriverCategory() || !CreateDriverPackge() ? false : true;
        }

        private bool CreateDriverPackge()
        {
            string query = string.Format("SELECT * FROM SMS_DriverPackage WHERE NAME = '{0}'", Name);
            packageObject = Utility.GetFirstWMIInstance(connectionManager, query);

            if (packageObject == null)
            {
                // ConfigMgr won't create a driver package in a folder that doesn't exist yet, and calls that a
                // permission problem; so the (empty) folder is made first.
                string folderProblem = null;
                try { Directory.CreateDirectory(Target); }
                catch (IOException ex) { folderProblem = ex.Message; }
                catch (UnauthorizedAccessException ex) { folderProblem = ex.Message; }
                if (folderProblem != null)
                {
                    log.Error(string.Format("Creating driver package folder {0} failed: {1}", Target, folderProblem));
                    Exception.Add(new SystemException(string.Format("Couldn't create the driver package folder {0}: {1}", Target, folderProblem)));
                    return false;
                }

                IResultObject instance = connectionManager.CreateInstance("SMS_DriverPackage");
                instance["Name"].StringValue = Name;
                instance["Description"].StringValue = "";
                instance["PkgSourceFlag"].IntegerValue = 2;
                instance["PkgSourcePath"].StringValue = Target;
                instance["PkgFlags"].IntegerValue |= 16777216;
                // this is only supported on 1810 and above
                if (instance.PropertyList.ContainsKey("DriverManufacturer"))
                    instance["DriverManufacturer"].StringValue = Vendor;
                instance["Version"].StringValue = FileVersion;
                try
                {
                    instance.Put();
                    instance.Get();
                }
                catch (SmsQueryException ex)
                {
                    // The SMS provider says what went wrong; that beats a general message.
                    string description = (ex.InnerException as ManagementException)?.ErrorInformation?["Description"]?.ToString();
                    string str;
                    switch (ex.ExtendStatusErrorCode)
                    {
                        case 4:
                            str = string.Format("The driver package folder ({0}) isn't empty.", Target);
                            break;
                        default:
                            str = string.IsNullOrEmpty(description)
                                ? "An error occurred while importing the selected driver package."
                                : description.TrimEnd('.') + ".";
                            break;
                    }
                    if (ex.ExtendStatusErrorCode == 1078462229 || (description ?? string.Empty).IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        str = string.Format(
                            "ConfigMgr can't write to the driver package folder ({0}). It works there as the SMS provider's computer " +
                            "account (usually the site server's), not as you, so that account needs Full Control on it, on both the " +
                            "share and the folder (Modify isn't enough).", Target);
                        if (DriverPackageGeneralPage.IsAdminShare(Target))
                            str += " An admin share such as C$ only works if that account is an administrator on the computer.";
                    }
                    log.Error(string.Format("Creating driver package {0} in {1} failed: {2}", Name, Target, description ?? ex.Message), ex);
                    Exception.Add(new SystemException(str));

                    return false;
                }

                string folder = registry.ReadString("DriverPackageConsoleFolder");
                if (folder == "%MANUFACTURER%")
                    folder = Vendor;

                Utility.AddObjectToFolder(connectionManager, folder, instance["PackageID"].StringValue, 23);

                packageObject = instance;
            }

            return true;
        }

        public bool UpdatePackageVersion()
        {
            string query = string.Format("SELECT * FROM SMS_DriverPackage WHERE NAME = '{0}'", Name);
            packageObject = Utility.GetFirstWMIInstance(connectionManager, query);

            if (packageObject != null)
            {
                // this is only supported on 1810 and above
                if (packageObject.PropertyList.ContainsKey("DriverManufacturer"))
                    packageObject["DriverManufacturer"].StringValue = Vendor;
                packageObject["Version"].StringValue = FileVersion;

                try
                {
                    packageObject.Put();
                    packageObject.Get();

                    return true;
                }
                catch (SmsQueryException ex)
                {
                    Exception.Add(ex);

                    return false;
                }
            }

            return false;
        }

        private bool CheckVersion()
        {
            return GetPackageVersion() && Version != FileVersion ? true : false;
        }

        private bool GetPackageVersion()
        {
            string query = string.Format("SELECT * FROM SMS_Package WHERE NAME = '{0}'", Name);
            IResultObject packageObject = Utility.GetFirstWMIInstance(connectionManager, query);

            if (packageObject != null)
            {
                Version = packageObject["Version"].StringValue;
            }

            return string.IsNullOrEmpty(Version) ? false : true;
        }

        #region Drivers
        public IEnumerable<IResultObject> GetDriversInPackge()
        {
            string query = string.Format("SELECT * FROM SMS_Driver WHERE CI_ID IN (SELECT DC.CI_ID FROM SMS_DriverContainer AS DC WHERE DC.PackageID='{0}') ORDER BY LocalizedDisplayName", Package["PackageID"].StringValue);
            using (IResultObject resultObjects = connectionManager.QueryProcessor.ExecuteQuery(query))
            {
                foreach (IResultObject resultObject in resultObjects)
                {
                    yield return resultObject;
                }
            }
        }

        public bool DriverContentExists(Driver driver)
        {
            return DriverContentExists(driver.Object);
        }

        public bool DriverContentExists(IResultObject driverObject)
        {
            string query = string.Format("SELECT * FROM SMS_CIToContent WHERE CI_ID='{0}'", driverObject["CI_ID"].IntegerValue);
            using (IResultObject contentObject = Utility.GetFirstWMIInstance(connectionManager, query))
            {
                if (contentObject != null)
                {
                    contentObject.Get();
                    if (Directory.Exists(Path.Combine(Target, contentObject["ContentUniqueID"].StringValue)) && Directory.Exists(driverObject["ContentSourcePath"].StringValue))
                        return true;
                }
            }

            return false;
        }

        public bool AddDriverToDriverPack(Driver driver)
        {
            log.Debug("AddDriverToDriverPack: " + driver.Model);

            List<int> contentIDs = new List<int>();

            string query = string.Format("SELECT * FROM SMS_CIToContent WHERE CI_ID='{0}'", driver.Object["CI_ID"].IntegerValue);

            foreach (IResultObject resultObject in connectionManager.QueryProcessor.ExecuteQuery(query))
                contentIDs.Add(resultObject["ContentID"].IntegerValue);

            List<string> packageSources = new List<string>
            {
                driver.Object["ContentSourcePath"].StringValue
            };

            Dictionary<string, object> methodParameters = new Dictionary<string, object>
            {
                { "bRefreshDPs", false },
                { "ContentIDs", contentIDs.ToArray() },
                { "ContentSourcePath", packageSources.ToArray() }
            };

            try
            {
                Package.ExecuteMethod("AddDriverContent", methodParameters);
            }
            catch (SmsQueryException ex)
            {
                string str = string.Format("{0} ({1})", driver.Object["LocalizedDisplayName"].StringValue, driver.Object["DriverINFFile"].StringValue);
                string reason = Utility.ProviderError(ex);
                ImportError[str] = "Could not be added to package: " + reason;
                log.Error("AddDriverToDriverPack: " + reason);

                return false;
            }

            return true;
        }

        public bool AddDriversToDriverPack()
        {
            List<int> contentIDs = new List<int>();
            List<string> packageSources = new List<string>();

            foreach (KeyValuePair<string, Driver> driver in Drivers)
            {
                if (driver.Value.Import && driver.Value.Object != null)
                {
                    string query = string.Format("SELECT ContentID FROM SMS_CIToContent WHERE CI_ID='{0}'", driver.Value.Object["CI_ID"].IntegerValue);

                    foreach (IResultObject resultObject in connectionManager.QueryProcessor.ExecuteQuery(query))
                        contentIDs.Add(resultObject["ContentID"].IntegerValue);

                    packageSources.Add(driver.Value.Object["ContentSourcePath"].StringValue);
                }
            }

            Dictionary<string, object> methodParameters = new Dictionary<string, object>
            {
                { "bRefreshDPs", true },
                { "ContentIDs", contentIDs.ToArray() },
                { "ContentSourcePath", packageSources.ToArray() }
            };

            try
            {
                Package.ExecuteMethod("AddDriverContent", methodParameters);
            }
            catch (SmsException ex)
            {
                if (ex is SmsQueryException)
                {
                    Exception.Add(new SystemException(Utility.ProviderError(ex)));
                }
                else
                {
                    Exception.Add(ex);
                }
                    
                return false;
            }

            return true;
        }

        public bool RemoveDriverFromDriverPack(IResultObject driverObject)
        {
            log.Debug("RemoveDriverFromDriverPack: " + driverObject["LocalizedDisplayName"].StringValue);

            List<int> contentIDs = new List<int>();

            string query = string.Format("SELECT * FROM SMS_CIToContent WHERE CI_ID='{0}'", driverObject["CI_ID"].IntegerValue);

            foreach (IResultObject resultObject in connectionManager.QueryProcessor.ExecuteQuery(query))
                contentIDs.Add(resultObject["ContentID"].IntegerValue);

            Dictionary<string, object> methodParameters = new Dictionary<string, object>
            {
                { "bRefreshDPs", false },
                { "ContentIDs", contentIDs.ToArray() }
            };

            try
            {
                Package.ExecuteMethod("RemoveDriverContent", methodParameters);
            }
            catch (SmsQueryException ex)
            {
                string str = string.Format("{0} ({1})", driverObject["LocalizedDisplayName"].StringValue, driverObject["DriverINFFile"].StringValue);
                string reason = Utility.ProviderError(ex);
                ImportWarning[str] = reason;
                log.Warn("RemoveDriverFromDriverPack: " + reason);

                return false;
            }

            return true;
        }
        #endregion

        #region Driver Category
        private IResultObject GetDriverCategory()
        {
            string query = string.Format("SELECT * FROM SMS_CategoryInstance WHERE CategoryTypeName='DriverCategories' AND LocalizedCategoryInstanceName='{0}'", Name);
            IResultObject categoryObject = Utility.GetFirstWMIInstance(connectionManager, query);

            if (categoryObject != null)
            {
                categoryObject.Get();
                if (categoryObject["LocalizedCategoryInstanceName"].StringValue == Name)
                    return categoryObject;
            }

            return null;
        }

        private bool CreateDriverCategory()
        {
            categoryObject = GetDriverCategory();

            if (categoryObject == null)
            {
                IResultObject instance = connectionManager.CreateInstance("SMS_CategoryInstance");
                instance["CategoryInstance_UniqueID"].StringValue = string.Format("DriverCategories: {0}", Guid.NewGuid().ToString());
                instance["CategoryTypeName"].StringValue = "DriverCategories";
                List<IResultObject> list = new List<IResultObject>();
                IResultObject embeddedObjectInstance = connectionManager.CreateEmbeddedObjectInstance("SMS_Category_LocalizedProperties");
                embeddedObjectInstance["CategoryInstanceName"].StringValue = Name;
                embeddedObjectInstance["LocaleID"].IntegerValue = 0;
                list.Add(embeddedObjectInstance);
                instance.SetArrayItems("LocalizedInformation", list);
                try
                {
                    instance.Put();
                    instance.Get();
                }
                catch (SmsQueryException ex)
                {
                    Exception.Add(new SystemException(Utility.ProviderError(ex)));
                    return false;
                }

                categoryObject = instance;
            }
            
            return true;
        }

        public bool AddDriverToCategory(Driver driver)
        {
            log.Debug("AddDriverToCategory: " + driver.Model);
            // get category unique id
            string categoryUniqueID = Category["CategoryInstance_UniqueID"].StringValue;
            ArrayList categories = new ArrayList();
            // check if driver has any category
            if (driver.Object["CategoryInstance_UniqueIDs"].StringArrayValue != null)
            {
                // check if category is in driver categories list
                categories.AddRange(driver.Object["CategoryInstance_UniqueIDs"].StringArrayValue);
                foreach (string str in categories)
                {
                    // if category exist return
                    if (str == categoryUniqueID)
                    {
                        return true;
                    }
                }
            }
            // add category to driver
            categories.Add(categoryUniqueID);
            driver.Object["CategoryInstance_UniqueIDs"].StringArrayValue = (string[])categories.ToArray(typeof(string));
            try
            {
                Utility.PutOnce(connectionManager, driver.Object);
                driver.Object.Get();
            }
            catch (Exception ex) when (ex is SmsQueryException || ex is ManagementException)
            {
                string str = string.Format("{0} ({1})", driver.Object["LocalizedDisplayName"].StringValue, driver.Object["DriverINFFile"].StringValue);
                string reason = Utility.ProviderError(ex);
                ImportError[str] = "Cannot be added to category: " + reason;
                log.Error("AddDriverToCategory: " + reason);

                return false;
            }

            return true;
        }

        public bool RemoveDriverFromCategory(IResultObject driverObject)
        {
            log.Debug("RemoveDriverFromCategory: " + driverObject["LocalizedDisplayName"].StringValue);
            // get category unique id
            string categoryUniqueID = Category["CategoryInstance_UniqueID"].StringValue;
            ArrayList categories = new ArrayList();
            ArrayList removeCategory = new ArrayList();
            // check if driver has any category
            if (driverObject["CategoryInstance_UniqueIDs"].StringArrayValue != null)
            {
                // check if category is in driver categories list
                categories.AddRange(driverObject["CategoryInstance_UniqueIDs"].StringArrayValue);
                foreach (string str in categories)
                {
                    // if category exist add it to list for removal
                    if (str != categoryUniqueID)
                    {
                        removeCategory.Add(str);
                    }
                }
            }
            // remove category from driver
            driverObject["CategoryInstance_UniqueIDs"].StringArrayValue = (string[])removeCategory.ToArray(typeof(string));
            try
            {
                Utility.PutOnce(connectionManager, driverObject);
                driverObject.Get();
            }
            catch (Exception ex) when (ex is SmsQueryException || ex is ManagementException)
            {
                string str = string.Format("{0} ({1})", driverObject["LocalizedDisplayName"].StringValue, driverObject["DriverINFFile"].StringValue);
                string reason = Utility.ProviderError(ex);
                ImportError[str] = "Cannot be removed from category: " + reason;
                log.Error("RemoveDriverFromCategory: " + reason);

                return false;
            }

            return true;
        }
        #endregion

    }
}
