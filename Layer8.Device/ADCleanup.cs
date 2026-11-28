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
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Layer8.Device
{
    /// <summary>Which tile a computer object or a site record is on, in the tiles' order.</summary>
    internal enum CleanupFinding { OK, NotInAD, Disabled, Stale, ADUnused, ClientSilent, NoClient, NotChecked }

    /// <summary>The changes the report can make in AD, in the Fix menu's order.</summary>
    internal enum CleanupFix { Disable, Enable, Move, Delete }

    /// <summary>Why a fix leaves a computer object alone. <see cref="ADCleanup.SkipText"/> says each one with its count.</summary>
    internal enum CleanupSkip { NoSid, DomainController, ThisComputer, SiteSystem, Gone, Changed, NotAllowed, Protected, BitLockerKeys, AlreadyThere }

    /// <summary>One computer object, with only the attributes <see cref="ADCleanup.Attributes"/> loads. Times in UTC.</summary>
    internal sealed class ADComputer
    {
        public string Name;
        public string AccountName;
        public string DistinguishedName;
        public string Sid;
        /// <summary>userAccountControl: the fixes keep every flag but the one they change, and spot domain controllers by it.</summary>
        public int Flags;
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
        /// <summary>The domain controller that answered. The window's later reads and every fix go to it too.</summary>
        public string Server;
        /// <summary>The domain's NetBIOS name, the name the site's records carry.</summary>
        public string Domain;
        /// <summary>The domain's distinguished name, where the search started.</summary>
        public string DomainPath;
        public List<ADComputer> Computers;
        public List<SiteRecord> Records;
        /// <summary>Records left out as obsolete: a newer record of the same device replaced each one.</summary>
        public int Obsolete;
        /// <summary>The short names of the site's own servers, which no fix touches.</summary>
        public HashSet<string> SiteSystems;
        public TimeSpan ADTime, SiteTime;
    }

    /// <summary>One computer object as a fix reads it again, just before asking and again just before changing it.</summary>
    internal sealed class ADObjectState
    {
        /// <summary>Null when it's gone from AD.</summary>
        public ADComputer Computer;
        /// <summary>Protected from accidental deletion: Everyone is denied Delete or Delete subtree on it.</summary>
        public bool Protected;
        /// <summary>The objects under it, BitLocker recovery objects included. Counted only when a delete needs to know.</summary>
        public int Children;
        public int BitLockerKeys;
        /// <summary>This account may write userAccountControl on it (AD's own answer, allowedAttributesEffective).</summary>
        public bool MayWriteFlags;
    }

    /// <summary>What AD keeps of a deleted object, and for how long.</summary>
    internal sealed class DeletedObjects
    {
        /// <summary>The AD Recycle Bin is on: a deleted object can be restored whole.</summary>
        public bool RecycleBin;
        /// <summary>The deleted-object lifetime with the Recycle Bin on; the tombstone lifetime with it off.</summary>
        public int Days;
    }

    /// <summary>An OU, or the domain's computers container, that a Move can put computer objects in.</summary>
    internal sealed class ADContainer
    {
        public string DistinguishedName;
        /// <summary>Its path from the top of the domain, as ADUC's tree shows it: "Layer8 Test / Disabled".</summary>
        public string Path;
    }

    /// <summary>
    /// AD Cleanup Report: the domain's computer objects beside the site's device records, each on a tile for what
    /// doesn't add up (not in AD, disabled, stale, AD unused while the client reports, a silent client, no client).
    /// The report reads: one search of AD and three of the site, and no device is contacted. Only the Fix button
    /// changes anything, and only in AD: Disable, Enable, Move and Delete, on the Stale and Disabled tiles, after a
    /// question (the fixes' half is below Classify).
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

        // userAccountControl's ACCOUNTDISABLE flag, and the two a domain controller has: SERVER_TRUST_ACCOUNT, and
        // PARTIAL_SECRETS_ACCOUNT on a read-only one (DC01's flags are 0x82000).
        private const int AccountDisabled = 0x2;
        private const int ServerTrustAccount = 0x2000;
        private const int PartialSecretsAccount = 0x4000000;
        private static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The most computer objects one Delete removes: the Health Dashboard's cap on a restart from a tile, the
        /// other change Layer8 can't take back. Disable, Enable and Move have no cap; more than one means typing the count.
        /// </summary>
        internal const int DeleteCap = 50;
        /// <summary>The names a question lists before "and N more".</summary>
        internal const int NamesShown = 10;

        /// <summary>
        /// What a fix reads again about each computer object: the report's attributes, whether this account may
        /// write userAccountControl (AD works that out from the permissions), and the permissions themselves, the DACL
        /// only, for protection from accidental deletion. Still nothing secret. The regression script checks this list.
        /// </summary>
        internal static readonly ReadOnlyCollection<string> StateAttributes = Array.AsReadOnly(Attributes.Concat(new[] { "allowedAttributesEffective", "nTSecurityDescriptor" }).ToArray());

        // COM error codes AD returns: access denied (as Windows and as the directory say it), no such object, an object
        // already there with that name, and the domain controller not answering.
        private const int AccessDenied = unchecked((int)0x80070005);
        private const int InsufficientRights = unchecked((int)0x80072098);
        private const int NoSuchObject = unchecked((int)0x80072030);
        private const int AlreadyExists = unchecked((int)0x80071392);
        private const int NameExists = unchecked((int)0x80072071);
        private const int ServerDown = unchecked((int)0x8007203A);
        private const int RpcUnavailable = unchecked((int)0x800706BA);

        // Well-known containers every domain has, found by GUID wherever they've been moved to (redircmp moves the first).
        private const string ComputersContainer = "AA312825768811D1ADED00C04FD8D5CD";
        private const string DomainControllersContainer = "A361B2FFFFD211D1AA4B00C04FD7D83A";

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

        /// <summary>
        /// Reads AD, then the site. Runs on a background thread; stops between results when canceled. With
        /// <paramref name="server"/> null, the first domain controller to answer is the one; give it again on a refresh,
        /// so a change just made shows straight away. With several AD sites, another domain controller could show the
        /// old state until replication catches up: between sites that's every 180 minutes by default.
        /// </summary>
        internal static CleanupRead Read(ConnectionManagerBase connection, string server, CancellationToken token)
        {
            CleanupRead read = new CleanupRead { Server = server };
            Stopwatch watch = Stopwatch.StartNew();
            read.Computers = ReadComputers(ref read.Server, token, out read.Domain, out read.DomainPath);
            read.ADTime = watch.Elapsed;
            LogTime(string.Format("read {0:N0} computer objects from {1} on {2}", read.Computers.Count, read.Domain, read.Server), read.ADTime);

            watch.Restart();
            read.Records = ReadRecords(connection, token, out read.Obsolete);
            read.SiteSystems = ReadSiteSystems(connection);
            read.SiteTime = watch.Elapsed;
            LogTime(string.Format("read {0:N0} device records from the site ({1:N0} obsolete left out) and {2:N0} site systems",
                                  read.Records.Count, read.Obsolete, read.SiteSystems.Count), read.SiteTime);
            return read;
        }

        /// <summary>
        /// Every computer object in the console user's domain, a thousand a page, with only <see cref="Attributes"/>;
        /// and the domain's NetBIOS name and distinguished name. All from <paramref name="server"/>, or when that's
        /// null from the first domain controller to answer, whose name comes back in it.
        /// </summary>
        internal static List<ADComputer> ReadComputers(ref string server, CancellationToken token, out string domain, out string domainPath)
        {
            string configuration;
            using (DirectoryEntry rootDse = new DirectoryEntry(string.IsNullOrEmpty(server) ? "LDAP://RootDSE" : "LDAP://" + server + "/RootDSE"))
            {
                domainPath = rootDse.Properties["defaultNamingContext"].Value as string;
                configuration = rootDse.Properties["configurationNamingContext"].Value as string;
                if (string.IsNullOrEmpty(server)) server = rootDse.Properties["dnsHostName"].Value as string;
            }
            if (string.IsNullOrEmpty(domainPath) || string.IsNullOrEmpty(configuration))
            {
                throw new InvalidOperationException("Active Directory didn't say which domain this is: its RootDSE has no defaultNamingContext.");
            }
            if (string.IsNullOrEmpty(server)) throw new InvalidOperationException("Active Directory didn't say which domain controller answered: its RootDSE has no dnsHostName.");
            domain = NetBiosName(server, configuration, domainPath);

            List<ADComputer> computers = new List<ADComputer>();
            using (DirectoryEntry root = new DirectoryEntry(AdsPath(server, domainPath)))
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
                        computers.Add(ToComputer(result));
                    }
                }
            }
            return computers;
        }

        /// <summary>A computer object from a search that loaded <see cref="Attributes"/>: the report's read, and a fix's.</summary>
        private static ADComputer ToComputer(SearchResult result)
        {
            int flags = First(result, "userAccountControl") is int value ? value : 0;
            return new ADComputer
            {
                Name = First(result, "name") as string,
                AccountName = First(result, "sAMAccountName") as string,
                DistinguishedName = First(result, "distinguishedName") as string,
                Sid = First(result, "objectSid") is byte[] sid ? new SecurityIdentifier(sid, 0).Value : null,
                Flags = flags,
                Disabled = (flags & AccountDisabled) != 0,
                LastLogon = FileTime(First(result, "lastLogonTimestamp")),
                PasswordSet = FileTime(First(result, "pwdLastSet")),
                Created = First(result, "whenCreated") is DateTime created ? DateTime.SpecifyKind(created, DateTimeKind.Utc) : (DateTime?)null,
                OperatingSystem = First(result, "operatingSystem") as string,
            };
        }

        /// <summary>The domain's NetBIOS name, from its entry in the forest's Partitions container.</summary>
        private static string NetBiosName(string server, string configuration, string domainPath)
        {
            using (DirectoryEntry partitions = new DirectoryEntry(AdsPath(server, "CN=Partitions," + configuration)))
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
        /// The short names of every server with a site system role (the site server, its database, management and
        /// distribution points...). No fix touches them: a disabled site server stops the site, and one that looks
        /// stale is a role to remove in the console first.
        /// </summary>
        internal static HashSet<string> ReadSiteSystems(ConnectionManagerBase connection)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (IResultObject results = connection.QueryProcessor.ExecuteQuery("SELECT ServerName FROM SMS_SystemResourceList"))
            {
                foreach (IResultObject role in results)
                {
                    string server = Text(role, "ServerName");
                    if (server != null) names.Add(ShortName(server));
                }
            }
            return names;
        }

        /// <summary>"CM01" from "CM01.lab.internal" or "\\CM01".</summary>
        private static string ShortName(string host)
        {
            string name = host.Trim().TrimStart('\\');
            int dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        /// <summary>The computer's account name without its $: what the site and the network call it.</summary>
        private static string AccountShortName(ADComputer computer)
        {
            string account = computer.AccountName;
            return account != null && account.EndsWith("$", StringComparison.Ordinal) ? account.Substring(0, account.Length - 1) : account;
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
                string account = AccountShortName(computer);
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

        // --- The fixes ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The fixes a tile offers: only Stale (neither AD nor a client has seen it in the days set) and Disabled. AD
        /// unused has a client still reporting, so the device is alive: disabling it would cut it off the domain, and
        /// its fix is the trust relationship, on the device. Client silent and No client are seen by AD, so they aren't
        /// AD's problem, and Not in AD has nothing in AD to change.
        /// </summary>
        internal static CleanupFix[] FixesFor(CleanupFinding finding)
        {
            switch (finding)
            {
                case CleanupFinding.Stale: return new[] { CleanupFix.Disable, CleanupFix.Move, CleanupFix.Delete };
                case CleanupFinding.Disabled: return new[] { CleanupFix.Enable, CleanupFix.Move, CleanupFix.Delete };
                default: return new CleanupFix[0];
            }
        }

        /// <summary>The Fix menu's name for a fix.</summary>
        internal static string Name(CleanupFix fix) => fix == CleanupFix.Move ? "Move to OU" : fix.ToString();

        internal static string Verb(CleanupFix fix) => fix.ToString().ToLowerInvariant();

        internal static string Done(CleanupFix fix) => fix == CleanupFix.Move ? "moved" : Verb(fix) + "d";

        /// <summary>
        /// Why no fix touches this computer object, whatever its tile, or null when one may. Left alone: one with no
        /// SID to find it by again; a domain controller or a read-only one, by its flags or by being in the Domain
        /// Controllers OU (which also holds Entra Kerberos's AzureADKerberos object); the console's own computer; and
        /// any server with a site system role.
        /// </summary>
        internal static CleanupSkip? Guard(ADComputer computer, string domainPath, ICollection<string> siteSystems)
        {
            if (string.IsNullOrEmpty(computer.Sid)) return CleanupSkip.NoSid;
            if ((computer.Flags & (ServerTrustAccount | PartialSecretsAccount)) != 0) return CleanupSkip.DomainController;
            if (computer.DistinguishedName != null && computer.DistinguishedName.EndsWith(",OU=Domain Controllers," + domainPath, StringComparison.OrdinalIgnoreCase)) return CleanupSkip.DomainController;
            string account = AccountShortName(computer);
            if (Utility.IsThisComputer(computer.Name) || Utility.IsThisComputer(account)) return CleanupSkip.ThisComputer;
            if (siteSystems != null && ((computer.Name != null && siteSystems.Contains(computer.Name)) || (account != null && siteSystems.Contains(account)))) return CleanupSkip.SiteSystem;
            return null;
        }

        /// <summary>
        /// Why <paramref name="fix"/> leaves a computer object alone now that it's been read again, or null when it goes
        /// ahead. Its tile is worked out again from the fresh read and the report's site record, so a computer that's
        /// signed in, or been changed in ADUC, since the report read it isn't touched.
        /// </summary>
        internal static CleanupSkip? Check(CleanupFix fix, CleanupEntry entry, ADObjectState state, string target, string domainPath,
                                           ICollection<string> siteSystems, int days, DateTime nowUtc)
        {
            if (state.Computer == null) return CleanupSkip.Gone;
            CleanupSkip? guard = Guard(state.Computer, domainPath, siteSystems);
            if (guard != null) return guard;
            CleanupEntry again = new CleanupEntry { Computer = state.Computer, Record = entry.Record, Note = string.Empty };
            Classify(again, nowUtc.AddDays(-days));
            if (again.Finding != entry.Finding || Array.IndexOf(FixesFor(entry.Finding), fix) < 0) return CleanupSkip.Changed;
            switch (fix)
            {
                case CleanupFix.Disable:
                case CleanupFix.Enable:
                    if (!state.MayWriteFlags) return CleanupSkip.NotAllowed;
                    break;
                case CleanupFix.Move:
                    if (state.Protected) return CleanupSkip.Protected;
                    if (string.Equals(ParentOf(state.Computer.DistinguishedName), target, StringComparison.OrdinalIgnoreCase)) return CleanupSkip.AlreadyThere;
                    break;
                case CleanupFix.Delete:
                    if (state.Protected) return CleanupSkip.Protected;
                    // A stale laptop in a drawer may still need its key, and with the Recycle Bin off a delete loses it.
                    if (state.BitLockerKeys > 0) return CleanupSkip.BitLockerKeys;
                    break;
            }
            return null;
        }

        /// <summary>A reason with how many it left out, the way the questions and the log list them: "2 domain controllers".</summary>
        internal static string SkipText(CleanupSkip skip, int count)
        {
            string n = count.ToString("N0") + " ";
            bool one = count == 1;
            switch (skip)
            {
                case CleanupSkip.NoSid: return n + "with no SID to find " + (one ? "it" : "them") + " by";
                case CleanupSkip.DomainController: return n + (one ? "domain controller" : "domain controllers");
                case CleanupSkip.ThisComputer: return n + "this computer (the console's)";
                case CleanupSkip.SiteSystem: return n + "with a site system role";
                case CleanupSkip.Gone: return n + "gone from AD";
                case CleanupSkip.Changed: return n + "changed since the report read " + (one ? "it" : "them");
                case CleanupSkip.NotAllowed: return n + "this account isn't allowed to change";
                case CleanupSkip.Protected: return n + "protected from accidental deletion";
                case CleanupSkip.BitLockerKeys: return n + "with BitLocker recovery keys in AD";
                case CleanupSkip.AlreadyThere: return n + "already in that OU";
                default: return n + skip;
            }
        }

        /// <summary>Every reason with its count, in the reasons' order; null when nothing was left out.</summary>
        internal static string LeftOut(IDictionary<CleanupSkip, int> counts)
        {
            List<string> parts = counts.Where(pair => pair.Value > 0).OrderBy(pair => pair.Key).Select(pair => SkipText(pair.Key, pair.Value)).ToList();
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        /// <summary>Delete always asks for the count to be typed, even for one; the others do for more than one.</summary>
        internal static bool CountTyped(CleanupFix fix, int count) => fix == CleanupFix.Delete || count > 1;

        /// <summary>
        /// What a fix asks before it changes anything: what it does to which computer objects, what's left out and
        /// why, how it's undone (or that it can't be), and which domain controller and account make the change.
        /// </summary>
        internal static string Question(CleanupFix fix, string tile, string domain, string domainPath, IList<ADComputer> computers, string leftOut,
                                        string target, DeletedObjects deleted, string server, string user)
        {
            int count = computers.Count;
            string subject = count == 1 ? computers[0].Name : count.ToString("N0") + " computer objects";
            List<string> paragraphs = new List<string>();
            switch (fix)
            {
                case CleanupFix.Disable: paragraphs.Add(string.Format("Disable {0} in {1} (tile {2})?", subject, domain, tile)); break;
                case CleanupFix.Enable: paragraphs.Add(string.Format("Enable {0} in {1} (tile {2})?", subject, domain, tile)); break;
                case CleanupFix.Move: paragraphs.Add(string.Format("Move {0} to {1}?\r\n{2}", subject, DisplayPath(target, domainPath), target)); break;
                case CleanupFix.Delete: paragraphs.Add(string.Format("Delete {0} from {1} for good?", subject, domain)); break;
            }

            List<string> lines = new List<string>();
            if (count > 1) lines.Add(Names(computers));
            if (fix == CleanupFix.Move || fix == CleanupFix.Delete) lines.Add("From: " + From(computers, domainPath));
            if (leftOut != null) lines.Add("Left out: " + leftOut + ".");
            if (lines.Count > 0) paragraphs.Add(string.Join("\r\n", lines));

            switch (fix)
            {
                case CleanupFix.Disable:
                    paragraphs.Add("A disabled computer can't sign in to the domain. Enable, on the Disabled tile, undoes it.");
                    break;
                case CleanupFix.Enable:
                    paragraphs.Add("An enabled computer can sign in to the domain again. Disable undoes it.");
                    break;
                case CleanupFix.Move:
                    paragraphs.Add("Group Policy linked to the new OU applies at their next refresh, and permissions delegated on the old one stop applying. Moving them back undoes it: Layer8's log keeps where each one was.");
                    break;
                case CleanupFix.Delete:
                    paragraphs.Add("Each one goes with everything under it, its LAPS password and its group memberships, and its SID is gone for good.\r\n" +
                        (deleted != null && deleted.RecycleBin
                            ? string.Format("The AD Recycle Bin is on: AD can restore them whole for {0} (Restore-ADObject). Layer8 can't undo this.", Days(deleted.Days))
                            : string.Format("The AD Recycle Bin is off in {0}: Layer8 can't undo this, and AD keeps only a tombstone, stripped of most attributes, for {1}.",
                                            domain, Days(deleted?.Days ?? 60))));
                    break;
            }
            paragraphs.Add(string.Format("Writes to {0} as {1}.", server, user));
            return string.Join("\r\n\r\n", paragraphs);
        }

        /// <summary>The first <see cref="NamesShown"/> names, then how many more.</summary>
        internal static string Names(IList<ADComputer> computers)
        {
            string names = string.Join(", ", computers.Take(NamesShown).Select(computer => computer.Name));
            return computers.Count > NamesShown ? string.Format("{0} and {1:N0} more", names, computers.Count - NamesShown) : names;
        }

        /// <summary>Where they are now, most first: "Computers (10), Layer8 Test (2)".</summary>
        internal static string From(IList<ADComputer> computers, string domainPath)
        {
            return string.Join(", ", computers
                .GroupBy(computer => DisplayPath(ParentOf(computer.DistinguishedName) ?? string.Empty, domainPath), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => string.Format("{0} ({1:N0})", group.Key, group.Count())));
        }

        /// <summary>
        /// Reads one computer object again by its SID, from <paramref name="server"/>: still there, its attributes,
        /// protection from accidental deletion, and whether this account may write userAccountControl on it. With
        /// <paramref name="countChildren"/>, also what's under it, by object class only: a BitLocker recovery object is
        /// counted, never read, so its password isn't loaded.
        /// </summary>
        internal static ADObjectState ReadState(string server, string sid, bool countChildren)
        {
            ADObjectState state = new ADObjectState();
            using (DirectoryEntry entry = new DirectoryEntry("LDAP://" + server + "/<SID=" + sid + ">"))
            {
                SearchResult result;
                using (DirectorySearcher search = new DirectorySearcher(entry, "(objectClass=computer)", StateAttributes.ToArray(), SearchScope.Base))
                {
                    // The DACL only: the owner isn't needed, and the audit settings need a privilege.
                    search.SecurityMasks = SecurityMasks.Dacl;
                    try
                    {
                        result = search.FindOne();
                    }
                    catch (COMException ex) when (ex.ErrorCode == NoSuchObject)
                    {
                        return state;
                    }
                }
                if (result == null) return state;
                state.Computer = ToComputer(result);
                state.MayWriteFlags = Contains(result, "allowedAttributesEffective", "userAccountControl");
                state.Protected = First(result, "nTSecurityDescriptor") is byte[] descriptor && DeniesDelete(descriptor);
                if (countChildren)
                {
                    using (DirectorySearcher children = new DirectorySearcher(entry, "(objectClass=*)", new[] { "objectClass" }, SearchScope.OneLevel))
                    using (SearchResultCollection found = children.FindAll())
                    {
                        foreach (SearchResult child in found)
                        {
                            state.Children++;
                            if (Contains(child, "objectClass", "msFVE-RecoveryInformation")) state.BitLockerKeys++;
                        }
                    }
                }
            }
            return state;
        }

        /// <summary>
        /// Protected from accidental deletion: ADUC's box, and Set-ADObject's switch, put an explicit Deny for Everyone
        /// on Delete and Delete subtree. A move needs Delete too, so both are refused while it's set. Layer8 never
        /// changes it: those are the object's permissions.
        /// </summary>
        internal static bool DeniesDelete(byte[] descriptor)
        {
            ActiveDirectorySecurity security = new ActiveDirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            foreach (ActiveDirectoryAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny && everyone.Equals(rule.IdentityReference) &&
                    (rule.ActiveDirectoryRights & (ActiveDirectoryRights.Delete | ActiveDirectoryRights.DeleteTree)) != 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether the AD Recycle Bin is on (once on, it can't be turned off), and for how many days a deleted object can
        /// come back: whole within the deleted-object lifetime with it on; with it off, only as a tombstone stripped of
        /// most attributes, within the tombstone lifetime. Unset, the tombstone lifetime is 60 days (forests from before
        /// Windows Server 2003 SP1; newer ones set 180, as LAB's is), and the deleted-object lifetime follows it.
        /// </summary>
        internal static DeletedObjects ReadDeletedObjects(string server)
        {
            string configuration;
            using (DirectoryEntry rootDse = new DirectoryEntry("LDAP://" + server + "/RootDSE"))
            {
                configuration = rootDse.Properties["configurationNamingContext"].Value as string;
            }
            string service = "CN=Directory Service,CN=Windows NT,CN=Services," + configuration;
            SearchResult feature = BaseRead(server, "CN=Recycle Bin Feature,CN=Optional Features," + service, "msDS-EnabledFeatureBL");
            bool recycleBin = feature != null && feature.Properties.Contains("msDS-EnabledFeatureBL") && feature.Properties["msDS-EnabledFeatureBL"].Count > 0;
            SearchResult lifetimes = BaseRead(server, service, "tombstoneLifetime", "msDS-deletedObjectLifetime");
            int tombstone = First(lifetimes, "tombstoneLifetime") is int set && set > 0 ? set : 60;
            int deleted = First(lifetimes, "msDS-deletedObjectLifetime") is int kept && kept > 0 ? kept : tombstone;
            return new DeletedObjects { RecycleBin = recycleBin, Days = recycleBin ? deleted : tombstone };
        }

        /// <summary>
        /// Every OU a Move can use, and the domain's computers container (CN=Computers unless redircmp moved it), which
        /// isn't an OU but is where a computer goes back to. The Domain Controllers OU and everything under it are left
        /// out: a computer put there isn't a domain controller, but gets their policy.
        /// </summary>
        internal static List<ADContainer> ReadContainers(string server, string domainPath, CancellationToken token)
        {
            string computers = WellKnownPath(server, ComputersContainer, domainPath);
            string controllers = WellKnownPath(server, DomainControllersContainer, domainPath);
            List<string> paths = new List<string>();
            if (computers != null) paths.Add(computers);
            using (DirectoryEntry root = new DirectoryEntry(AdsPath(server, domainPath)))
            using (DirectorySearcher search = new DirectorySearcher(root, "(objectClass=organizationalUnit)", new[] { "distinguishedName" }, SearchScope.Subtree))
            {
                search.PageSize = 1000;
                search.CacheResults = false;
                using (SearchResultCollection results = search.FindAll())
                {
                    foreach (SearchResult result in results)
                    {
                        token.ThrowIfCancellationRequested();
                        if (First(result, "distinguishedName") is string path) paths.Add(path);
                    }
                }
            }
            return paths
                .Where(path => controllers == null || !(string.Equals(path, controllers, StringComparison.OrdinalIgnoreCase) || path.EndsWith("," + controllers, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new ADContainer { DistinguishedName = path, Path = DisplayPath(path, domainPath) })
                .OrderBy(container => container.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>A well-known container's distinguished name, found by its GUID; null when the domain doesn't say.</summary>
        private static string WellKnownPath(string server, string guid, string domainPath)
        {
            return First(BaseRead(server, "<WKGUID=" + guid + "," + domainPath + ">", "distinguishedName"), "distinguishedName") as string;
        }

        /// <summary>Whether this account may create computer objects in a container: AD's own answer, allowedChildClassesEffective.</summary>
        internal static bool MayCreateComputers(string server, string container)
        {
            return Contains(BaseRead(server, container, "allowedChildClassesEffective"), "allowedChildClassesEffective", "computer");
        }

        /// <summary>
        /// Makes one fix on one computer object, through <paramref name="server"/>, by the name the fresh read gave it
        /// a moment ago. Disable and Enable change the one flag in userAccountControl and keep the rest. Move changes
        /// its parent, and nothing else. Delete removes it, with a tree delete when something's under it (that needs
        /// Delete subtree) and a plain one when nothing is (that needs only Delete). Throws when AD refuses; Explain
        /// says why in words.
        /// </summary>
        internal static void Apply(CleanupFix fix, string server, ADObjectState state, string target)
        {
            ADComputer computer = state.Computer;
            using (DirectoryEntry entry = new DirectoryEntry(AdsPath(server, computer.DistinguishedName)))
            {
                switch (fix)
                {
                    case CleanupFix.Disable:
                    case CleanupFix.Enable:
                        entry.Properties["userAccountControl"].Value = fix == CleanupFix.Disable ? computer.Flags | AccountDisabled : computer.Flags & ~AccountDisabled;
                        entry.CommitChanges();
                        break;
                    case CleanupFix.Move:
                        using (DirectoryEntry parent = new DirectoryEntry(AdsPath(server, target)))
                        {
                            entry.MoveTo(parent);
                        }
                        break;
                    case CleanupFix.Delete:
                        if (state.Children > 0)
                        {
                            entry.DeleteTree();
                        }
                        else
                        {
                            using (DirectoryEntry parent = new DirectoryEntry(AdsPath(server, ParentOf(computer.DistinguishedName))))
                            {
                                parent.Children.Remove(entry);
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>Why AD refused a fix, in words for the summary and the log.</summary>
        internal static string Explain(Exception failure, CleanupFix fix)
        {
            int code = (failure as COMException)?.ErrorCode ?? 0;
            if (failure is UnauthorizedAccessException || code == AccessDenied || code == InsufficientRights) return "this account isn't allowed to " + Verb(fix) + " it";
            if (code == NoSuchObject) return "it's gone from AD";
            if (code == AlreadyExists || code == NameExists) return "that OU already has an object with its name";
            if (IsUnreachable(failure)) return "the domain controller didn't answer";
            string message = failure.Message?.Trim();
            return string.IsNullOrEmpty(message) ? failure.GetType().Name : message;
        }

        /// <summary>The domain controller stopped answering: the rest of a run would fail the same way, so it stops.</summary>
        internal static bool IsUnreachable(Exception failure)
        {
            int code = (failure as COMException)?.ErrorCode ?? 0;
            return code == ServerDown || code == RpcUnavailable;
        }

        /// <summary>The distinguished name one up: everything after the first comma that isn't escaped. Null at the top.</summary>
        internal static string ParentOf(string distinguishedName)
        {
            if (distinguishedName == null) return null;
            for (int i = 0; i < distinguishedName.Length; i++)
            {
                if (distinguishedName[i] == '\\') i++;
                else if (distinguishedName[i] == ',') return distinguishedName.Substring(i + 1);
            }
            return null;
        }

        /// <summary>
        /// "Layer8 Test / Disabled" for OU=Disabled,OU=Layer8 Test,DC=lab,DC=internal: the names from the top of the
        /// domain down, as ADUC's tree shows them, with the escapes a distinguished name needs (\, and \2C) undone.
        /// </summary>
        internal static string DisplayPath(string distinguishedName, string domainPath)
        {
            string rest = distinguishedName ?? string.Empty;
            if (string.Equals(rest, domainPath, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (rest.EndsWith("," + domainPath, StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(0, rest.Length - domainPath.Length - 1);
            List<string> names = new List<string>();
            int start = 0;
            for (int i = 0; i <= rest.Length; i++)
            {
                if (i < rest.Length && rest[i] == '\\') { i++; continue; }
                if (i < rest.Length && rest[i] != ',') continue;
                string part = rest.Substring(start, i - start);
                int equals = part.IndexOf('=');
                names.Add(Unescape(equals >= 0 ? part.Substring(equals + 1) : part));
                start = i + 1;
            }
            names.Reverse();
            return string.Join(" / ", names);
        }

        private static string Unescape(string value)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    text.Append(value[i]);
                }
                else if (i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
                {
                    text.Append((char)Convert.ToInt32(value.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                {
                    text.Append(value[++i]);
                }
            }
            return text.ToString();
        }

        /// <summary>One object's attributes, from one domain controller; null when it isn't there.</summary>
        private static SearchResult BaseRead(string server, string distinguishedName, params string[] attributes)
        {
            using (DirectoryEntry entry = new DirectoryEntry(AdsPath(server, distinguishedName)))
            using (DirectorySearcher search = new DirectorySearcher(entry, "(objectClass=*)", attributes, SearchScope.Base))
            {
                try
                {
                    return search.FindOne();
                }
                catch (COMException ex) when (ex.ErrorCode == NoSuchObject)
                {
                    return null;
                }
            }
        }

        /// <summary>A multi-valued attribute holds a value, ignoring case: an attribute or class name in a list of them.</summary>
        private static bool Contains(SearchResult result, string property, string value)
        {
            return result != null && result.Properties.Contains(property) &&
                result.Properties[property].Cast<object>().Any(each => string.Equals(each as string, value, StringComparison.OrdinalIgnoreCase));
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

        /// <summary>
        /// An LDAP path to a distinguished name on one domain controller: a / in the name would end the path's server
        /// part, so it's escaped.
        /// </summary>
        private static string AdsPath(string server, string distinguishedName) => "LDAP://" + server + "/" + distinguishedName.Replace("/", "\\/");

        private static object First(SearchResult result, string property)
        {
            return result != null && result.Properties.Contains(property) && result.Properties[property].Count > 0 ? result.Properties[property][0] : null;
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
