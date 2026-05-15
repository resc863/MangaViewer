// Project: MangaViewer
// File: Services/Logging/Log.cs
// Purpose: Centralized logging helper for Debug output. Keeps call sites concise without
//          requiring an external logging package.

using System;
using DiagnosticsDebug = System.Diagnostics.Debug;

namespace MangaViewer.Services.Logging
{
    public static class Log
    {
        private const string Category = "MangaViewer";

        public static void Info(string message) => Write("INFO", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Error(Exception ex, string message) => Write("ERROR", $"{message}{Environment.NewLine}{ex}");
        public static void Warn(string message) => Write("WARN", message);
        public static void Debug(string message) => Write("DEBUG", message);

        private static void Write(string level, string message)
        {
            DiagnosticsDebug.WriteLine($"[{Category}][{level}] {message}");
        }
    }
}
