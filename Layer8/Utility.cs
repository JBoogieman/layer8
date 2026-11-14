using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Management;
using System.Windows.Forms;
using Microsoft.ConfigurationManagement.ManagementProvider.DialogFramework;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Layer8
{
    public class Utility
    {
        public const double BytesPerKilobyte = 1024;
        public const double BytesPerMegabyte = 1024 * 1024;

        /// <summary>
        /// A size the way the console shows sizes: 1024-based, in the largest unit the value is at least 1 of,
        /// with up to two decimals by default ("109.56 MB", "5 GB").
        /// </summary>
        public static string FormatBytes(double bytes, string numberFormat = "0.##")
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = bytes;
            int unit = 0;
            while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString(numberFormat, System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
        }

        /// <summary>
        /// Extracts every file in a .cab into destinationFolder, keeping the cab's own folders, with the expand.exe
        /// that's part of Windows. Returns the files that are in destinationFolder afterwards.
        /// clearDestination empties the folder first, for temporary folders Layer8 owns.
        /// </summary>
        public static string[] ExtractCab(string cabPath, string destinationFolder, bool clearDestination = false)
        {
            // No trailing backslash: on expand.exe's command line, a backslash right before the closing quote would escape the quote.
            destinationFolder = destinationFolder.TrimEnd('\\', '/');

            if (clearDestination && Directory.Exists(destinationFolder))
            {
                Directory.Delete(destinationFolder, true);
            }
            Directory.CreateDirectory(destinationFolder);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe"),
                Arguments = string.Format("\"{0}\" -F:* \"{1}\"", cabPath, destinationFolder),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            string output;
            int exitCode;
            using (var expand = System.Diagnostics.Process.Start(startInfo))
            {
                expand.StandardInput.Close();   // nothing to answer, so it can never sit waiting for input
                var errors = expand.StandardError.ReadToEndAsync();
                output = expand.StandardOutput.ReadToEnd() + errors.Result;
                expand.WaitForExit();
                exitCode = expand.ExitCode;
            }

            string[] files = Directory.GetFiles(destinationFolder, "*", SearchOption.AllDirectories);
            if (exitCode != 0 || files.Length == 0)
            {
                output = output.Trim();
                if (output.Length > 1000) output = "..." + output.Substring(output.Length - 1000);
                throw new IOException(string.Format("Could not extract {0} (expand.exe exit code {1}). {2}", cabPath, exitCode, output));
            }
            return files;
        }

        public IEnumerable<IResultObject> GetCollectionMembership(ConnectionManagerBase ConnectionManager, int resourceID)
        {
            string query = string.Format("SELECT SMS_Collection.* FROM SMS_FullCollectionMembership, SMS_Collection where ResourceID = '{0}' and SMS_FullCollectionMembership.CollectionID = SMS_Collection.CollectionID", resourceID);
            using (IResultObject resultObject = ConnectionManager.QueryProcessor.ExecuteQuery(query))
            {
                foreach (IResultObject resultObject1 in resultObject)
                {
                    yield return resultObject1;
                }
            }
        }

        public static bool AddObjectToFolder(ConnectionManagerBase connectionManager, string folderName, string objectId, int objectType)
        {
            // No folder set in Options means leave the object at the top level. Creating a folder with no name fails.
            if (string.IsNullOrWhiteSpace(folderName))
                return false;

            try
            {
                IResultObject resultObject1;
                try
                {
                    string query = string.Format("SELECT * FROM SMS_ObjectContainerNode WHERE Name LIKE '{0}' AND ObjectType={1}", WqlEscapeString(folderName), objectType);
                    resultObject1 = connectionManager.QueryProcessor.ExecuteQuery(query);
                }
                catch (SmsQueryException ex)
                {
                    ExceptionUtilities.TraceException(ex);
                    throw;
                }
                IResultObject resultObject2 = null;
                int? nullable = new int?();
                foreach (IResultObject resultObject3 in resultObject1)
                {
                    if (resultObject3["ObjectType"].IntegerValue == objectType && resultObject3["ParentContainerNodeID"].IntegerValue == 0)
                    {
                        resultObject2 = resultObject3;
                        break;
                    }
                }
                if (resultObject2 == null)
                {
                    try
                    {
                        nullable = CreateFolder(connectionManager, folderName, objectType);
                    }
                    catch (Exception ex)
                    {
                        ExceptionUtilities.TraceException(ex);
                        return false;
                    }
                }
                else
                    nullable = new int?(resultObject2["ContainerNodeID"].IntegerValue);
                IResultObject resultObject4 = null;
                string query2 = string.Format("Select * From SMS_ObjectContainerItem Where InstanceKey='{0}'", objectId);
                IResultObject resultObject5 = connectionManager.QueryProcessor.ExecuteQuery(query2);
                if (resultObject5 != null)
                {
                    IEnumerator enumerator = resultObject5.GetEnumerator();
                    if (enumerator.MoveNext())
                        resultObject4 = (IResultObject)enumerator.Current;
                    if (resultObject4 != null)
                    {
                        if (resultObject4["MemberID"] == null)
                            resultObject4 = null;
                    }
                }
                if (resultObject4 == null)
                {
                    resultObject4 = connectionManager.CreateInstance("SMS_ObjectContainerItem");
                }
                if (resultObject4 == null)
                {
                    return false;
                }
                resultObject4["InstanceKey"].StringValue = objectId;
                resultObject4["ObjectType"].IntegerValue = objectType;
                resultObject4["ContainerNodeID"].IntegerValue = nullable.Value;
                resultObject4.Put();
                resultObject4.Get();
                return true;
            }
            catch (Exception ex)
            {
                ExceptionUtilities.TraceException(ex);
                return false;
            }
        }

        public static int? CreateFolder(ConnectionManagerBase connectionManager, string folderName, int objectType)
        {
            IResultObject instance = connectionManager.CreateInstance("SMS_ObjectContainerNode");
            instance["Name"].StringValue = folderName;
            instance["ObjectType"].IntegerValue = objectType;
            instance["ParentContainerNodeID"].IntegerValue = 0;
            instance.Put();
            instance.Get();
            if (instance == null)
                return new int?();
            return new int?(instance["ContainerNodeID"].IntegerValue);
        }

        /// <summary>
        /// Is host the computer the console runs on? WMI there runs with the console's own token, so Windows UAC
        /// applies: an administrator's token is limited unless the console was started with Run as administrator.
        /// Every other device is reached over the network, where that limit doesn't apply to domain accounts.
        /// </summary>
        public static bool IsThisComputer(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            string name = host.Trim();
            if (name == "." || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            int dot = name.IndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);
            return string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Is the console running with full administrator rights (Run as administrator, or UAC not limiting it)?</summary>
        public static bool IsElevated()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>Did Windows or WMI refuse access?</summary>
        public static bool IsAccessDenied(Exception error)
        {
            return error is UnauthorizedAccessException
                || (error is ManagementException wmi && wmi.ErrorCode == ManagementStatus.AccessDenied)
                || (error is System.Runtime.InteropServices.COMException com && com.ErrorCode == unchecked((int)0x80070005));
        }

        /// <summary>
        /// When an access-denied error came from the computer the console runs on, and the console isn't running as
        /// administrator, the reason and the fix in one short line (it has to fit a result window's Status column);
        /// otherwise null. Reported by people whose admin account isn't the
        /// built-in Administrator: a bare "Access denied" there gives no hint that the fix is Run as administrator.
        /// </summary>
        public static string LocalAccessDeniedReason(string host, Exception error)
        {
            if (!IsAccessDenied(error) || !IsThisComputer(host) || IsElevated()) return null;
            return "Access denied: on the console's own computer, run the console as administrator";
        }

        /// <summary>
        /// The SMS provider's own explanation of a failed call, or the error message when it gave none. The provider's
        /// Description is often blank (WMI's own errors, such as "Invalid operation", have none), so read it only through here.
        /// </summary>
        public static string ProviderError(Exception exception)
        {
            for (Exception e = exception; e != null; e = e.InnerException)
            {
                if (e is ManagementException managementException)
                {
                    string description = null, operation = null;
                    try
                    {
                        description = managementException.ErrorInformation?["Description"]?.ToString();
                        operation = managementException.ErrorInformation?["Operation"]?.ToString();
                    }
                    catch (ManagementException) { }
                    if (!string.IsNullOrWhiteSpace(description))
                        return description.Trim();
                    // When a save fails, the console's connection reconnects and tries again, and the retry fails with
                    // WMI's own "Invalid operation" (a PutClass), so ConfigMgr's reason never reaches us.
                    if (operation == "PutClass" && managementException.ErrorCode == ManagementStatus.InvalidOperation)
                        return "ConfigMgr refused the change, and the console didn't pass on why. SMSProv.log on the site server has the reason.";
                    return managementException.Message.Trim();
                }
            }

            return exception?.Message?.Trim();
        }

        /// <summary>
        /// Saves an object once, so a refusal comes back with ConfigMgr's reason. The console's own Put reconnects and
        /// retries a failed save, and that retry fails with WMI's "Invalid operation", hiding the reason. This makes the
        /// console's first attempt (the same WMI save, with the connection's context) and stops there. Throws
        /// ManagementException.
        /// </summary>
        public static void PutOnce(ConnectionManagerBase connectionManager, IResultObject resultObject)
        {
            if (resultObject is Microsoft.ConfigurationManagement.ManagementProvider.WqlQueryEngine.WqlResultObject wqlObject && wqlObject.ManagedObject != null)
            {
                ManagementNamedValueCollection context = new ManagementNamedValueCollection();
                foreach (KeyValuePair<string, object> item in connectionManager.Context)
                    context.Add(item.Key, item.Value);
                wqlObject.ManagedObject.Put(new PutOptions(context));
            }
            else
            {
                resultObject.Put();
            }
        }

        /// <summary>The SQL Server message behind a failed provider call, if ConfigMgr passed one on.</summary>
        public static string ProviderSqlMessage(Exception exception)
        {
            for (Exception e = exception; e != null; e = e.InnerException)
            {
                if (e is ManagementException managementException)
                {
                    try { return managementException.ErrorInformation?["SQLMessage"]?.ToString(); }
                    catch (ManagementException) { return null; }
                }
            }

            return null;
        }

        public static string WqlEscapeString(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            text = text.Replace("_", "[_]");
            text = text.Replace("'", "''");
            return text;
        }

        public static string CreateHashForFolder(string path)
        {
            // assuming you want to include nested folders
            var exts = new[] { ".hash", ".version" };
            List<string> files = Directory
                .GetFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(file => !exts.Any(x => file.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p).ToList();
            // create a temp file with all the files as content
            string tempHashFile = Path.Combine(Path.GetTempPath(), RandomString(16, false) + ".hash");

            string hash = null;

            using (SHA256 shaHash = SHA256.Create())
            {
                if (files.Count > 0)
                {
                    // create a new file     
                    using (FileStream fs = File.Create(tempHashFile))
                    {
                        StreamWriter writer = new StreamWriter(fs);
                        for (int i = 0; i < files.Count; i++)
                        {
                            string file = files[i];
                            string content = file + " " + file.Length + Environment.NewLine;
                            writer.Write(content);
                        }
                        writer.Close();
                    }
                    shaHash.ComputeHash(Encoding.UTF8.GetBytes(File.ReadAllText(tempHashFile)));
                    hash = BitConverter.ToString(shaHash.Hash).Replace("-", "");

                    File.Delete(tempHashFile);
                }
            }

            return hash;
        }

        public static ManagementScope GetWMIScope(string host, string space, string username = null, string password = null)
        {
            // TODO update to have all the wmi connection catch here, so we can remove it from the other places
            ConnectionOptions options = new ConnectionOptions
            {
                Authentication = AuthenticationLevel.PacketPrivacy,
                Impersonation = ImpersonationLevel.Impersonate,
                EnablePrivileges = true,
                Timeout = TimeSpan.FromSeconds(5)
            };
            if (username != null)
            {
                options.Username = username;
                options.Password = password;
            }

            ManagementScope scope = new ManagementScope(string.Format(@"\\{0}\root\{1}", host, space), options);
            scope.Connect();

            if (scope.IsConnected == true)
            {
                return scope;
            }
            else
            {
                throw new InvalidOperationException("Cannot connect to WMI");
            }
        }

        public static IEnumerable<ManagementObject> SearchWMI(ManagementScope scope, ObjectQuery query)
        {
            ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
            using (ManagementObjectCollection queryCollection = searcher.Get())
            {
                foreach (ManagementObject managmentObject in queryCollection)
                {
                    yield return managmentObject;
                }
            }
        }

        public static List<ManagementObject> SearchWMIToList(ManagementScope scope, string query)
        {
            return SearchWMIToList(scope, new ObjectQuery(query));
        }

        public static List<ManagementObject> SearchWMIToList(ManagementScope scope, ObjectQuery query)
        {
            List<ManagementObject> list = new List<ManagementObject>();
            ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
            using (ManagementObjectCollection queryCollection = searcher.Get())
            {
                foreach (ManagementObject managmentObject in queryCollection)
                {
                    list.Add(managmentObject);
                }
            }
            return list;
        }

        public static ManagementObject GetFirstWMIInstance(ManagementScope scope, string query)
        {
            return GetFirstWMIInstance(scope, new ObjectQuery(query));
        }

        public static ManagementObject GetFirstWMIInstance(ManagementScope scope, ObjectQuery query)
        {
            ManagementObject managmentObject = null;
            ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
            using (ManagementObjectCollection queryCollection = searcher.Get())
            {
                foreach (ManagementObject managmentObject1 in queryCollection)
                    managmentObject = managmentObject1;
            }
            return managmentObject;
        }

        public static ManagementObject GetFirstWMIInstance(string host, string space, string query, string select = "*")
        {
            ManagementScope scope = GetWMIScope(host, space);

            ObjectQuery wmiquery = new ObjectQuery(string.Format("SELECT {0} FROM {1}", select, query));
            ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, wmiquery);
            var enu = searcher.Get().GetEnumerator();
            if (!enu.MoveNext()) throw new Exception("Unexpected WMI query failure");
            return (ManagementObject)enu.Current;
        }

        public static IEnumerable<IResultObject> SearchWMI(ConnectionManagerBase connectionManager, string query)
        {
            using (IResultObject resultObjects = connectionManager.QueryProcessor.ExecuteQuery(query))
            {
                foreach (IResultObject resultObject in resultObjects)
                {
                    yield return resultObject;
                }
            }
        }

        public static List<IResultObject> SearchWMIToList(ConnectionManagerBase connectionManager, string query)
        {
            List<IResultObject> list = new List<IResultObject>();
            using (IResultObject resultObjects = connectionManager.QueryProcessor.ExecuteQuery(query))
            {
                foreach (IResultObject resultObject in resultObjects)
                {
                    list.Add(resultObject);
                }
            }
            return list;
        }

        public static IResultObject GetFirstWMIInstance(ConnectionManagerBase connectionManager, string query)
        {
            IResultObject resultObject = null;
            using (IResultObject resultObjects = connectionManager.QueryProcessor.ExecuteQuery(query))
            {
                foreach (IResultObject resultObject1 in resultObjects)
                    resultObject = resultObject1;
            }
            return resultObject;
        }

        public static void RequestLock(ConnectionManagerBase connectionManager, string objectPath)
        {
            if (connectionManager == null)
                throw new ArgumentNullException("connectionManager");
            if (string.IsNullOrEmpty(objectPath))
                return;
            DateTime dateTime = DateTime.Now.AddSeconds(ReadObjectLockTimeout());
            Dictionary<string, object> methodParameters = new Dictionary<string, object>
            {
                ["ObjectRelPath"] = objectPath,
                ["RequestTransfer"] = true
            };
            while (DateTime.Now < dateTime)
            {
                IResultObject resultObject;
                try
                {
                    resultObject = connectionManager.ExecuteMethod("SMS_ObjectLock", "RequestLock", methodParameters);
                }
                catch (SmsQueryException ex)
                {
                    ExceptionUtilities.TraceException(ex);
                    Thread.Sleep(1000);
                    continue;
                }
                if (resultObject == null)
                {
                    Thread.Sleep(1000);
                }
                else
                {
                    using (resultObject)
                    {
                        if (resultObject["RequestState"].ObjectValue != null)
                        {
                            if (resultObject["RequestState"].IntegerValue == 10 || resultObject["RequestState"].IntegerValue == 11)
                                break;
                            if (resultObject["RequestState"].IntegerValue == 12)
                                break;
                        }
                    }
                    Thread.Sleep(1000);
                }
            }
        }

        public static void ReleaseLock(ConnectionManagerBase connectionManager, string objectPath)
        {
            if (connectionManager == null)
                throw new ArgumentNullException("connectionManager");
            if (string.IsNullOrEmpty(objectPath))
                return;
            Dictionary<string, object> methodParameters = new Dictionary<string, object>();
            methodParameters["ObjectRelPath"] = objectPath;
            connectionManager.ExecuteMethod("SMS_ObjectLock", "ReleaseLock", methodParameters);
        }

        private static int ReadObjectLockTimeout()
        {
            RegistryKey registryKey = null;
            try
            {
                registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\ConfigMgr10\\AdminUI", false);
                if (registryKey != null)
                {
                    try
                    {
                        object obj = registryKey.GetValue("ObjectLockTimeOut");
                        if (obj != null)
                            return (int)obj;
                    }
                    catch (InvalidCastException)
                    {
                    }
                }
                else
                {
                    registryKey = Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\ConfigMgr10\\AdminUI", false);
                    if (registryKey != null)
                    {
                        try
                        {
                            object obj = registryKey.GetValue("ObjectLockTimeOut");
                            if (obj != null)
                                return (int)obj;
                        }
                        catch (InvalidCastException)
                        {
                        }
                    }
                }
            }
            finally
            {
                if (registryKey != null)
                    registryKey.Close();
            }
            return 30;
        }

        public static void UpdateDataGridViewColumnsSize(DataGridView dataGridView, DataGridViewColumn targetColumn)
        {
            if (dataGridView == null || targetColumn == null || !dataGridView.Columns.Contains(targetColumn))
                return;
            int num = dataGridView.Width - dataGridView.Margin.Left - dataGridView.Margin.Right;
            foreach (DataGridViewColumn dataGridViewColumn in dataGridView.Columns)
            {
                if (dataGridViewColumn != targetColumn && dataGridViewColumn.Visible == true)
                    num -= dataGridViewColumn.Width;
            }

            VScrollBar vScrollbar = dataGridView.Controls.OfType<VScrollBar>().First();
            if (vScrollbar.Visible)
            {
                num -= SystemInformation.VerticalScrollBarWidth;
            }
            if (num >= dataGridView.Width - dataGridView.Margin.Left - dataGridView.Margin.Right || num <= targetColumn.MinimumWidth)
                return;
            targetColumn.Width = num;
        }

        public static void SelectDataGridViewWithSpace(KeyEventArgs e, DataGridView dataGridView, DataGridViewColumn targetColumn)
        {
            int selectedRowCount = dataGridView.Rows.GetRowCount(DataGridViewElementStates.Selected);

            if (selectedRowCount > 0 && e.KeyCode == Keys.Space)
            {
                for (int i = 0; i < selectedRowCount; i++)
                {
                    dataGridView.SelectedRows[i].Cells[targetColumn.Name].Value = !(bool)dataGridView.SelectedRows[i].Cells[targetColumn.Name].Value;
                }

                e.Handled = true;
            }
        }

        public static void CopyToClipboard(ListView listView)
        {
            StringBuilder buffer = new StringBuilder();
            foreach (ListViewItem item in listView.SelectedItems)
            {
                foreach (ListViewItem.ListViewSubItem subitem in item.SubItems)
                {
                    buffer.Append(subitem.Text);
                    buffer.Append("\t");
                }
                buffer.AppendLine();
            }
            buffer.Remove(buffer.Length - 1, 1);
            Clipboard.SetData(DataFormats.Text, buffer.ToString());
        }

        public static string RandomString(int size, bool lowerCase)
        {
            StringBuilder builder = new StringBuilder();
            Random random = new Random();
            char ch;
            for (int i = 0; i < size; i++)
            {
                ch = Convert.ToChar(Convert.ToInt32(Math.Floor(26 * random.NextDouble() + 65)));
                builder.Append(ch);
            }
            if (lowerCase)
                return builder.ToString().ToLower();
            return builder.ToString();
        }

        public static bool InclusiveBetween(IComparable a, IComparable b, IComparable c)
        {
            return a.CompareTo(b) >= 0 && a.CompareTo(c) <= 0;
        }

        public static DialogResult ShowDialog(string dialogId, IResultObject resultObject, string title = null)
        {
            ISmsForm dialog = DialogFactoryBase.DialogFactory.CreateDialog(dialogId, resultObject, (PropertyDataUpdated)null, false);
            using (dialog.Form)
            {
                dialog.Initialize();
                dialog.Form.ShowInTaskbar = false;
                if (title != null)
                {
                    // A property sheet names itself after the selected objects ("DEV01 Properties"), which is wrong
                    // for a window like Layer8 Settings. It may set that name while loading, so set ours after too.
                    dialog.Form.Text = title;
                    dialog.Form.Load += (sender, e) => dialog.Form.Text = title;
                    dialog.Form.Shown += (sender, e) => dialog.Form.Text = title;
                }
                return dialog.Form.ShowDialog();
            }
        }
        /// <summary>
        /// The size a web server reports for a file, or -1 if it doesn't say or can't be reached. Callers compare it
        /// with a file they already downloaded, so -1 simply means "download it again".
        /// </summary>
        public static long GetFileSize(Uri url)
        {
            try
            {
                WebRequest req = WebRequest.Create(url);
                req.Method = "HEAD";
                using (WebResponse resp = req.GetResponse())
                {
                    if (long.TryParse(resp.Headers.Get("Content-Length"), out long contentLength))
                    {
                        return contentLength;
                    }
                }
            }
            catch (Exception ex)
            {
                Layer8Log.GetLogger(typeof(Utility)).Warn("Could not get the size of " + url, ex);
            }
            return -1;
        }

        public static long GetFileSize(string url)
        {
            return GetFileSize(new Uri(url)); 
        }

        public static void Copy(string sourceDir, string targetDir, bool overwrite = false)
        {
            if (!Directory.Exists(targetDir) || overwrite)
            {
                Directory.CreateDirectory(targetDir);

                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite);
                }

                foreach (var directory in Directory.GetDirectories(sourceDir))
                    Copy(directory, Path.Combine(targetDir, Path.GetFileName(directory)), overwrite);
            }
        }

        /// <summary>
        /// Whether the current user can use a folder the way the caller needs. It tries the access instead of
        /// reading the folder's permissions: the old permissions check said yes when any rule granted the right,
        /// even one for another user or a Deny rule. For write-type rights it creates a temporary file that's
        /// deleted as it closes; for read rights it lists the folder.
        /// </summary>
        public static bool CheckFolderPermissions(string directoryPath, FileSystemRights accessType)
        {
            const FileSystemRights writeRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
            try
            {
                if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath)) return false;

                if ((accessType & writeRights) != 0)
                {
                    string probe = Path.Combine(directoryPath, ".layer8-access-check-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                    return true;
                }

                using (IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directoryPath).GetEnumerator())
                {
                    entries.MoveNext();
                }
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public class ModifyRegistry
    {
        public string SubKey { get; set; } = "SOFTWARE\\Layer8";
        public RegistryKey BaseRegistryKey { get; set; } = Registry.CurrentUser;

        // Every read tolerates a missing key, a missing value and a value someone hand-edited into the wrong
        // type: it falls back to the default instead of throwing inside a wizard page.
        protected object Read(string KeyName)
        {
            try
            {
                using (RegistryKey key = BaseRegistryKey.OpenSubKey(SubKey))
                {
                    return key?.GetValue(KeyName);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public int ReadInt(string KeyName)
        {
            return int.TryParse(Read(KeyName)?.ToString(), out int value) ? value : 0;
        }

        public double? ReadDouble(string KeyName)
        {
            return double.TryParse(Read(KeyName)?.ToString(), out double value) ? value : (double?)null;
        }

        public bool ReadBool(string KeyName)
        {
            return bool.TryParse(Read(KeyName)?.ToString(), out bool value) && value;
        }

        public string ReadString(string KeyName)
        {
            return Read(KeyName)?.ToString() ?? string.Empty;
        }

        public bool Write(string KeyName, object Value)
        {
            try
            {
                // BaseRegistryKey is the shared HKCU root: open and close only the subkey.
                using (RegistryKey key = BaseRegistryKey.CreateSubKey(SubKey))
                {
                    key.SetValue(KeyName, Value);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
