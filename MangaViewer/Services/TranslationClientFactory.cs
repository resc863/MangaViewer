using System;
using Microsoft.Extensions.AI;

namespace MangaViewer.Services
{
    internal sealed class TranslationClientConfiguration
    {
        public required IChatClient Client { get; init; }
        public required string Model { get; init; }
        public required string SystemPrompt { get; init; }
        public required string EffectiveThinkingLevel { get; init; }
    }

    internal static class TranslationClientFactory
    {
        public static bool TryCreate(TranslationSettingsService settings, out TranslationClientConfiguration? configuration)
        {
            var providerSettings = settings.GetCurrentProviderSettings();
            var descriptor = providerSettings.Descriptor;
            string effectiveThinkingLevel = descriptor.NormalizeThinkingLevel(settings.ThinkingLevel);
            configuration = CreateConfiguration(providerSettings, effectiveThinkingLevel, descriptor.CreateClient);

            return true;
        }

        private static TranslationClientConfiguration CreateConfiguration(
            TranslationProviderSettingsSnapshot settings,
            string effectiveThinkingLevel,
            Func<TranslationProviderSettingsSnapshot, string, IChatClient> clientFactory)
        {
            return new TranslationClientConfiguration
            {
                Model = settings.Model,
                SystemPrompt = settings.SystemPrompt,
                EffectiveThinkingLevel = effectiveThinkingLevel,
                Client = clientFactory(settings, effectiveThinkingLevel)
            };
        }
    }
}
