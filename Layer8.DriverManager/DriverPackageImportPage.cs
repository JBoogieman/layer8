using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Layer8.DriverManager
{
    public partial class DriverPackageImportPage : SmsPageControl
    {
        #region Private
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ModifyRegistry registry = new ModifyRegistry();
        // How many drivers already in a package are checked at once (each check is a few WMI calls to the SMS provider).
        private const int ParallelDriverChecks = 8;
        private int progressStepPercent;
        private int progresPercent;
        private int progressTotal = 0;
        private int progressCount = 0;
        private int progressStart;
        private int progressStepCount;
        private int totalDriverPackages;
        private bool refreshDP;
        private bool refreshPackage;
        // Drivers ConfigMgr refused in the current package (import, category or adding to the package). Kept apart
        // from INF files that can't be read, which a second run won't fix either.
        private int failedImports;
        #endregion

        public DriverPackageImportPage(SmsPageData pageData)
            : base(pageData)
        {
            Title = "Select Packages";
            Headline = "Select driver packages to get imported";

            InitializeComponent();
        }

        public override void InitializePageControl()
        {
            base.InitializePageControl();

            ControlsInspector.AddControl(dataGridViewDriverPackages, new ControlDataStateEvaluator(ValidateSelectedDriverPackages), "Select driver packages to import");
            dataGridViewDriverPackages.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

            Initialized = false;

            InitializeDataGridView();
        }

        protected override void ApplyChanges()
        {
            base.ApplyChanges();
        }

        public override void PostApply(BackgroundWorker worker, DoWorkEventArgs e)
        {
            try
            {
                Dictionary<string, string> error = new Dictionary<string, string>();
                List<string> successful = new List<string>();

                int num = 0;
                progressStepCount = 5;
 
                List<DriverPackage> driverPackages = (List<DriverPackage>)UserData["DriverPackageItems"];
                totalDriverPackages = driverPackages.Count;

                foreach (DriverPackage driverPackage in driverPackages)
                {
                    progressStart = num * 100 / totalDriverPackages;
                    worker.ReportProgress(progressStart, string.Format("Importing Driver Package: {0}", driverPackage.Name));

                    if (driverPackage.Create())
                    {
                        string objectPath = null;
                        refreshPackage = false;
                        failedImports = 0;
                        refreshDP = false;
                        progressTotal = 0;
                        progressCount = 0;

                        try
                        {
                            // lock driver packge object
                            objectPath = driverPackage.Package["__RELPath"].StringValue;
                            Utility.RequestLock(ConnectionManager, objectPath);

                            int totalInfs = driverPackage.Infs.Length;
                            int num2 = 1;
                            // parse ini files and create a dictinonary with the results
                            log.Debug("ProcessingINFs ---- ");
                            foreach (string inf in driverPackage.Infs)
                            {
                                // I still hate calculating progress bars
                                progresPercent = (num2 / totalInfs * 100) / progressStepCount / totalDriverPackages;
                                worker.ReportProgress(
                                    progressStart + (progresPercent), 
                                    string.Format("Reading {1} ({0})", driverPackage.Name, Path.GetFileName(inf))
                                    );

                                log.Debug("ProcessingINF: " + inf);
                                Driver driver = new Driver(inf);
                                if (driver.HasWarning)
                                {
                                    driverPackage.ImportWarning[inf] = driver.Warning.Message;
                                }
                                else if (driver.HasException)
                                {
                                    driverPackage.ImportError[inf] = driver.Exception.Message;
                                }
                                else
                                {
                                    // need to check versions to for duplicate drivers with differnt version number
                                    driverPackage.Drivers[Path.GetFileName(inf) + driver.Version] = driver;
                                }

                                num2++;
                            }
                            // I still hate calculating progress bars
                            progressStepPercent = progresPercent;
                            progresPercent = 100 / progressStepCount / totalDriverPackages * 2;
                            log.Debug("ProcessingDrivers ---- ");
                            // check if driver is in driver package and same version
                            // Check several drivers at once, so it's faster. A failed check is logged and listed with the
                            // package's import errors (the old thread pool dropped those errors silently).
                            List<IResultObject> driversInPackage = driverPackage.GetDriversInPackge().ToList();
                            object errorSync = new object();
                            Parallel.ForEach(driversInPackage, new ParallelOptions { MaxDegreeOfParallelism = ParallelDriverChecks }, driverObject =>
                            {
                                try
                                {
                                    ProcessDrivers(driverPackage, driverObject, worker);
                                }
                                catch (Exception ex)
                                {
                                    string driverName = driverObject["LocalizedDisplayName"].StringValue;
                                    log.Error("Checking driver " + driverName + " failed", ex);
                                    lock (errorSync)
                                    {
                                        driverPackage.ImportError[driverName] = ex.Message;
                                    }
                                }
                            });
                            // I still hate calculating progress bars
                            progressStepPercent = progresPercent;
                            log.Debug("ImportingDrivers ---- ");

                            // Import one driver at a time: the SMS provider's CreateFromINF fails when it gets too many requests at once.
                            // A failed import is logged and listed with the package's import errors.
                            List<Driver> driversToImport = driverPackage.Drivers.Values.Where(d => d.Import).ToList();
                            progressTotal = driversToImport.Count;
                            foreach (Driver driverToImport in driversToImport)
                            {
                                try
                                {
                                    ImportDrivers(driverPackage, driverToImport, worker);
                                }
                                catch (Exception ex)
                                {
                                    log.Error("Importing driver " + driverToImport.InfLocation + " failed", ex);
                                    driverPackage.ImportError[driverToImport.InfLocation] = ex.Message;
                                    failedImports++;
                                }
                            }

                            if (refreshPackage)
                            {
                                log.Debug("RefreshPackage ---- ");
                                // I still hate calculating progress bars
                                progresPercent = 100 / progressStepCount / totalDriverPackages * 4;
                                worker.ReportProgress(progressStart + (progresPercent), string.Format("Adding the drivers to {0}", driverPackage.Name));
                                // add all drivers in the drivers list to the driver package
                                if (!driverPackage.AddDriversToDriverPack())
                                    failedImports++;
                            }

                            if (refreshDP)
                            {
                                log.Debug("RefreshDP ---- ");
                                // I still hate calculating progress bars
                                progresPercent = 100 / progressStepCount / totalDriverPackages * 5;
                                worker.ReportProgress(progressStart + (progresPercent), string.Format("Updating {0} on its distribution points", driverPackage.Name));
                                driverPackage.Package.ExecuteMethod("RefreshPkgSource", null);
                            }

                            // Mark the package as imported only when every driver made it. Otherwise the next run would
                            // call it "No change" and skip it, leaving the failed drivers out for good.
                            if (failedImports > 0)
                            {
                                log.Warn(string.Format("{0}: ConfigMgr refused {1} driver(s), so the package isn't marked as imported; the next import offers it again", driverPackage.Name, failedImports));
                            }
                            else
                            {
                                log.Debug("CreateHashFile ---- ");
                                driverPackage.CreateHashFile();
                                log.Debug("UpdatePackageVersion ---- ");
                                driverPackage.UpdatePackageVersion();
                            }
                        }
                        finally
                        {
                            // unlock driver package object
                            Utility.ReleaseLock(ConnectionManager, objectPath);
                        }
                    }
                    ++num;
                }

                PrepareCompletion();
                base.PostApply(worker, e);
            }
            catch (Exception ex)
            {
                AddRefreshResultObject(null, PropertyDataUpdateAction.RefreshAll);
                PrepareError(ex.Message);
            }
        }

        private bool ImportDrivers(DriverPackage driverPackage, Driver driver, BackgroundWorker worker)
        {
            ++progressCount;

            progresPercent = progressStepPercent + Convert.ToInt32(((double)progressCount / progressTotal * 100) / progressStepCount / totalDriverPackages);

            worker.ReportProgress(
               progressStart + (progresPercent),
               string.Format("Driver {1} of {2}: {3} {4} ({0})",
                   driverPackage.Name,
                   progressCount,
                   progressTotal,
                   driver.Model,
                   driver.Version
                   )
               );

            log.Debug("Importing driver: " + driver.Model);
            // this is a great idea but throws an file hash error when adding driver to package
            if (registry.ReadBool("DriverPackageQuickMerge") && driver.CheckIfExists(ConnectionManager))
            {
                log.Debug("QuickMergeCheckIfExists: " + driver.Model);
                if (!driverPackage.AddDriverToCategory(driver))
                    failedImports++;
            }
            else if (driver.CreateObjectFromInfFile(ConnectionManager))
            {
                log.Debug("CreateObjectFromInfFile: " + driver.Model);
                // add category to driver object
                if (!driverPackage.AddDriverToCategory(driver))
                    failedImports++;
            }
            else
            {
                // List it with the package's import errors; this used to be dropped without a word.
                driverPackage.ImportError[driver.InfLocation] = driver.HasException
                    ? driver.Exception.Message
                    : "ConfigMgr didn't import this driver.";
                failedImports++;
            }
            log.Debug("Done importing driver: " + driver.Model);

            refreshPackage = true;
            refreshDP = true;

            return true;
        }

        private bool ProcessDrivers(DriverPackage driverPackage, IResultObject driverObject, BackgroundWorker worker)
        {
            worker.ReportProgress(
                progressStart + (progresPercent),
                string.Format("Checking {1} {2} ({0})",
                    driverPackage.Name, driverObject["LocalizedDisplayName"].StringValue,
                    driverObject["DriverVersion"].StringValue
                    )
                );

            if (driverPackage.Drivers.TryGetValue(driverObject["DriverINFFile"].StringValue + driverObject["DriverVersion"].StringValue, out Driver driver))
            {
                if (driver.Version == driverObject["DriverVersion"].StringValue)
                {
                    driver.AddObject(driverObject);

                    if (driverPackage.DriverContentExists(driver))
                    {
                        driverPackage.AddDriverToCategory(driver);
                        driver.Import = false;
                    }
                }
            }
            else
            {
                worker.ReportProgress(
                    progressStart + (progresPercent),
                    string.Format("Removing {1} {2} from {0}",
                        driverPackage.Name, driverObject["LocalizedDisplayName"].StringValue,
                        driverObject["DriverVersion"].StringValue)
                    );
                // remove from driver package and category if source folder have been removed
                driverPackage.RemoveDriverFromCategory(driverObject);
                if (driverPackage.RemoveDriverFromDriverPack(driverObject))
                {
                    refreshDP = true;
                }
            }

            return true;
        }

        public override void OnAddSummary(SummaryRequestHandler handler)
        {
            base.OnAddSummary(handler);

            List<DriverPackage> list = new List<DriverPackage>();
            foreach (DataGridViewRow dataGridViewRow in dataGridViewDriverPackages.Rows)
            {
                if (Convert.ToBoolean(dataGridViewRow.Cells[columnSelected.Name].Value) == true)
                {
                    if (dataGridViewRow.Tag is DriverPackage package)
                    {
                        list.Add(package);
                    }
                }
            }
            UserData["DriverPackageItems"] = list;

            AddAction("GeneralDescription", string.Format("The following driver package(s) will be imported ({0}):", list.Count));
            AddAction("PackageInformation", string.Empty);

            foreach (DriverPackage package in list)
                AddActionDetailMessage("PackageInformation", package.Name);
        }

        private void PrepareCompletion()
        {
            RemoveAllSummary();

            bool hasError = false;
            bool hasWarning = false;
            List<string> packageSuccess = new List<string>();
            List<DriverPackage> packageError = new List<DriverPackage>();
            foreach (DriverPackage package in (List<DriverPackage>)UserData["DriverPackageItems"])
            {
                if (package.HasDriverWarning)
                {
                    hasWarning = true;
                }
                if (package.HasException || package.HasDriverError)
                {
                    hasError = true;
                }

                if (package.HasException || package.HasDriverError || package.HasDriverWarning)
                    packageError.Add(package);
                else
                    packageSuccess.Add(package.Name);
            }

            string title = "All driver package(s) imported successfully.";
            AddAction("GeneralDescription", title);
            if (hasWarning)
            {
                title = "Some driver package(s) imported with warning. See following details.";
                UpdateActionStatus("GeneralDescription", SmsSummaryAction.ActionStatus.CompleteWithWarnings);
            }
            if (hasError)
            {
                title = "Some driver package(s) cannot be imported successfully. See following details.";
                UpdateActionStatus("GeneralDescription", SmsSummaryAction.ActionStatus.CompleteWithErrors);
            }
            UpdateAction("GeneralDescription", title);

            if (packageSuccess.Count > 0)
            {
                AddAction("PackageInformation", string.Format("The following driver package(s) were imported ({0}):", packageSuccess.Count));
                UpdateActionStatus("PackageInformation", SmsSummaryAction.ActionStatus.CompleteWithSuccess);

                foreach (string message in packageSuccess)
                    AddActionDetailMessage("PackageInformation", message);

                AddAction("EmptyLine", string.Empty);
            }

            if (hasError || hasWarning)
            {
                foreach (DriverPackage package in packageError)
                {
                    AddAction(package.Name, package.Name);

                    if (package.HasDriverWarning)
                    {
                        UpdateActionStatus(package.Name, SmsSummaryAction.ActionStatus.CompleteWithWarnings);
                    }
                    if (package.HasException || package.HasDriverError)
                    {
                        UpdateActionStatus(package.Name, SmsSummaryAction.ActionStatus.CompleteWithErrors);
                    }

                    if (package.HasDriverWarning)
                    {
                        foreach (KeyValuePair<string, string> warning in package.ImportWarning)
                        {
                            string str = string.Format("• {0}\nMessage: {1}", new object[2]
                            {
                                warning.Key.Replace((string)UserData["sourceDirectory"], ""),
                                warning.Value
                            });
                            AddActionWarningMessage(package.Name, str);
                        }
                    }

                    if (package.Exception.Count > 0)
                    {
                        string str = string.Format(CultureInfo.CurrentCulture, "• {0}", new object[1]
                        {
                             package.Exception.FirstOrDefault().Message
                        });
                        AddActionErrorMessage(package.Name, str);
                    }

                    if (package.HasDriverError)
                    {
                        foreach (KeyValuePair<string, string> error in package.ImportError)
                        {
                            string str = string.Format("• {0}\n{1}", new object[2]
                            {
                                error.Key.Replace((string)UserData["sourceDirectory"], ""),
                                error.Value
                            });
                            AddActionErrorMessage(package.Name, str);
                        }
                    }
                    AddAction("EmptyLine", string.Empty);
                }
            }
        }

        private void PrepareError(string errorMessage)
        {
            RemoveAllSummary();
            AddAction("ErrorInfo", errorMessage);
            UpdateActionStatus("ErrorInfo", SmsSummaryAction.ActionStatus.CompleteWithErrors);
        }

        private void RemoveAllSummary()
        {
            foreach (string id in Enumerable.ToList(Enumerable.Select(GetSummaryItems(), i => i.Id)))
                RemoveItem(id);
        }

        public override void OnActivated()
        {
            base.OnActivated();
        }

        public override bool OnDeactivate()
        {
            dataGridViewDriverPackages.EndEdit();
            return base.OnDeactivate();
        }

        private void InitializeDataGridView()
        {
            List<DriverPackage> driverPackages = (List<DriverPackage>)UserData["DriverPackages"];

            dataGridViewDriverPackages.Rows.Clear();
            foreach (DriverPackage package in driverPackages)
            {
                DataGridViewRow dataGridViewRow = new DataGridViewRow();
                dataGridViewRow.CreateCells(dataGridViewDriverPackages);

                dataGridViewRow.Cells[0].Value = package.Import ? true : false;
                dataGridViewRow.Cells[1].Value = package.Name;
                dataGridViewRow.Cells[2].Value = package.FileVersion;
                dataGridViewRow.Cells[3].Value = package.Import ? string.Format("Found {0} drivers", package.Infs.Length) : "No change";

                dataGridViewRow.Tag = package;
                dataGridViewDriverPackages.Rows.Add(dataGridViewRow);
            }

            Utility.UpdateDataGridViewColumnsSize(dataGridViewDriverPackages, columnPackage);

            Initialized = true;
        }

        private ControlDataState ValidateSelectedDriverPackages()
        {
            foreach (DataGridViewRow dataGridViewRow in dataGridViewDriverPackages.Rows)
            {
                if (Convert.ToBoolean(dataGridViewRow.Cells[0].Value, CultureInfo.InvariantCulture))
                {
                    return ControlDataState.Valid;
                }
            }
            return ControlDataState.Invalid;
        }

        private void ButtonSelect_Click(object sender, EventArgs e)
        {
            dataGridViewDriverPackages.Focus();
            dataGridViewDriverPackages.BeginEdit(true);
            bool flag = sender == buttonSelectAll;
            foreach (DataGridViewRow dataGridViewRow in dataGridViewDriverPackages.Rows)
                dataGridViewRow.Cells[0].Value = flag ? true : false;
            dataGridViewDriverPackages.EndEdit();
        }

        private void DataGridViewDrivers_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (!dataGridViewDriverPackages.IsCurrentCellDirty)
                return;
            dataGridViewDriverPackages.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void DataGridViewDrivers_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            ControlsInspector.InspectAll();
            Dirty = !ReadOnly;
        }

        private void DataGridViewDrivers_KeyUp(object sender, KeyEventArgs e)
        {
            Utility.SelectDataGridViewWithSpace(e, (DataGridView)sender, columnSelected);
        }
    }
}
