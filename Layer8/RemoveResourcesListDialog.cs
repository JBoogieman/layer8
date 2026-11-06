using Microsoft.ConfigurationManagement.ManagementProvider;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Layer8.CollectionManagement
{
    /// <summary>
    /// Remove Resources List: the Add Resources window in reverse. Paste names, search, and the devices that are
    /// direct members of the collection lose their direct rule. Devices that are members through a query rule or an
    /// include can't be removed this way, so they're listed and left alone.
    /// </summary>
    public class RemoveResourcesListDialog : AddResourcesListDialog
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(RemoveResourcesListDialog));

        public RemoveResourcesListDialog()
        {
            Title = "Remove Resources from Collection";
        }

        protected override bool ApplyToCollection(List<IResultObject> resources)
        {
            // CollectionRules is a lazy property: read the collection itself to get it.
            SelectedObject.Get();
            HashSet<int> wanted = new HashSet<int>(resources.Select(r => r["ResourceID"].IntegerValue));
            List<IResultObject> remove = new List<IResultObject>();
            HashSet<int> direct = new HashSet<int>();
            foreach (IResultObject rule in SelectedObject.GetArrayItems("CollectionRules"))
            {
                if (rule.ObjectClass != "SMS_CollectionRuleDirect") continue;
                int id = rule["ResourceID"].IntegerValue;
                if (wanted.Contains(id) && direct.Add(id)) remove.Add(rule);
            }

            string collection = SelectedObject["Name"].StringValue;
            List<string> notDirect = resources.Where(r => !direct.Contains(r["ResourceID"].IntegerValue)).Select(r => r["Name"].StringValue).ToList();
            if (remove.Count == 0)
            {
                MessageBox.Show(string.Format("None of these devices is a direct member of {0}, so there's no rule to remove.", collection),
                    "Remove Resources", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            string question = string.Format("Remove {0} device{1} from {2}?", remove.Count, remove.Count == 1 ? "" : "s", collection);
            if (notDirect.Count > 0) question += string.Format("\n\nNot direct members, so left alone: {0}", Shorten(notDirect));
            if (MessageBox.Show(question, "Remove Resources", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;

            SelectedObject.ExecuteMethod("DeleteMembershipRules", new Dictionary<string, object> { { "collectionRules", remove } });
            log.InfoFormat("Removed {0} direct member(s) from {1}", remove.Count, collection);
            return true;
        }

        private static string Shorten(List<string> names)
        {
            const int Shown = 10;
            return names.Count <= Shown ? string.Join(", ", names) : string.Join(", ", names.Take(Shown)) + string.Format(" (+{0} more)", names.Count - Shown);
        }
    }
}
