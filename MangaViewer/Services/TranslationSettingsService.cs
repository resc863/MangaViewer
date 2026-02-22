using System;

namespace MangaViewer.Services
{
    public class TranslationSettingsService
    {
        private static readonly Lazy<TranslationSettingsService> _instance = new(() => new TranslationSettingsService());
        public static TranslationSettingsService Instance => _instance.Value;

        public event EventHandler? SettingsChanged;

        public string Provider
        {
            get => SettingsProvider.Get("TranslationProvider", "Google");
            set
            {
                SettingsProvider.Set("TranslationProvider", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string Model
        {
            get => SettingsProvider.Get("TranslationModel", "gemini-3-flash-preview");
            set
            {
                SettingsProvider.Set("TranslationModel", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string GoogleApiKey
        {
            get => SettingsProvider.GetSecret("TranslationApiKey_Google");
            set
            {
                SettingsProvider.SetSecret("TranslationApiKey_Google", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string OpenAIApiKey
        {
            get => SettingsProvider.GetSecret("TranslationApiKey_OpenAI");
            set
            {
                SettingsProvider.SetSecret("TranslationApiKey_OpenAI", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string AnthropicApiKey
        {
            get => SettingsProvider.GetSecret("TranslationApiKey_Anthropic");
            set
            {
                SettingsProvider.SetSecret("TranslationApiKey_Anthropic", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
