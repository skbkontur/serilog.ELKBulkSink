using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog.Events;
using Serilog.Sinks.PeriodicBatching;
using System.Text.RegularExpressions;
using Murmur;

namespace Serilog.ELKBulkSink
{
    public class ELKSink : PeriodicBatchingSink
    {
        private readonly string elkUrl;
        private readonly string indexTemplate;
        private readonly bool includeDiagnostics;
        private static Regex stackTraceFilterRegexp = new Regex(@"(([0-9a-fA-F\-][0-9a-fA-F\-]){2}){4,}", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public const double MaxBulkBytes = 4.5 * 1024 * 1024;
        public const int MaxTermBytes = 32 * 1024;

        public ELKSink(string elkUrl, string indexTemplate, int batchSizeLimit, TimeSpan period,
            bool includeDiagnostics = false)
            : base(batchSizeLimit, period)
        {
            this.elkUrl = elkUrl.TrimEnd('/');
            this.indexTemplate = indexTemplate;
            this.includeDiagnostics = includeDiagnostics;
        }

        protected override async Task EmitBatchAsync(IEnumerable<LogEvent> events)
        {
            foreach (var content in ChunkEvents(events))
            {
                if (content == null)
                {
                    continue;
                }
                using (var httpClient = new HttpClient())
                {
                    try
                    {
                        var url = string.Format("{0}/{1}{2}", elkUrl, indexTemplate, DateTime.UtcNow.ToString("yyyy.MM.dd"));
                        await httpClient.PostAsync(url, content);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(string.Format("Exception posting to ELK {0}", ex));
                    }
                }
            }
        }

        public IEnumerable<StringContent> ChunkEvents(IEnumerable<LogEvent> events)
        {
            if (events == null)
            {
                yield break;
            }

            var jsons = events.Select(EventToJson).Where(_ => !string.IsNullOrWhiteSpace(_)).ToList();

            var bytes = 0;
            int page = 0;
            var chunk = new List<string>();

            foreach (var json in jsons)
            {
                if (bytes > MaxBulkBytes)
                {
                    yield return PackageContent(chunk, bytes, page);
                    bytes = 0;
                    page++;
                    chunk.Clear();
                }

                bytes += Encoding.UTF8.GetByteCount(json) + 1;
                chunk.Add(json);
            }

            yield return PackageContent(chunk, bytes, page, includeDiagnostics);
        }

        public static StringContent PackageContent(List<string> jsons, int bytes, int page, bool includeDiagnostics = false)
        {
            if (includeDiagnostics)
            {
                var diagnostic = JsonConvert.SerializeObject(new
                {
                    Event = "LogglyDiagnostics",
                    Trace = string.Format("EventCount={0}, ByteCount={1}, PageCount={2}", jsons.Count, bytes, page)
                });
                jsons.Add(diagnostic);
            }
            
            return new StringContent(string.Join("\n", jsons), Encoding.UTF8, "application/json");
        }

        public static string EventToJson(LogEvent logEvent)
        {
            if (logEvent == null)
            {
                throw new ArgumentNullException("logEvent");
            }

            var payload = new Dictionary<string, object>();
            try
            {
                foreach (var key in logEvent.Properties.Keys)
                {
                    int dummy;
                    if (Int32.TryParse(key, out dummy)) continue;
                    var propertyValue = logEvent.Properties[key];
                    var simpleValue = SerilogPropertyFormatter.Simplify(propertyValue);
                    var safeKey = key.Replace(" ", "").Replace(":", "").Replace("-", "").Replace("_", "");
                    AddIfNotContains(payload, safeKey, simpleValue);
                }

                AddIfNotContains(payload, "Level", logEvent.Level.ToString());
                AddIfNotContains(payload, "@timestamp", logEvent.Timestamp);
                var message = logEvent.RenderMessage();
                var messageBytes = Encoding.UTF8.GetBytes(message);

                if (messageBytes.Length > MaxTermBytes)
                {
                    var truncated = messageBytes.Length - MaxTermBytes;
                    var ending = string.Format("[truncated {0}]", truncated);
                    var subLength = MaxTermBytes - ending.Length;
                    if (subLength > 0)
                    {
                        message = string.Format("{0}{1}", message.Substring(0, subLength), ending);
                        payload["@truncated"] = truncated;
                    }
                }

                AddIfNotContains(payload, "Message", message);

                if (logEvent.Exception != null)
                {
                    AddIfNotContains(payload, "Exception", logEvent.Exception.ToString());
                    var stackTrace = logEvent.Exception.StackTrace;
                    if (stackTrace != null)
                    {
                        stackTrace = stackTraceFilterRegexp.Replace(stackTrace, string.Empty);
                        AddIfNotContains(payload, "exc_stacktrace_hash", GetMurmur3HashString(stackTrace));
                    }
                }

                var result = JsonConvert.SerializeObject(payload,
                    new JsonSerializerSettings
                    {
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                    });
                return result;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(string.Format("Error extracting json from logEvent {0}",ex));
            }
            return null;
        }
        
        public static void AddIfNotContains<TKey, TValue>(IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key)) return;
            dictionary[key] = value;
        }

        private static string GetMurmur3HashString(string value)
        {
            return BitConverter.ToString(MurmurHash.Create128().ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }
    }
}
