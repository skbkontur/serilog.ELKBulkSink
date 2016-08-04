using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
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
        private readonly SinkOptions options;
        private readonly bool includeDiagnostics;

        private static readonly Regex StackTraceFilterRegexp = new Regex(@"(([0-9a-fA-F\-][0-9a-fA-F\-]){2}){4,}",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        public const double MAX_BULK_BYTES = 4.5 * 1024 * 1024;
        public const int MAX_TERM_BYTES = 32 * 1024;

        public ELKSink(SinkOptions options, bool includeDiagnostics = false)
            : base(options.BatchLimit, options.Period)
        {
            options.Url = options.Url.TrimEnd('/');
            this.options = options;
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
                try
                {
                    var webRequest = CreateWebRequest();
                    using (var requestStream = webRequest.GetRequestStream())
                    {
                        await content.CopyToAsync(requestStream);
                    }
                    using (var response = (HttpWebResponse) await webRequest.GetResponseAsync())
                    {
                        if (response.StatusCode != HttpStatusCode.OK)
                            Trace.WriteLine($"Failed posting log events to ELK, server responded: {response.StatusCode} {response.StatusDescription}");
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Exception posting to ELK {ex}");
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
            var page = 0;
            var chunk = new List<string>();

            foreach (var json in jsons)
            {
                if (bytes > MAX_BULK_BYTES)
                {
                    yield return PackageContent(chunk, bytes, page, includeDiagnostics);
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
                    Trace = $"EventCount={jsons.Count}, ByteCount={bytes}, PageCount={page}"
                });
                jsons.Add(diagnostic);
            }

            return new StringContent(string.Join("\n", jsons), Encoding.UTF8, "application/json");
        }

        public static string EventToJson(LogEvent logEvent)
        {
            if (logEvent == null)
            {
                throw new ArgumentNullException(nameof(logEvent));
            }

            var payload = new Dictionary<string, object>();
            try
            {
                foreach (var kvp in logEvent.Properties)
                {
                    var safeKey = kvp.Key.Replace(" ", "").Replace(":", "").Replace("-", "").Replace("_", "");
                    int dummy;
                    if (int.TryParse(kvp.Key, out dummy))
                        continue;
                    var simpleValue = SerilogPropertyFormatter.Simplify(kvp.Value);
                    payload[safeKey] = simpleValue;
                }

                payload["Level"] = logEvent.Level.ToString();
                payload["@timestamp"] = logEvent.Timestamp;

                var message = logEvent.RenderMessage();
                var messageBytes = Encoding.UTF8.GetBytes(message);

                if (messageBytes.Length > MAX_TERM_BYTES)
                {
                    var truncated = messageBytes.Length - MAX_TERM_BYTES;
                    var ending = $"[truncated {truncated}]";
                    var subLength = MAX_TERM_BYTES - ending.Length;
                    if (subLength > 0)
                    {
                        message = message.Substring(0, subLength) + ending;
                        payload["@truncated"] = truncated;
                    }
                }

                payload["Message"] = message;

                if (logEvent.Exception != null)
                {
                    payload["Exception"] = logEvent.Exception.ToString();
                    var stackTrace = logEvent.Exception.StackTrace;
                    if (stackTrace != null)
                    {
                        stackTrace = StackTraceFilterRegexp.Replace(stackTrace, string.Empty);
                        payload["exc_stacktrace_hash"] = GetMurmur3HashString(stackTrace);
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
                Trace.WriteLine($"Error extracting json from logEvent {ex}");
            }
            return null;
        }

        private static string GetMurmur3HashString(string value) 
            => BitConverter.ToString(MurmurHash.Create128().ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");


        private HttpWebRequest CreateWebRequest()
        {
            var url = $"{options.Url}/{options.IndexTemplate}{DateTime.UtcNow.ToString("yyyy.MM.dd")}";
            var webRequest = WebRequest.CreateHttp(new Uri(url));
            {
                webRequest.Method = WebRequestMethods.Http.Post;
                webRequest.Headers.Add("Authorization", $"ELK {options.AuthKey}");
                webRequest.ContentType = "application/octet-stream";
                webRequest.SendChunked = true;
                webRequest.KeepAlive = true;
                webRequest.Pipelined = true;
                webRequest.AllowWriteStreamBuffering = false;
                webRequest.AllowReadStreamBuffering = false;
                webRequest.AuthenticationLevel = AuthenticationLevel.None;
                webRequest.AutomaticDecompression = DecompressionMethods.None;
                webRequest.ServicePoint.Expect100Continue = false;
                webRequest.ServicePoint.ConnectionLimit = 200;
                webRequest.ServicePoint.UseNagleAlgorithm = false;
                webRequest.ServicePoint.ReceiveBufferSize = 1024;
                webRequest.Timeout = (int)options.Timeout.TotalMilliseconds;
                webRequest.ReadWriteTimeout = (int)options.Timeout.TotalMilliseconds;
            }
            return webRequest;
        }
    }
}
