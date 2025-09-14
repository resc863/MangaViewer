using System;
using Microsoft.Extensions.Logging;

namespace MangaViewer.Services.Logging
{
    public static class Log
    {
        private static ILogger? _logger;
        private static ILogger Logger => _logger ??= App.LoggerFactory.CreateLogger("MangaViewer");

        public static void Info(string message) => Logger.LogInformation(message);
        public static void Error(string message) => Logger.LogError(message);
        public static void Error(Exception ex, string message) => Logger.LogError(ex, message);
        public static void Warn(string message) => Logger.LogWarning(message);
        public static void Debug(string message) => Logger.LogDebug(message);
    }
}
