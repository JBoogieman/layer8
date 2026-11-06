using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Layer8.Device
{
    /// <summary>
    /// Monitoring > Distribution Status > Content Status: send content again to every distribution point where it's
    /// retrying or failed, the same as Redistribute on each DP in the console (it sets SMS_DistributionPoint.RefreshNow).
    /// </summary>
    public static class ContentRedistribute
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(ContentRedistribute));

        // SMS_PackageStatusDistPointsSummarizer.State: 2 INSTALL_RETRYING, 3 INSTALL_FAILED.
        private const string BrokenStates = "2, 3";

        private sealed class Failure
        {
            public string PackageID, PackageName, ServerNALPath;
            public int State;
        }

        /// <summary>Console entry point: redistributes the selected content to every DP in error for it.</summary>
        public static void RedistributeFailed(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Redistribute Failed Content", () => RedistributeCore(selectedResultObjects));
        }

        private static void RedistributeCore(IResultObject selection)
        {
            List<Failure> failures = FindFailures(selection);
            if (failures.Count == 0)
            {
                MessageBox.Show("No distribution point is retrying or failed for the selected content.", "Redistribute Failed Content", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string list = string.Join("\n", failures.Take(15).Select(f => f.PackageName + " on " + DpName(f.ServerNALPath)));
            if (failures.Count > 15) list += string.Format("\n...and {0} more", failures.Count - 15);
            if (MessageBox.Show(string.Format("Redistribute to the {0} distribution point{1} in error?\n\n{2}", failures.Count, failures.Count == 1 ? "" : "s", list),
                "Redistribute Failed Content", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            int sent = 0;
            List<string> errors = new List<string>();
            foreach (Failure failure in failures)
            {
                try
                {
                    if (Redistribute(selection.ConnectionManager, failure)) sent++;
                    else errors.Add(DpName(failure.ServerNALPath) + ": no distribution point record for " + failure.PackageID);
                }
                catch (SmsException ex)
                {
                    errors.Add(DpName(failure.ServerNALPath) + ": " + ex.Message);
                    log.Warn("Redistribute " + failure.PackageID + " to " + DpName(failure.ServerNALPath) + " failed", ex);
                }
            }

            StringBuilder summary = new StringBuilder().AppendFormat("Redistribution started on {0} of {1}. distmgr.log on the site server shows it working.", sent, failures.Count);
            if (errors.Count > 0) summary.AppendLine().AppendLine().Append(string.Join("\n", errors));
            MessageBox.Show(summary.ToString(), "Redistribute Failed Content", MessageBoxButtons.OK, errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        /// <summary>Every DP in error for the selected content (Content Status rows are SMS_ObjectContentExtraInfo).</summary>
        private static List<Failure> FindFailures(IResultObject selection)
        {
            List<Failure> failures = new List<Failure>();
            foreach (IResultObject item in selection)
            {
                string packageId = item["PackageID"].StringValue;
                string name = item["SoftwareName"].StringValue;
                if (string.IsNullOrEmpty(name)) name = packageId;
                string wql = string.Format("SELECT PackageID, ServerNALPath, State FROM SMS_PackageStatusDistPointsSummarizer WHERE PackageID = '{0}' AND State IN ({1})", packageId, BrokenStates);
                using (IResultObject rows = selection.ConnectionManager.QueryProcessor.ExecuteQuery(wql))
                {
                    foreach (IResultObject row in rows)
                    {
                        failures.Add(new Failure { PackageID = packageId, PackageName = name, ServerNALPath = row["ServerNALPath"].StringValue, State = row["State"].IntegerValue });
                    }
                }
            }
            return failures;
        }

        /// <summary>Sets RefreshNow on the package's record for that DP. Matched in code, so the NAL path needs no WQL escaping.</summary>
        private static bool Redistribute(ConnectionManagerBase connection, Failure failure)
        {
            string wql = string.Format("SELECT * FROM SMS_DistributionPoint WHERE PackageID = '{0}'", failure.PackageID);
            using (IResultObject dps = connection.QueryProcessor.ExecuteQuery(wql))
            {
                foreach (IResultObject dp in dps)
                {
                    if (!string.Equals(dp["ServerNALPath"].StringValue, failure.ServerNALPath, StringComparison.OrdinalIgnoreCase)) continue;
                    dp["RefreshNow"].BooleanValue = true;
                    dp.Put();
                    log.InfoFormat("Redistributing {0} ({1}) to {2}", failure.PackageName, failure.PackageID, DpName(failure.ServerNALPath));
                    return true;
                }
            }
            return false;
        }

        /// <summary>The server name out of a NAL path such as ["Display=\\CM01\"]MSWNET:["SMS_SITE=LAB"]\\CM01\.</summary>
        internal static string DpName(string nalPath)
        {
            Match match = Regex.Match(nalPath ?? string.Empty, @"\\\\([^\\""\]]+)\\?$");
            return match.Success ? match.Groups[1].Value : nalPath;
        }
    }
}
