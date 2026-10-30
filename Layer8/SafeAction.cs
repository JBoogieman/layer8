using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using log4net;

namespace Layer8
{
    /// <summary>
    /// Keeps a failing Layer8 action from taking the whole console down.
    /// The console calls Layer8's menu actions directly and doesn't protect itself, so an error that
    /// escapes one of them can crash the console. Every entry point runs through here instead: errors
    /// are logged with the full exception and shown to the user as a short message.
    /// </summary>
    public static class SafeAction
    {
        private static readonly ILog Log = Layer8Log.GetLogger(typeof(SafeAction));

        public static void Run(string actionName, Action body)
        {
            try
            {
                Log.InfoFormat("{0}: started", actionName);
                body();
            }
            catch (Exception ex)
            {
                Report(actionName, ex);
            }
        }

        /// <summary>Strips the reflection and task wrappers so the message shows the real error.</summary>
        public static Exception Unwrap(Exception ex)
        {
            while (true)
            {
                if (ex is TargetInvocationException && ex.InnerException != null)
                {
                    ex = ex.InnerException;
                    continue;
                }
                if (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                {
                    ex = aggregate.InnerExceptions[0];
                    continue;
                }
                return ex;
            }
        }

        public static void Report(string actionName, Exception ex, IWin32Window owner = null)
        {
            Exception root = Unwrap(ex);
            Log.Error(actionName + " failed", root);
            try
            {
                MessageBox.Show(
                    owner,
                    string.Format("{0} failed.{1}{1}{2}{1}{1}Details are in {3}.", actionName, Environment.NewLine, root.Message, Path.Combine(Layer8Log.LogFolder, "Layer8.log")),
                    "Layer8",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch (Exception)
            {
                // Never let reporting the error become the crash.
            }
        }
    }
}
