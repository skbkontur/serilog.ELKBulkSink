# serlog.elkbulksink

A Bulk Http Sink for [Serilog](https://github.com/serilog/serilog)

## Usage

```csharp
   var configuration = new LoggerConfiguration().WriteTo.ELKBulk("http://vm-elk:8080/logs/", "test-", period: TimeSpan.FromSeconds(1));
   var logger = configuration.CreateLogger();
   Log.Logger = logger;
```

Index name will be test-yyyy.MM.dd
