using System;
using System.IO;
using log4net.Core;
using log4net.Layout.Pattern;
using log4net.Util;

namespace ConfigMgr.QuickTools.CMTrace
{
    /// <summary>Writes the CMTrace severity for a log4net level: 3 = error, 2 = warning, 1 = everything else.</summary>
    public class NumericLevelPatternConverter : PatternLayoutConverter
    {
        protected override void Convert(TextWriter writer, LoggingEvent loggingEvent)
        {
            int level = loggingEvent.Level.Value;
            writer.Write(level >= Level.Error.Value ? 3 : level >= Level.Warn.Value ? 2 : 1);
        }
    }

    /// <summary>Writes the time-zone bias CMTrace expects after the time, in minutes (for example +300 for US Central).</summary>
    public class UTCOffsetPatternConverter : PatternConverter
    {
        public override void Convert(TextWriter writer, object state)
        {
            double bias = -TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
            writer.Write(bias.ToString("+0;-0"));
        }
    }
}
