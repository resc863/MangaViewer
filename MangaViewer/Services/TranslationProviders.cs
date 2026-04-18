using System;
using System.Collections.Generic;

namespace MangaViewer.Services
{
    internal enum TranslationProviderKind
    {
        Google,
        OpenAI,
        Anthropic,
        Ollama
    }

    internal static class TranslationProviders
    {
        public const string Google = "Google";
        public const string OpenAI = "OpenAI";
        public const string Anthropic = "Anthropic";
        public const string Ollama = "Ollama";

        private static readonly IReadOnlyDictionary<TranslationProviderKind, TranslationProviderDescriptor> s_descriptors
            = new Dictionary<TranslationProviderKind, TranslationProviderDescriptor>
            {
                [TranslationProviderKind.Google] = new TranslationProviderDescriptor
                {
                    Kind = TranslationProviderKind.Google,
                    Name = Google,
                    RequiresApiKey = true,
                    UsesSelectableModel = true,
                    UsesEndpoint = false,
                    GetModel = static service => service.GoogleModel,
                    SetModel = static (service, value) => service.GoogleModel = value,
                    GetSystemPrompt = static service => service.GoogleSystemPrompt,
                    SetSystemPrompt = static (service, value) => service.GoogleSystemPrompt = value,
                    GetApiKey = static service => service.GoogleApiKey,
                    SetApiKey = static (service, value) => service.GoogleApiKey = value,
                    GetEndpoint = static _ => string.Empty,
                    EmptyEndpoint = string.Empty,
                    NormalizeThinkingLevel = static thinkingLevel => thinkingLevel,
                    CreateClient = static (snapshot, thinkingLevel) => new GoogleGenAIChatClient(snapshot.ApiKey, snapshot.Model, thinkingLevel)
                },
                [TranslationProviderKind.OpenAI] = new TranslationProviderDescriptor
                {
                    Kind = TranslationProviderKind.OpenAI,
                    Name = OpenAI,
                    RequiresApiKey = true,
                    UsesSelectableModel = false,
                    UsesEndpoint = false,
                    GetModel = static service => service.OpenAIModel,
                    SetModel = static (service, value) => service.OpenAIModel = value,
                    GetSystemPrompt = static service => service.OpenAISystemPrompt,
                    SetSystemPrompt = static (service, value) => service.OpenAISystemPrompt = value,
                    GetApiKey = static service => service.OpenAIApiKey,
                    SetApiKey = static (service, value) => service.OpenAIApiKey = value,
                    GetEndpoint = static _ => string.Empty,
                    EmptyEndpoint = string.Empty,
                    NormalizeThinkingLevel = static thinkingLevel => thinkingLevel,
                    CreateClient = static (snapshot, thinkingLevel) => new OpenAIChatClient(snapshot.ApiKey, snapshot.Model, thinkingLevel: thinkingLevel)
                },
                [TranslationProviderKind.Anthropic] = new TranslationProviderDescriptor
                {
                    Kind = TranslationProviderKind.Anthropic,
                    Name = Anthropic,
                    RequiresApiKey = true,
                    UsesSelectableModel = false,
                    UsesEndpoint = false,
                    GetModel = static service => service.AnthropicModel,
                    SetModel = static (service, value) => service.AnthropicModel = value,
                    GetSystemPrompt = static service => service.AnthropicSystemPrompt,
                    SetSystemPrompt = static (service, value) => service.AnthropicSystemPrompt = value,
                    GetApiKey = static service => service.AnthropicApiKey,
                    SetApiKey = static (service, value) => service.AnthropicApiKey = value,
                    GetEndpoint = static _ => string.Empty,
                    EmptyEndpoint = string.Empty,
                    NormalizeThinkingLevel = static thinkingLevel => thinkingLevel,
                    CreateClient = static (snapshot, thinkingLevel) => new AnthropicChatClient(snapshot.ApiKey, snapshot.Model, thinkingLevel)
                },
                [TranslationProviderKind.Ollama] = new TranslationProviderDescriptor
                {
                    Kind = TranslationProviderKind.Ollama,
                    Name = Ollama,
                    RequiresApiKey = false,
                    UsesSelectableModel = true,
                    UsesEndpoint = true,
                    GetModel = static service => service.OllamaModel,
                    SetModel = static (service, value) => service.OllamaModel = value,
                    GetSystemPrompt = static service => service.OllamaSystemPrompt,
                    SetSystemPrompt = static (service, value) => service.OllamaSystemPrompt = value,
                    GetApiKey = static _ => string.Empty,
                    SetApiKey = static (_, _) => { },
                    GetEndpoint = static service => service.OllamaEndpoint,
                    EmptyEndpoint = string.Empty,
                    NormalizeThinkingLevel = static thinkingLevel => ThinkingLevelHelper.NormalizeOllama(thinkingLevel),
                    CreateClient = static (snapshot, thinkingLevel) => new OllamaChatClient(snapshot.Endpoint, snapshot.Model, thinkingLevel)
                }
            };

        private static readonly IReadOnlyDictionary<string, TranslationProviderDescriptor> s_descriptorsByName
            = new Dictionary<string, TranslationProviderDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [Google] = s_descriptors[TranslationProviderKind.Google],
                [OpenAI] = s_descriptors[TranslationProviderKind.OpenAI],
                [Anthropic] = s_descriptors[TranslationProviderKind.Anthropic],
                [Ollama] = s_descriptors[TranslationProviderKind.Ollama]
            };

        public static TranslationProviderDescriptor GetDescriptor(TranslationProviderKind provider)
            => s_descriptors.TryGetValue(provider, out var descriptor)
                ? descriptor
                : s_descriptors[TranslationProviderKind.Google];

        public static TranslationProviderDescriptor GetDescriptor(string? provider)
            => TryGetDescriptor(provider, out var descriptor)
                ? descriptor
                : GetDescriptor(TranslationProviderKind.Google);

        public static bool TryGetDescriptor(string? provider, out TranslationProviderDescriptor descriptor)
        {
            if (!string.IsNullOrWhiteSpace(provider) && s_descriptorsByName.TryGetValue(provider, out descriptor!))
                return true;

            descriptor = GetDescriptor(TranslationProviderKind.Google);
            return false;
        }

        public static TranslationProviderKind ParseOrDefault(string? provider, TranslationProviderKind defaultKind = TranslationProviderKind.Google)
            => TryParse(provider, out var kind) ? kind : defaultKind;

        public static bool TryParse(string? provider, out TranslationProviderKind kind)
        {
            if (TryGetDescriptor(provider, out var descriptor))
            {
                kind = descriptor.Kind;
                return true;
            }

            kind = default;
            return false;
        }

        public static string ToName(TranslationProviderKind provider)
            => GetDescriptor(provider).Name;
    }
}
