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
        private interface ITranslationClientConfigurationBuilder
        {
            TranslationProviderKind Provider { get; }

            TranslationClientConfiguration Create(TranslationProviderSettingsSnapshot settings, string thinkingLevel);
        }

        private static readonly IReadOnlyDictionary<TranslationProviderKind, ITranslationClientConfigurationBuilder> s_builders
            = new Dictionary<TranslationProviderKind, ITranslationClientConfigurationBuilder>
            {
                [TranslationProviderKind.Google] = new GoogleTranslationClientConfigurationBuilder(),
                [TranslationProviderKind.OpenAI] = new OpenAiTranslationClientConfigurationBuilder(),
                [TranslationProviderKind.Anthropic] = new AnthropicTranslationClientConfigurationBuilder(),
                [TranslationProviderKind.Ollama] = new OllamaTranslationClientConfigurationBuilder(),
            };

        public static bool TryCreate(TranslationSettingsService settings, out TranslationClientConfiguration? configuration)
        {
            var providerSettings = settings.GetCurrentProviderSettings();
            configuration = s_builders.TryGetValue(providerSettings.Provider, out var builder)
                ? builder.Create(providerSettings, settings.ThinkingLevel)
                : null;

            return configuration is not null;
        }

        private sealed class GoogleTranslationClientConfigurationBuilder : ITranslationClientConfigurationBuilder
        {
            public TranslationProviderKind Provider => TranslationProviderKind.Google;

            public TranslationClientConfiguration Create(TranslationProviderSettingsSnapshot settings, string thinkingLevel)
            {
                return new TranslationClientConfiguration
                {
                    Model = settings.Model,
                    SystemPrompt = settings.SystemPrompt,
                    EffectiveThinkingLevel = thinkingLevel,
                    Client = new GoogleGenAIChatClient(settings.ApiKey, settings.Model, thinkingLevel)
                };
            }
        }

        private sealed class OpenAiTranslationClientConfigurationBuilder : ITranslationClientConfigurationBuilder
        {
            public TranslationProviderKind Provider => TranslationProviderKind.OpenAI;

            public TranslationClientConfiguration Create(TranslationProviderSettingsSnapshot settings, string thinkingLevel)
            {
                return new TranslationClientConfiguration
                {
                    Model = settings.Model,
                    SystemPrompt = settings.SystemPrompt,
                    EffectiveThinkingLevel = thinkingLevel,
                    Client = new OpenAIChatClient(settings.ApiKey, settings.Model, thinkingLevel: thinkingLevel)
                };
            }
        }

        private sealed class AnthropicTranslationClientConfigurationBuilder : ITranslationClientConfigurationBuilder
        {
            public TranslationProviderKind Provider => TranslationProviderKind.Anthropic;

            public TranslationClientConfiguration Create(TranslationProviderSettingsSnapshot settings, string thinkingLevel)
            {
                return new TranslationClientConfiguration
                {
                    Model = settings.Model,
                    SystemPrompt = settings.SystemPrompt,
                    EffectiveThinkingLevel = thinkingLevel,
                    Client = new AnthropicChatClient(settings.ApiKey, settings.Model, thinkingLevel)
                };
            }
        }

        private sealed class OllamaTranslationClientConfigurationBuilder : ITranslationClientConfigurationBuilder
        {
            public TranslationProviderKind Provider => TranslationProviderKind.Ollama;

            public TranslationClientConfiguration Create(TranslationProviderSettingsSnapshot settings, string thinkingLevel)
            {
                string effectiveThinkingLevel = ThinkingLevelHelper.NormalizeOllama(thinkingLevel);
                return new TranslationClientConfiguration
                {
                    Model = settings.Model,
                    SystemPrompt = settings.SystemPrompt,
                    EffectiveThinkingLevel = effectiveThinkingLevel,
                    Client = new OllamaChatClient(settings.Endpoint, settings.Model, effectiveThinkingLevel)
                };
            }
        }
    }
}
