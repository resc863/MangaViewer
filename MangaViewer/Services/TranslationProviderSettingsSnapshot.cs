namespace MangaViewer.Services
{
    internal sealed record TranslationProviderSettingsSnapshot(
        TranslationProviderKind Provider,
        string Model,
        string SystemPrompt,
        string ApiKey,
        string Endpoint)
    {
        public TranslationProviderDescriptor Descriptor => TranslationProviders.GetDescriptor(Provider);
        public string ProviderName => Descriptor.Name;
        public bool RequiresApiKey => Descriptor.RequiresApiKey;
        public bool UsesSelectableModel => Descriptor.UsesSelectableModel;
        public bool UsesEndpoint => Descriptor.UsesEndpoint;
    }
}
