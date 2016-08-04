using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Serilog.Events;
using Serilog.Parsing;

namespace Serilog.ELKBulkSink.Tests
{
    [TestClass]
    public class SerilogELKBulkSinkTests
    {
        [TestMethod]
        public void TestAddIfContains()
        {
            var dictionary = new Dictionary<string, string>()
            {
                {"hello", "world"}
            };
            ELKSink.AddIfNotContains(dictionary, "hello", "another world");
            dictionary.ContainsKey("hello").Should().BeTrue();
            dictionary["hello"].Should().Be("world");


            ELKSink.AddIfNotContains(dictionary, "newkey", "orange");
            dictionary.ContainsKey("newkey").Should().BeTrue();
            dictionary["newkey"].Should().Be("orange");
        }

        [TestMethod]
        public void PackageContentsTest()
        {
            var jsons = new[]
            {
                "{'fruit': 'orange'}",
                "{'fruit': 'apple'}",
                "{'fruit': 'banana'}",
            }.ToList();

            var noDiagContent = ELKSink.PackageContent(jsons, Encoding.UTF8.GetByteCount(string.Join("\n", jsons)), 0, false);
            var stringContent = ELKSink.PackageContent(jsons, Encoding.UTF8.GetByteCount(string.Join("\n", jsons)), 0, true);
            stringContent.Should().NotBeNull();
            noDiagContent.Should().NotBeNull();
            var result = stringContent.ReadAsStringAsync().GetAwaiter().GetResult();
            var resultNoDiag = noDiagContent.ReadAsStringAsync().GetAwaiter().GetResult();
            result.Split('\n').Count().Should().Be(4);
            resultNoDiag.Split('\n').Count().Should().Be(3);
        }

        [TestMethod]
        public void TestRender()
        {
            var logEvent = new LogEvent(DateTimeOffset.UtcNow,
                LogEventLevel.Debug, null, new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()), new []
                {
                    new LogEventProperty("test1", new ScalarValue("answer1")),
                    new LogEventProperty("0", new ScalarValue("this should be missing")),
                    new LogEventProperty("key", new ScalarValue("value"))
                });
            var result = ELKSink.EventToJson(logEvent);
            var json = JsonConvert.DeserializeObject<dynamic>(result);
            Console.WriteLine(json);
            (json["test1"].Value as string).Should().Be("answer1");
            bool hasZero = (json["0"] == null);
            hasZero.Should().Be(true);
            (json["key"].Value as string).Should().Be("value");
        }

        [TestMethod]
        public void IncludeDiagnostics_WhenEnabled_IncludesDiagnosticsEvent()
        {
            var logEvent = new LogEvent(DateTimeOffset.UtcNow,
                LogEventLevel.Debug, null, new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()), new[]
                {
                    new LogEventProperty("Field1", new ScalarValue("Value1")),
                });
            var result = new List<string>{ELKSink.EventToJson(logEvent)};

            var package = ELKSink.PackageContent(result, 1024, 5, true);

            var packageStringTask = package.ReadAsStringAsync();
            packageStringTask.Wait();
            var packageString = packageStringTask.Result;

            Assert.IsTrue(result.Count == 2);
            Assert.IsTrue(result[1].Contains("LogglyDiagnostics"));
            Assert.IsTrue(packageString.Contains("LogglyDiagnostics"));
        }

        [TestMethod]
        public void IncludeDiagnostics_WhenEnabled_DoesNotIncludeDiagnosticsEvent()
        {
            var logEvent = new LogEvent(DateTimeOffset.UtcNow,
                LogEventLevel.Debug, null, new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()), new[]
                {
                    new LogEventProperty("Field1", new ScalarValue("Value1")),
                });
            var result = new List<string> { ELKSink.EventToJson(logEvent) };

            var package = ELKSink.PackageContent(result, 1024, 5);

            var packageStringTask = package.ReadAsStringAsync();
            packageStringTask.Wait();
            var packageString = packageStringTask.Result;

            Assert.IsTrue(result.Count == 1);
            Assert.IsTrue(!packageString.Contains("LogglyDiagnostics"));
        }

        [TestMethod, TestCategory("Functional")]
        public void ELKIntegration()
        {
            var configuration = new LoggerConfiguration().WriteTo.ELKBulk(new SinkOptions { Url = "http://vm-elk:8080/logs/", IndexTemplate = "test-", Period = TimeSpan.FromSeconds(1)});
            Debugging.SelfLog.Out = Console.Out;
            var logger = configuration.CreateLogger();
            Log.Logger = logger;
            Log.Information("Test record");
            Log.Error(new Exception("Test exception"), "Exception test");
            ((IDisposable)logger).Dispose();
        }
    }
}
