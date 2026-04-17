using System;

namespace MangaViewer.Services
{
    internal static class ThinkingLevelHelper
    {
        public static string NormalizeOllama(string? thinkingLevel)
        {
            if (string.IsNullOrWhiteSpace(thinkingLevel))
                return "Off";

            if (thinkingLevel.Equals("Off", StringComparison.OrdinalIgnoreCase)
                || thinkingLevel.Equals("False", StringComparison.OrdinalIgnoreCase)
                || thinkingLevel.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                return "Off";
            }

            return "On";
        }
    }
}
