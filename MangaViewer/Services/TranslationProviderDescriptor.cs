using Microsoft.Extensions.AI;
using System;

namespace MangaViewer.Services
{
    internal sealed class TranslationProviderDescriptor
    {
        public required TranslationProviderKind Kind { get; init; }
        public required string Name { get; init; }
        public required bool RequiresApiKey { get; init; }
        public required bool UsesSelectableModel { get; init; }
        public required bool UsesEndpoint { get; init; }
        public required Func<TranslationSettingsService, string> GetModel { get; init; }
        public required Action<TranslationSettingsService, string> SetModel { get; init; }
        public required Func<TranslationSettingsService, string> GetSystemPrompt { get; init; }
        public required Action<TranslationSettingsService, string> SetSystemPrompt { get; init; }
        public required Func<TranslationSettingsService, string> GetApiKey { get; init; }
        public required Action<TranslationSettingsService, string> SetApiKey { get; init; }
        public required Func<TranslationSettingsService, string> GetEndpoint { get; init; }
        public required string EmptyEndpoint { get; init; }
        public required Func<string, string> NormalizeThinkingLevel { get; init; }
        public required Func<TranslationProviderSettingsSnapshot, string, IChatClient> CreateClient { get; init; }
    }
}
