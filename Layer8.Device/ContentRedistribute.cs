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

        /// <summary>Console entry point: copies the distribution points in error for the selected content, for a ticket or a DP team.</summary>
        public static void CopyFailed(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Copy Failed Distribution Points", () =>
            {
                List<Failure> failures = FindFailures(selectedResultObjects);
                if (failures.Count == 0)
                {
                    MessageBox.Show("No distribution point is retrying or failed for the selected content.", "Copy Failed Distribution Points", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                StringBuilder text = new StringBuilder("Content\tPackage ID\tDistribution point\tState").AppendLine();
                foreach (Failure failure in failures)
                {
                    text.Append(failure.PackageName).Append('\t').Append(failure.PackageID).Append('\t').Append(DpName(failure.ServerNALPath)).Append('\t')
                        .AppendLine(failure.State == 2 ? "Retrying" : "Failed");
                }
                Clipboard.SetDataObject(text.ToString(), true, 10, 100);
                log.InfoFormat("Copied {0} distribution point(s) in error", failures.Count);
                MessageBox.Show(string.Format("Copied {0} distribution point{1} in error (pastes into Excel).", failures.Count, failures.Count == 1 ? "" : "s"),
                    "Copy Failed Distribution Points", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        // SMS_DistributionDPStatus.MessageState: 1 Success, 2 In Progress, 3 Error. Content Status counts its Failed
        // column from this; anything past In Progress is taken as an error.
        private const int FirstErrorMessageState = 3;

        /// <summary>
        /// Every DP in error for the selected content (Content Status rows are SMS_ObjectContentExtraInfo). Two
        /// records can say so, and they don't always agree: the per-DP status Content Status counts as Failed
        /// (a package whose source can't be read fails there), and the package summarizer's retrying or failed
        /// installs. A DP named by either is listed once.
        /// </summary>
        private static List<Failure> FindFailures(IResultObject selection)
        {
            List<Failure> failures = new List<Failure>();
            foreach (IResultObject item in selection)
            {
                string packageId = item["PackageID"].StringValue;
                string name = item["SoftwareName"].StringValue;
                if (string.IsNullOrEmpty(name)) name = packageId;

                Dictionary<string, Failure> byDp = new Dictionary<string, Failure>(StringComparer.OrdinalIgnoreCase);

                string summarizer = string.Format("SELECT PackageID, ServerNALPath, State FROM SMS_PackageStatusDistPointsSummarizer WHERE PackageID = '{0}' AND State IN ({1})", packageId, BrokenStates);
                using (IResultObject rows = selection.ConnectionManager.QueryProcessor.ExecuteQuery(summarizer))
                {
                    foreach (IResultObject row in rows)
                    {
                        string nal = row["ServerNALPath"].StringValue;
                        byDp[nal] = new Failure { PackageID = packageId, PackageName = name, ServerNALPath = nal, State = row["State"].IntegerValue };
                    }
                }

                string dpStatus = string.Format("SELECT NALPath, MessageState FROM SMS_DistributionDPStatus WHERE PackageID = '{0}' AND MessageState >= {1}", packageId, FirstErrorMessageState);
                try
                {
                    using (IResultObject rows = selection.ConnectionManager.QueryProcessor.ExecuteQuery(dpStatus))
                    {
                        foreach (IResultObject row in rows)
                        {
                            string nal = row["NALPath"].StringValue;
                            if (!string.IsNullOrEmpty(nal) && !byDp.ContainsKey(nal))
                            {
                                byDp[nal] = new Failure { PackageID = packageId, PackageName = name, ServerNALPath = nal, State = 3 };
                            }
                        }
                    }
                }
                catch (SmsQueryException ex)
                {
                    // The summarizer's answer still stands; note why the second record wasn't read.
                    log.Warn("Couldn't read SMS_DistributionDPStatus for " + packageId, ex);
                }

                failures.AddRange(byDp.Values);
            }
            log.InfoFormat("Distribution points in error for the selection: {0}", failures.Count);
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
