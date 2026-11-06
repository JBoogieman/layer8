using Microsoft.ConfigurationManagement.AdminConsole;
using Microsoft.ConfigurationManagement.ManagementProvider;
using Microsoft.ConfigurationManagement.AdminConsole.Schema;
using System.Windows.Forms;

namespace Layer8.Device
{
    public static class ClientCache
    {
        /// <summary>Console entry point: runs ChangeClientCacheCore behind Layer8's crash guard.</summary>
        public static void ChangeClientCache(object sender, ScopeNode scopeNode, ActionDescription action, IResultObject selectedResultObjects, PropertyDataUpdated dataUpdatedDelegate, Status status)
        {
            SafeAction.Run(action?.DisplayName ?? "Client Cache", () => ChangeClientCacheCore(selectedResultObjects));
        }

        private static void ChangeClientCacheCore(IResultObject selectedResultObjects)
        {
            // The window shows one client's cache and its contents; several devices need a different tool.
            if (selectedResultObjects.ObjectClass == "SMS_Collection" || selectedResultObjects.Count > 1)
            {
                MessageBox.Show("Client Cache opens for one device at a time. To empty the cache on many devices, use Client Actions > Clear Client Cache.",
                    "Client Cache", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Utility.ShowDialog("Layer8DeviceCache", selectedResultObjects);
        }
    }
}
