using System.Windows.Forms;

namespace ConfigMgr.QuickTools
{
    /// <summary>
    /// Update checks are turned off in Layer8.
    /// The original Quick Tools updater used AutoUpdater.NET (which has a known critical
    /// vulnerability) and pulled its update feed from the upstream author's GitHub repo.
    /// The public methods stay so existing callers still compile; they no longer reach out anywhere.
    /// </summary>
    public class Updater
    {
        public static void CheckUpdates()
        {
            // Intentionally empty: Layer8 does not check for updates automatically.
        }

        public static void CheckUpdatesNow()
        {
            MessageBox.Show(
                "Automatic update checks are turned off in Layer8. Get new versions from the Layer8 releases on GitHub.",
                "Layer8",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
