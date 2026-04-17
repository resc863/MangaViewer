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

        public static TranslationProviderKind ParseOrDefault(string? provider, TranslationProviderKind defaultKind = TranslationProviderKind.Google)
            => TryParse(provider, out var kind) ? kind : defaultKind;

        public static bool TryParse(string? provider, out TranslationProviderKind kind)
        {
            kind = provider switch
            {
                Google => TranslationProviderKind.Google,
                OpenAI => TranslationProviderKind.OpenAI,
                Anthropic => TranslationProviderKind.Anthropic,
                Ollama => TranslationProviderKind.Ollama,
                _ => default
            };

            return provider is Google or OpenAI or Anthropic or Ollama;
        }

        public static string ToName(TranslationProviderKind provider)
            => provider switch
            {
                TranslationProviderKind.Google => Google,
                TranslationProviderKind.OpenAI => OpenAI,
                TranslationProviderKind.Anthropic => Anthropic,
                TranslationProviderKind.Ollama => Ollama,
                _ => Google
            };
    }
}
