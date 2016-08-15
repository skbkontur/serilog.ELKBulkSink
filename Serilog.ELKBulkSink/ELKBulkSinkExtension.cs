using System;
using Serilog.Configuration;

namespace Serilog.ELKBulkSink
{
    public static class ELKBulkSinkExtension
    {
        public static LoggerConfiguration ELKBulk(this LoggerSinkConfiguration lc, SinkOptions options)
        {
            if (lc == null)
                throw new ArgumentNullException(nameof(lc));
            return lc.Sink(new ELKSink(options), options.RestrictedToMinLevel);
        }
    }
}
