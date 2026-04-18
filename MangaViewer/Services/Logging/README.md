# Logging

Overview
- Logging is centralized through the static `Log` helper in this folder.
- `Log` resolves its `ILogger` from `App.LoggerFactory`, so logger configuration stays in app startup.
- Current call sites primarily use it for OCR, folder loading, and translation-related diagnostics.

Available helpers
- `Log.Info(string message)`
- `Log.Warn(string message)`
- `Log.Debug(string message)`
- `Log.Error(string message)`
- `Log.Error(Exception ex, string message)`

Usage notes
- Keep messages concise and structured enough to correlate reader, OCR, and streaming flows.
- In hot paths, avoid heavy string formatting before the logger call.
- If new sinks or filtering rules are added, update `App.LoggerFactory` instead of bypassing `Log` at call sites.
