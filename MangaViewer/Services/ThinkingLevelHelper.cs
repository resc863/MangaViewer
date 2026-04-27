using System;

namespace MangaViewer.Services
{
    internal static class ThinkingLevelHelper
    {
        public static bool IsOff(string? thinkingLevel)
            => string.IsNullOrWhiteSpace(thinkingLevel)
                || thinkingLevel.Equals("Off", StringComparison.OrdinalIgnoreCase)
                || thinkingLevel.Equals("False", StringComparison.OrdinalIgnoreCase)
                || thinkingLevel.Equals("0", StringComparison.OrdinalIgnoreCase);

        public static string NormalizeOllama(string? thinkingLevel)
        {
            return IsOff(thinkingLevel) ? "Off" : "On";
        }

        public static int GetAnthropicBudgetTokens(string? thinkingLevel)
        {
            return thinkingLevel switch
            {
                "Minimal" or "Low" => 1024,
                "Medium" => 5000,
                "High" => 10000,
                _ => 1024
            };
        }

        public static int GetGoogleThinkingBudget(string? thinkingLevel)
        {
            return thinkingLevel switch
            {
                "Minimal" => 128,
                "Low" => 1024,
                "Medium" => 8192,
                "High" => 24576,
                _ => 0
            };
        }

        public static string? NormalizeGoogle(string? thinkingLevel)
        {
            return thinkingLevel switch
            {
                "Minimal" => "minimal",
                "Low" => "low",
                "Medium" => "medium",
                "High" => "high",
                _ => null
            };
        }
    }
}
