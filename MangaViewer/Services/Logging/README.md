# Logging

Overview
- Use the `Log` static helper. Currently only Debug output is configured in `App.ConfigureLogging`.

Public API examples
- `Log.Error(Exception ex, string message)`
- `Log.Info(string message)`

Change notes
- When adding sinks (file/ETW/console), extend `ILoggerFactory` accordingly.
- In hot paths, avoid excessive string formatting/allocations; set appropriate `LogLevel` filters.
