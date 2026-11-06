using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.AdminConsole.CollectionProperty.DeployWizard;
using Microsoft.ConfigurationManagement.AdminConsole.Common;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Forms;

namespace Layer8.Device
{
    public static class Device
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(Device));

        /// <summary>
        /// Developer tool, not shipped in the MSI (see Extensions.Dev): logs the selected objects' class and every
        /// property, to learn what a console node hands Layer8 before writing a feature for it.
        /// </summary>
        public static void ProbeSelection(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run("Probe Selection", () =>
            {
                int count = 0;
                foreach (IResultObject item in selectedObject)
                {
                    System.Text.StringBuilder text = new System.Text.StringBuilder("Probe: " + item.ObjectClass);
                    foreach (string name in item.PropertyNames)
                    {
                        object value;
                        try { value = item[name].ObjectValue; }
                        catch (Exception ex) { value = "(" + ex.GetType().Name + ")"; }
                        text.AppendLine().Append("  ").Append(name).Append(" = ").Append(value);
                    }
                    log.Info(text.ToString());
                    count++;
                }
                System.Windows.Forms.MessageBox.Show(string.Format("Logged {0} object(s) with all their properties to {1}", count, Layer8Log.LogFile), "Probe Selection");
            });
        }

        /// <summary>
        /// Console entry point for Layer8 Settings... on the Layer8 menu. The settings aren't about the selected
        /// device; the console just opens its settings window (a property sheet) on whatever is selected.
        /// </summary>
        public static void OpenSettings(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Layer8 Settings", () => Utility.ShowDialog("Layer8Settings", selectedObject, "Layer8 Settings"));
        }

        /// <summary>Console entry point: runs AddMulitDeviceCollectionCore behind Layer8's crash guard.</summary>
        public static void AddMulitDeviceCollection(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status Status)
        {
            SafeAction.Run(action?.DisplayName ?? "AddMulitDeviceCollection", () => AddMulitDeviceCollectionCore(sender, scopeNode, action, selectedObject, dataUpdatedDelegate, Status));
        }

        private static void AddMulitDeviceCollectionCore(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObject, PropertyDataUpdated dataUpdatedDelegate, Status Status)
        {
            try
            {
                using (BrowseCollectionDialog collectionDialog = new BrowseCollectionDialog(selectedObject.ConnectionManager))
                {
                    collectionDialog.MultiSelect = true;
                    collectionDialog.CollectionType = CollectionType.Device;
                    collectionDialog.CollectionFilter = (collectionResultObject =>
                    {
                        if (collectionResultObject["IsReferenceCollection"].BooleanValue || collectionResultObject["IsBuiltIn"].BooleanValue)
                            return false;
                        if (selectedObject.Count > 1)
                        {
                            foreach (IResultObject resultObject in selectedObject)
                            {
                                if (resultObject.ObjectClass == "SMS_Collection" && resultObject["CollectionID"].StringValue == collectionResultObject["CollectionID"].StringValue)
                                    return false;
                            }
                            return true;
                        }
                        return !(selectedObject.ObjectClass == "SMS_Collection") || !(selectedObject["CollectionID"].StringValue == collectionResultObject["CollectionID"].StringValue);
                    });

                    if (collectionDialog.ShowDialog() != DialogResult.OK)
                        return;

                    foreach (IResultObject collection in collectionDialog.SelectedCollections)
                    {
                        List<IResultObject> list = new List<IResultObject>();
                        foreach (IResultObject item in selectedObject)
                        {
                            IResultObject instance = selectedObject.ConnectionManager.CreateEmbeddedObjectInstance("SMS_CollectionRuleDirect");
                            instance["ResourceClassName"].StringValue = "SMS_R_System";
                            instance["RuleName"].StringValue = item["Name"].StringValue;
                            instance["ResourceID"].IntegerValue = item["ResourceID"].IntegerValue;
                            list.Add(instance);
                        }
                        collection.ExecuteMethod("AddMembershipRules", new Dictionary<string, object>() { { "collectionRules", list } });
                    }
                }
            }
            catch (SmsQueryException ex)
            {
                SccmExceptionDialog.ShowDialog(SnapIn.Console, ex);
            }
        }

        /// <summary>
        /// Console entry point: copies the selected devices' names to the clipboard, one per line. For a
        /// selected collection it copies the names of all its members instead.
        /// </summary>
        // Copy Device Info's columns: what a ticket needs, all from the site's record, so offline devices work too.
        // Each pair is the site data's property and the header Copy Device Info gives it.
        private static readonly string[,] InfoColumns =
        {
            { "Name", "Name" },
            { "ResourceID", "Resource ID" },
            { "ClientVersion", "Client Version" },
            { "CNIsOnline", "Online" },
            { "CNLastOnlineTime", "Last Online" },
            { "UserName", "Last User" },
            { "PrimaryUser", "Primary User" },
            { "DeviceOS", "Operating System" },
            { "ADSiteName", "AD Site" },
            { "LastMPServerName", "Management Point" },
        };

        /// <summary>Console entry point: copies a tab-separated row per selected device, with a header row, for Excel or a ticket.</summary>
        public static void CopyDeviceInfo(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Copy Device Info", () =>
            {
                int columns = InfoColumns.GetLength(0);
                System.Text.StringBuilder text = new System.Text.StringBuilder(
                    string.Join("\t", Enumerable.Range(0, columns).Select(i => InfoColumns[i, 1]))).AppendLine();
                int count = 0;
                foreach (IResultObject device in selectedObjects)
                {
                    text.AppendLine(string.Join("\t", Enumerable.Range(0, columns).Select(i => ColumnText(device, InfoColumns[i, 0]))));
                    count++;
                }
                System.Windows.Forms.Clipboard.SetDataObject(text.ToString(), true, 10, 100);
                log.InfoFormat("Copy Device Info: copied {0} device(s)", count);
                System.Windows.Forms.MessageBox.Show(string.Format("Copied {0} device{1} (pastes into Excel).", count, count == 1 ? "" : "s"), "Copy Device Info",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            });
        }

        /// <summary>A column as text, or empty when this node's objects don't have it (collection members have fewer).</summary>
        private static string ColumnText(IResultObject device, string property)
        {
            try
            {
                if (property == "CNLastOnlineTime")
                {
                    DateTime? when = ClientActions.SiteTime(device, property);
                    return when.HasValue ? when.Value.ToString("g") : string.Empty;
                }
                object value = device[property].ObjectValue;
                if (value is DateTime given) return given.Year > 2000 ? given.ToString("g") : string.Empty;
                return value?.ToString() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static void CopyNames(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Copy Names", () => CopyNamesCore(action, selectedObjects));
        }

        private static void CopyNamesCore(ActionDescription action, IResultObject selectedObjects)
        {
            List<string> names = new List<string>();
            List<string> collections = new List<string>();
            foreach (IResultObject item in selectedObjects)
            {
                if (item.ObjectClass == "SMS_Collection")
                {
                    collections.Add(item["Name"].StringValue);
                    names.AddRange(CollectionMemberNames(item).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                }
                else
                {
                    names.Add(item["Name"].StringValue);
                }
            }

            names = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show("There are no names to copy.", action?.DisplayName ?? "Copy Names", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Retries for a moment if another program has the clipboard open.
            System.Windows.Forms.Clipboard.SetDataObject(string.Join(Environment.NewLine, names), true, 10, 100);
            Layer8.Layer8Log.GetLogger(typeof(Device)).InfoFormat("Copied {0} name(s) to the clipboard", names.Count);

            // Copying a single device is obvious; for collections, say how many came along.
            if (collections.Count > 0)
            {
                System.Windows.Forms.MessageBox.Show(
                    string.Format("Copied {0} member name{1} from {2}.", names.Count, names.Count == 1 ? "" : "s", string.Join(", ", collections)),
                    action?.DisplayName ?? "Copy Names", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static IEnumerable<string> CollectionMemberNames(IResultObject collection)
        {
            string query = string.Format("SELECT Name FROM SMS_FullCollectionMembership WHERE CollectionID = '{0}'", Utility.WqlEscapeString(collection["CollectionID"].StringValue));
            using (IResultObject members = collection.ConnectionManager.QueryProcessor.ExecuteQuery(query))
            {
                foreach (IResultObject member in members)
                {
                    yield return member["Name"].StringValue;
                }
            }
        }

        public static void DeleteDeploymentForMonitoring(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObject, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            DeleteDeployment(sender, selectedResultObject, dataUpdatedDelegate, "SoftwareName");
        }

        private static void DeleteDeployment(object sender, IResultObject selectedResultObject, PropertyDataUpdated dataUpdatedDelegate, string displayPropertyName, bool confirmDialog = true)
        {
            IResultObject deploymentFromSummarizer = Utilities.GetCorrespondingDeploymentFromSummarizer(selectedResultObject);

            if (confirmDialog)
            {
                // Name the collection too: the same software is often deployed to several collections.
                string collection = selectedResultObject["CollectionName"].StringValue;
                string question = string.IsNullOrEmpty(collection)
                    ? string.Format("Delete the selected '{0}' deployment?", selectedResultObject[displayPropertyName].StringValue)
                    : string.Format("Delete the '{0}' deployment to {1}?", selectedResultObject[displayPropertyName].StringValue, collection);
                if (System.Windows.MessageBox.Show(question, "Configuration Manager", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
                    return;
            }
            deploymentFromSummarizer.Delete();

            List<PropertyDataUpdateItem> refreshDataList = new List<PropertyDataUpdateItem>
            {
                new PropertyDataUpdateItem(selectedResultObject, PropertyDataUpdateAction.Delete)
            };

            if (dataUpdatedDelegate == null)
                return;

            dataUpdatedDelegate(sender, refreshDataList);
        }
    }
}
