using System;
using System.Collections.Generic;
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
            configuration = providerSettings.Provider switch
            {
                TranslationProviderKind.Google => CreateConfiguration(
                    providerSettings,
                    settings.ThinkingLevel,
                    static (snapshot, thinkingLevel) => new GoogleGenAIChatClient(snapshot.ApiKey, snapshot.Model, thinkingLevel)),
                TranslationProviderKind.OpenAI => CreateConfiguration(
                    providerSettings,
                    settings.ThinkingLevel,
                    static (snapshot, thinkingLevel) => new OpenAIChatClient(snapshot.ApiKey, snapshot.Model, thinkingLevel: thinkingLevel)),
                TranslationProviderKind.Anthropic => CreateConfiguration(
                    providerSettings,
                    settings.ThinkingLevel,
                    static (snapshot, thinkingLevel) => new AnthropicChatClient(snapshot.ApiKey, snapshot.Model, thinkingLevel)),
                TranslationProviderKind.Ollama => CreateConfiguration(
                    providerSettings,
                    ThinkingLevelHelper.NormalizeOllama(settings.ThinkingLevel),
                    static (snapshot, thinkingLevel) => new OllamaChatClient(snapshot.Endpoint, snapshot.Model, thinkingLevel)),
                _ => null
            };

            return configuration is not null;
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
