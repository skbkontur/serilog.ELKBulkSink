using System;
using Serilog.Events;

namespace Serilog.ELKBulkSink
{
    public class SinkOptions
    {
        public string Url { get; set; }
        public string IndexTemplate { get; set; }
        public bool AppendIndex { get; set; } = true;
        public string AuthKey { get; set; }
        public string AuthSchema { get; set; } = "ELK";

        public int BatchLimit { get; set; } = 100;
        public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(30);

        public LogEventLevel RestrictedToMinLevel { get; set; } = LogEventLevel.Verbose;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

        public Func<string, bool> ShouldPropertyBeIgnored { get; set; }
            = propName => propName.StartsWith("__");
    }
}
