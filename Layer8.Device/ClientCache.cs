using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using System;
using System.Management;
using System.Windows.Forms;

namespace Layer8.Device
{
    public static class ClientCache
    {
        private static readonly Layer8.Logger log = Layer8.Layer8Log.GetLogger(typeof(ClientCache));

        /// <summary>Console entry point: runs ChangeClientCacheCore behind Layer8's crash guard.</summary>
        public static void ChangeClientCache(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Client Cache", () => ChangeClientCacheCore(scopeNode, action, selectedResultObjects));
        }

        /// <summary>
        /// One device: the Client Cache window (size, location, contents). Several devices or a collection: ask for
        /// a size and set it on all of them in the progress window.
        /// </summary>
        private static void ChangeClientCacheCore(ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects)
        {
            if (selectedResultObjects.ObjectClass != "SMS_Collection" && selectedResultObjects.Count == 1)
            {
                Utility.ShowDialog("Layer8DeviceCache", selectedResultObjects);
                return;
            }

            string answer = InputPrompt.Ask("Client Cache", "Set the client cache size on every selected device, in MB (10240 is 10 GB). To empty caches instead, use Client Actions > Clear Client Cache.", "10240");
            if (answer == null) return;
            if (!uint.TryParse(answer.Trim(), out uint sizeMb) || sizeMb < 1024)
            {
                MessageBox.Show("Enter a whole number of MB, at least 1024.", "Client Cache", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ClientActions.ProcessAction(scopeNode, action, selectedResultObjects, nameof(SetCacheSize), device => SetCacheSize(device, sizeMb),
                string.Format("Client Cache: set {0:N0} MB", sizeMb), alwaysShowDialog: true,
                confirmQuestion: string.Format("Set the ConfigMgr client cache to {0:N0} MB on {{0}}?\n\nIf client settings set a cache size, the client goes back to that at its next policy evaluation.", sizeMb));
        }

        /// <summary>Sets the client cache size (CacheConfig.Size, in MB) on one device.</summary>
        public static string SetCacheSize(IResultObject resultObject, uint sizeMb)
        {
            string host = resultObject["Name"].StringValue;
            ManagementScope scope = Utility.GetWMIScope(host, @"ccm\softmgmtagent");
            using (ManagementObject config = Utility.GetFirstWMIInstance(scope, "SELECT * FROM CacheConfig WHERE ConfigKey='Cache'"))
            {
                if (config == null) throw new InvalidOperationException("No client cache settings on this device");
                uint before = Convert.ToUInt32(config["Size"]);
                config["Size"] = sizeMb;
                config.Put(new PutOptions { Type = PutType.UpdateOnly });
                log.InfoFormat("Client cache on {0}: {1} MB (was {2} MB)", host, sizeMb, before);
                return string.Format("Cache {0:N0} MB (was {1:N0} MB)", sizeMb, before);
            }
        }
    }
}
