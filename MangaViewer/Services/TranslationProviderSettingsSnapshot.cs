namespace MangaViewer.Services
{
    internal sealed record TranslationProviderSettingsSnapshot(
        TranslationProviderKind Provider,
        string Model,
        string SystemPrompt,
        string ApiKey,
        string Endpoint)
    {
        public string ProviderName => TranslationProviders.ToName(Provider);
        public bool RequiresApiKey => Provider != TranslationProviderKind.Ollama;
        public bool UsesSelectableModel => Provider is TranslationProviderKind.Google or TranslationProviderKind.Ollama;
        public bool UsesEndpoint => Provider == TranslationProviderKind.Ollama;
    }
}
