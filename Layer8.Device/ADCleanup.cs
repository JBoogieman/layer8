using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.AdminConsole.DialogFramework;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using Microsoft.ConfigurationManagement.ManagementProvider;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.DirectoryServices;
using System.Linq;
using System.Security.Principal;
using System.Threading;

namespace Layer8.Device
{
    /// <summary>Which tile a computer object or a site record is on, in the tiles' order.</summary>
    internal enum CleanupFinding { OK, NotInAD, Disabled, Stale, ADUnused, ClientSilent, NoClient, NotChecked }

    /// <summary>One computer object, with only the attributes <see cref="ADCleanup.Attributes"/> loads. Times in UTC.</summary>
    internal sealed class ADComputer
    {
        public string Name;
        public string AccountName;
        public string DistinguishedName;
        public string Sid;
        public bool Disabled;
        public DateTime? LastLogon;
        public DateTime? PasswordSet;
        public DateTime? Created;
        public string OperatingSystem;

        /// <summary>
        /// When AD last saw the computer: the latest of its last logon, its last password change and its creation.
        /// lastLogonTimestamp is only rewritten once it's 9 to 14 days old (DEV01's said November 18 while it was in
        /// use on the 25th); a domain member changes its password every 30 days, and pwdLastSet is written at once.
        /// Creation counts so a computer object made today isn't stale before it's had the chance to be used.
        /// </summary>
        public DateTime? LastSeen => new[] { LastLogon, PasswordSet, Created }.Where(time => time.HasValue).Max();
    }

    /// <summary>One of the site's device records (SMS_R_System). Times in UTC.</summary>
    internal sealed class SiteRecord
    {
        public int ResourceId;
        public string Name;
        public string Sid;
        /// <summary>ResourceDomainORWorkgroup: a NetBIOS domain name, or a workgroup.</summary>
        public string Domain;
        public bool Client;
        public string OperatingSystem;
        /// <summary>The discovery methods that found it, as the site names them (Heartbeat Discovery, MP_ClientRegistration...).</summary>
        public string FoundBy;
        /// <summary>The last time the site heard from its client at all (HealthCheck's SiteContact.LastSeen); null if never.</summary>
        public DateTime? LastContact;
    }

    /// <summary>A computer object, a site record, or the two matched, and the tile it's on.</summary>
    internal sealed class CleanupEntry
    {
        /// <summary>Null when the site's record has no computer object in AD.</summary>
        public ADComputer Computer;
        /// <summary>Null when the site has no record of the computer.</summary>
        public SiteRecord Record;
        public CleanupFinding Finding;
        /// <summary>Why, where the tile alone doesn't say: how it was matched, or why it couldn't be.</summary>
        public string Note;

        public string Name => Computer?.Name ?? Record?.Name;

        /// <summary>A server by what AD or the site says it runs, for the window's Workstations only box.</summary>
        public bool IsServer =>
            (Computer?.OperatingSystem ?? string.Empty).IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (Record?.OperatingSystem ?? string.Empty).IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>What one pass read: the domain, its computer objects and the site's records.</summary>
    internal sealed class CleanupRead
    {
        /// <summary>The domain's NetBIOS name, the name the site's records carry.</summary>
        public string Domain;
        /// <summary>The domain's distinguished name, where the search started.</summary>
        public string DomainPath;
        public List<ADComputer> Computers;
        public List<SiteRecord> Records;
        /// <summary>Records left out as obsolete: a newer record of the same device replaced each one.</summary>
        public int Obsolete;
        public TimeSpan ADTime, SiteTime;
    }

    /// <summary>
    /// AD Cleanup Report: the domain's computer objects beside the site's device records, each on a tile for what
    /// doesn't add up (not in AD, disabled, stale, AD unused while the client reports, a silent client, no client).
    /// It only reads: one search of AD and two of the site, and no device is contacted.
    /// </summary>
    public static class ADCleanup
    {
        private static readonly Logger log = Layer8Log.GetLogger(typeof(ADCleanup));

        /// <summary>
        /// The only attributes the AD search loads. Nothing secret: no LAPS attribute, and the BitLocker recovery
        /// objects under a computer aren't computer objects, so the search doesn't return them. The regression
        /// script checks this list.
        /// </summary>
        internal static readonly ReadOnlyCollection<string> Attributes = Array.AsReadOnly(new[]
        {
            "name", "sAMAccountName", "distinguishedName", "objectSid", "userAccountControl",
            "lastLogonTimestamp", "pwdLastSet", "whenCreated", "operatingSystem",
        });

        // userAccountControl's ACCOUNTDISABLE flag.
        private const int AccountDisabled = 0x2;
        private static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(10);

        /// <summary>Console entry point, on any device collection: the report covers the whole domain and site either way.</summary>
        public static void Show(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            string title = action?.DisplayName ?? "AD Cleanup Report";
            SafeAction.Run(title, () =>
            {
                using (ADCleanupDialog dialog = new ADCleanupDialog(title, selectedObjects.ConnectionManager))
                {
                    dialog.ShowDialog();
                }
            });
        }

        /// <summary>Reads AD, then the site. Runs on a background thread; stops between results when canceled.</summary>
        internal static CleanupRead Read(ConnectionManagerBase connection, CancellationToken token)
        {
            CleanupRead read = new CleanupRead();
            Stopwatch watch = Stopwatch.StartNew();
            read.Computers = ReadComputers(token, out read.Domain, out read.DomainPath);
            read.ADTime = watch.Elapsed;
            LogTime(string.Format("read {0:N0} computer objects from {1}", read.Computers.Count, read.Domain), read.ADTime);

            watch.Restart();
            read.Records = ReadRecords(connection, token, out read.Obsolete);
            read.SiteTime = watch.Elapsed;
            LogTime(string.Format("read {0:N0} device records from the site ({1:N0} obsolete left out)", read.Records.Count, read.Obsolete), read.SiteTime);
            return read;
        }

        /// <summary>
        /// Every computer object in the console user's domain, a thousand a page, with only <see cref="Attributes"/>;
        /// and the domain's NetBIOS name and distinguished name.
        /// </summary>
        internal static List<ADComputer> ReadComputers(CancellationToken token, out string domain, out string domainPath)
        {
            string configuration;
            using (DirectoryEntry rootDse = new DirectoryEntry("LDAP://RootDSE"))
            {
                domainPath = rootDse.Properties["defaultNamingContext"].Value as string;
                configuration = rootDse.Properties["configurationNamingContext"].Value as string;
            }
            if (string.IsNullOrEmpty(domainPath) || string.IsNullOrEmpty(configuration))
            {
                throw new InvalidOperationException("Active Directory didn't say which domain this is: its RootDSE has no defaultNamingContext.");
            }
            domain = NetBiosName(configuration, domainPath);

            List<ADComputer> computers = new List<ADComputer>();
            using (DirectoryEntry root = new DirectoryEntry(AdsPath(domainPath)))
            using (DirectorySearcher search = new DirectorySearcher(root, "(&(objectCategory=computer)(objectClass=computer))", Attributes.ToArray(), SearchScope.Subtree))
            {
                // Paged, so a domain past a domain controller's 1,000-result limit comes back whole. Not cached:
                // results are read once, front to back.
                search.PageSize = 1000;
                search.CacheResults = false;
                using (SearchResultCollection results = search.FindAll())
                {
                    foreach (SearchResult result in results)
                    {
                        token.ThrowIfCancellationRequested();
                        computers.Add(new ADComputer
                        {
                            Name = First(result, "name") as string,
                            AccountName = First(result, "sAMAccountName") as string,
                            DistinguishedName = First(result, "distinguishedName") as string,
                            Sid = First(result, "objectSid") is byte[] sid ? new SecurityIdentifier(sid, 0).Value : null,
                            Disabled = First(result, "userAccountControl") is int flags && (flags & AccountDisabled) != 0,
                            LastLogon = FileTime(First(result, "lastLogonTimestamp")),
                            PasswordSet = FileTime(First(result, "pwdLastSet")),
                            Created = First(result, "whenCreated") is DateTime created ? DateTime.SpecifyKind(created, DateTimeKind.Utc) : (DateTime?)null,
                            OperatingSystem = First(result, "operatingSystem") as string,
                        });
                    }
                }
            }
            return computers;
        }

        /// <summary>The domain's NetBIOS name, from its entry in the forest's Partitions container.</summary>
        private static string NetBiosName(string configuration, string domainPath)
        {
            using (DirectoryEntry partitions = new DirectoryEntry(AdsPath("CN=Partitions," + configuration)))
            using (DirectorySearcher search = new DirectorySearcher(partitions, "(&(objectClass=crossRef)(nCName=" + BitLockerKeys.LdapEscape(domainPath) + "))",
                                                                   new[] { "nETBIOSName" }, SearchScope.OneLevel))
            {
                SearchResult result = search.FindOne();
                string name = result == null ? null : First(result, "nETBIOSName") as string;
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Active Directory has no NetBIOS name for " + domainPath + ", which the site's records go by.");
                return name;
            }
        }

        /// <summary>
        /// Every device record on the site (SMS_R_System) but the obsolete ones, with when the site last heard from
        /// each client: HealthCheck.ReadSiteContact, the read behind the Health Dashboard's Not reporting tile.
        /// </summary>
        internal static List<SiteRecord> ReadRecords(ConnectionManagerBase connection, CancellationToken token, out int obsolete)
        {
            obsolete = 0;
            List<SiteRecord> records = new List<SiteRecord>();
            using (IResultObject results = connection.QueryProcessor.ExecuteQuery(
                "SELECT ResourceID, Name, SID, Client, Obsolete, ResourceDomainORWorkgroup, OperatingSystemNameandVersion, AgentName FROM SMS_R_System"))
            {
                foreach (IResultObject device in results)
                {
                    token.ThrowIfCancellationRequested();
                    if (Flag(device, "Obsolete"))
                    {
                        obsolete++;
                        continue;
                    }
                    records.Add(new SiteRecord
                    {
                        ResourceId = device["ResourceID"].IntegerValue,
                        Name = Text(device, "Name"),
                        Sid = Text(device, "SID"),
                        Domain = Text(device, "ResourceDomainORWorkgroup"),
                        Client = Flag(device, "Client"),
                        OperatingSystem = Text(device, "OperatingSystemNameandVersion"),
                        FoundBy = device["AgentName"].ObjectValue == null ? null : string.Join("; ", device["AgentName"].StringArrayValue),
                    });
                }
            }

            List<int> clients = records.Where(record => record.Client).Select(record => record.ResourceId).ToList();
            Dictionary<int, SiteContact> contact = HealthCheck.ReadSiteContact(connection, clients, Layer8Settings.NotReportingDays, token);
            foreach (SiteRecord record in records)
            {
                if (contact.TryGetValue(record.ResourceId, out SiteContact site)) record.LastContact = site.LastSeen;
            }
            return records;
        }

        /// <summary>
        /// Puts every computer object and every site record on one tile, the first that fits from the top. A record
        /// matches a computer object by SID, which a rename keeps. A record without one (CM01's came from server
        /// discovery, with none) matches by name, the way LAPS and BitLocker pick a computer object
        /// (BitLockerKeys.Matching): when two objects could be it, it's flagged, never guessed. Records from another
        /// domain or a workgroup aren't checked, since only this domain is read. Internal for the regression script.
        /// </summary>
        internal static List<CleanupEntry> Compare(IList<ADComputer> computers, IList<SiteRecord> records, string domain, int days, DateTime nowUtc)
        {
            Dictionary<string, ADComputer> bySid = new Dictionary<string, ADComputer>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<ADComputer>> byName = new Dictionary<string, List<ADComputer>>(StringComparer.OrdinalIgnoreCase);
            foreach (ADComputer computer in computers)
            {
                if (!string.IsNullOrEmpty(computer.Sid)) bySid[computer.Sid] = computer;
                string account = computer.AccountName != null && computer.AccountName.EndsWith("$", StringComparison.Ordinal)
                    ? computer.AccountName.Substring(0, computer.AccountName.Length - 1)
                    : computer.AccountName;
                foreach (string name in new[] { computer.Name, account }.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!byName.TryGetValue(name, out List<ADComputer> named)) byName[name] = named = new List<ADComputer>();
                    named.Add(computer);
                }
            }

            // Seen up to this long ago counts; "not seen in 90 days" is 90 days or more, like the dashboard's tiles.
            DateTime cutoff = nowUtc.AddDays(-days);
            HashSet<ADComputer> matched = new HashSet<ADComputer>();
            List<CleanupEntry> entries = new List<CleanupEntry>();
            foreach (SiteRecord record in records)
            {
                CleanupEntry entry = new CleanupEntry { Record = record };
                entries.Add(entry);
                if (!string.Equals(record.Domain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Finding = CleanupFinding.NotChecked;
                    entry.Note = string.IsNullOrEmpty(record.Domain)
                        ? "The site's record names no domain; only " + domain + " is read"
                        : "In " + record.Domain + ", not " + domain + ": only " + domain + " is read";
                    continue;
                }

                List<ADComputer> sameName = record.Name != null && byName.TryGetValue(record.Name, out List<ADComputer> found) ? found : new List<ADComputer>();
                if (!string.IsNullOrEmpty(record.Sid))
                {
                    if (!bySid.TryGetValue(record.Sid, out entry.Computer))
                    {
                        entry.Finding = CleanupFinding.NotInAD;
                        entry.Note = sameName.Count > 0
                            ? "No computer object has its SID. One named " + record.Name + " has another: the name was used again, or the computer joined the domain again"
                            : "No computer object has its SID or its name";
                        continue;
                    }
                }
                else
                {
                    List<int> which = BitLockerKeys.Matching(sameName.ConvertAll(computer => computer.AccountName), record.Name);
                    if (which.Count == 0)
                    {
                        entry.Finding = CleanupFinding.NotInAD;
                        entry.Note = "The site's record has no SID, and no computer object has its name";
                        continue;
                    }
                    if (which.Count > 1)
                    {
                        entry.Finding = CleanupFinding.NotChecked;
                        entry.Note = string.Format("{0} computer objects could be it, and the site's record has no SID to tell them apart: {1}",
                                                   which.Count, string.Join("; ", which.Select(index => sameName[index].DistinguishedName)));
                        continue;
                    }
                    entry.Computer = sameName[which[0]];
                    entry.Note = "Matched by name: the site's record has no SID";
                }
                matched.Add(entry.Computer);
                Classify(entry, cutoff);
            }

            foreach (ADComputer computer in computers)
            {
                if (matched.Contains(computer)) continue;
                CleanupEntry entry = new CleanupEntry { Computer = computer };
                Classify(entry, cutoff);
                entries.Add(entry);
            }
            return entries;
        }

        /// <summary>A computer object's tile, with or without a site record: the first that fits from the top.</summary>
        private static void Classify(CleanupEntry entry, DateTime cutoff)
        {
            ADComputer computer = entry.Computer;
            SiteRecord record = entry.Record;
            bool seen = computer.LastSeen.HasValue && computer.LastSeen.Value > cutoff;
            bool hasClient = record != null && record.Client;
            bool clientAlive = hasClient && record.LastContact.HasValue && record.LastContact.Value > cutoff;
            if (computer.Disabled) entry.Finding = CleanupFinding.Disabled;
            else if (!seen && !clientAlive) entry.Finding = CleanupFinding.Stale;
            else if (!seen) entry.Finding = CleanupFinding.ADUnused;
            else if (hasClient && !clientAlive) entry.Finding = CleanupFinding.ClientSilent;
            else if (!hasClient) entry.Finding = CleanupFinding.NoClient;
            else entry.Finding = CleanupFinding.OK;

            if (entry.Note == null && entry.Finding == CleanupFinding.NoClient)
            {
                entry.Note = record == null ? "The site has no record of it" : "The site's record has no client";
            }
        }

        /// <summary>A number of days the way the tiles, the status line and the log say it: "1 day", "90 days".</summary>
        internal static string Days(int days) => days.ToString("N0") + (days == 1 ? " day" : " days");

        /// <summary>The name each tile and the grid's Finding column use.</summary>
        internal static string Name(CleanupFinding finding)
        {
            switch (finding)
            {
                case CleanupFinding.OK: return "OK";
                case CleanupFinding.NotInAD: return "Not in AD";
                case CleanupFinding.Disabled: return "Disabled";
                case CleanupFinding.Stale: return "Stale";
                case CleanupFinding.ADUnused: return "AD unused";
                case CleanupFinding.ClientSilent: return "Client silent";
                case CleanupFinding.NoClient: return "No client";
                case CleanupFinding.NotChecked: return "Not checked";
                default: return finding.ToString();
            }
        }

        /// <summary>
        /// A FILETIME attribute (lastLogonTimestamp, pwdLastSet) in UTC. 0 means never, and so does the largest
        /// value, which is past what a DateTime holds.
        /// </summary>
        internal static DateTime? FileTime(object value)
        {
            if (!(value is long ticks) || ticks <= 0 || ticks >= DateTime.MaxValue.ToFileTimeUtc()) return null;
            return DateTime.FromFileTimeUtc(ticks);
        }

        /// <summary>An LDAP path for a distinguished name: a / in it would end the path's server part, so it's escaped.</summary>
        private static string AdsPath(string distinguishedName) => "LDAP://" + distinguishedName.Replace("/", "\\/");

        private static object First(SearchResult result, string property)
        {
            return result.Properties.Contains(property) && result.Properties[property].Count > 0 ? result.Properties[property][0] : null;
        }

        private static string Text(IResultObject row, string property)
        {
            string text = row[property].ObjectValue == null ? null : row[property].StringValue;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static bool Flag(IResultObject row, string property) => row[property].ObjectValue != null && row[property].IntegerValue == 1;

        /// <summary>Each read's time: at Debug, and at Info when it's slow, as the dashboard logs its steps.</summary>
        private static void LogTime(string step, TimeSpan took)
        {
            if (took >= SlowStep)
                log.InfoFormat("AD Cleanup Report: {0} in {1:0.0} seconds", step, took.TotalSeconds);
            else if (log.IsDebugEnabled)
                log.DebugFormat("AD Cleanup Report: {0} in {1} ms", step, (long)took.TotalMilliseconds);
        }
    }
}
