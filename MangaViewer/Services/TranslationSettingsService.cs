using System;

namespace MangaViewer.Services
{
    public sealed class TranslationSettingsService
    {
        private static readonly Lazy<TranslationSettingsService> _instance = new(() => new TranslationSettingsService());
        private const string DefaultProvider = TranslationProviders.Google;
        private const string DefaultTargetLanguage = "Korean";
        private const string DefaultThinkingLevel = "Off";
        private const int MinPrefetchAdjacentPageCount = 0;
        private const int MaxPrefetchAdjacentPageCount = 10;
        private const double MinOverlayFontSize = 8.0;
        private const double MaxOverlayFontSize = 28.0;
        private const double MinOverlayBoxScale = 0.6;
        private const double MaxOverlayBoxScale = 2.2;

        public static TranslationSettingsService Instance => _instance.Value;

        private TranslationSettingsService()
        {
        }

        public event EventHandler? SettingsChanged;

        private void RaiseSettingsChanged()
            => SettingsChanged?.Invoke(this, EventArgs.Empty);

        private static T GetValue<T>(string key, T defaultValue)
            => SettingsProvider.Get(key, defaultValue);

        private static string GetSecretValue(string key)
            => SettingsProvider.GetSecret(key);

        private void SetValue<T>(string key, T value)
        {
            SettingsProvider.Set(key, value);
            RaiseSettingsChanged();
        }

        private void SetSecretValue(string key, string value)
        {
            SettingsProvider.SetSecret(key, value);
            RaiseSettingsChanged();
        }

        public string Provider
        {
            get => GetValue("TranslationProvider", DefaultProvider);
            set => SetValue("TranslationProvider", value);
        }

        internal TranslationProviderKind ProviderKind
        {
            get => TranslationProviders.ParseOrDefault(Provider);
            set => Provider = TranslationProviders.ToName(value);
        }

        internal TranslationProviderSettingsSnapshot GetCurrentProviderSettings()
            => GetProviderSettings(ProviderKind);

        public static bool IsOllamaProvider(string? provider)
            => string.Equals(provider, TranslationProviders.Ollama, StringComparison.OrdinalIgnoreCase);

        internal static bool IsOllamaProvider(TranslationProviderKind provider)
            => provider == TranslationProviderKind.Ollama;

        internal TranslationProviderSettingsSnapshot GetProviderSettings(TranslationProviderKind provider)
            => provider switch
            {
                TranslationProviderKind.OpenAI => new TranslationProviderSettingsSnapshot(provider, OpenAIModel, OpenAISystemPrompt, OpenAIApiKey, string.Empty),
                TranslationProviderKind.Anthropic => new TranslationProviderSettingsSnapshot(provider, AnthropicModel, AnthropicSystemPrompt, AnthropicApiKey, string.Empty),
                TranslationProviderKind.Ollama => new TranslationProviderSettingsSnapshot(provider, OllamaModel, OllamaSystemPrompt, string.Empty, OllamaEndpoint),
                _ => new TranslationProviderSettingsSnapshot(TranslationProviderKind.Google, GoogleModel, GoogleSystemPrompt, GoogleApiKey, string.Empty)
            };

        internal TranslationProviderSettingsSnapshot GetProviderSettings(string? provider)
            => GetProviderSettings(TranslationProviders.ParseOrDefault(provider));

        public string GetModelForProvider(string? provider)
            => GetProviderSettings(provider).Model;

        internal string GetModelForProvider(TranslationProviderKind provider)
            => GetProviderSettings(provider).Model;

        public void SetModelForProvider(string? provider, string value)
            => SetModelForProvider(TranslationProviders.ParseOrDefault(provider), value);

        internal void SetModelForProvider(TranslationProviderKind provider, string value)
        {
            switch (provider)
            {
                case TranslationProviderKind.OpenAI:
                    OpenAIModel = value;
                    break;
                case TranslationProviderKind.Anthropic:
                    AnthropicModel = value;
                    break;
                case TranslationProviderKind.Ollama:
                    OllamaModel = value;
                    break;
                default:
                    GoogleModel = value;
                    break;
            }
        }

        public string GetSystemPromptForProvider(string? provider)
            => GetProviderSettings(provider).SystemPrompt;

        internal string GetSystemPromptForProvider(TranslationProviderKind provider)
            => GetProviderSettings(provider).SystemPrompt;

        public void SetSystemPromptForProvider(string? provider, string value)
            => SetSystemPromptForProvider(TranslationProviders.ParseOrDefault(provider), value);

        internal void SetSystemPromptForProvider(TranslationProviderKind provider, string value)
        {
            switch (provider)
            {
                case TranslationProviderKind.OpenAI:
                    OpenAISystemPrompt = value;
                    break;
                case TranslationProviderKind.Anthropic:
                    AnthropicSystemPrompt = value;
                    break;
                case TranslationProviderKind.Ollama:
                    OllamaSystemPrompt = value;
                    break;
                default:
                    GoogleSystemPrompt = value;
                    break;
            }
        }

        public string GetApiKeyForProvider(string? provider)
            => GetProviderSettings(provider).ApiKey;

        internal string GetApiKeyForProvider(TranslationProviderKind provider)
            => GetProviderSettings(provider).ApiKey;

        public void SetApiKeyForProvider(string? provider, string value)
            => SetApiKeyForProvider(TranslationProviders.ParseOrDefault(provider), value);

        internal void SetApiKeyForProvider(TranslationProviderKind provider, string value)
        {
            switch (provider)
            {
                case TranslationProviderKind.Google:
                    GoogleApiKey = value;
                    break;
                case TranslationProviderKind.OpenAI:
                    OpenAIApiKey = value;
                    break;
                case TranslationProviderKind.Anthropic:
                    AnthropicApiKey = value;
                    break;
            }
        }

        public string OllamaModel
        {
            get => GetValue("TranslationModel_Ollama", "qwen3:8b");
            set => SetValue("TranslationModel_Ollama", value);
        }

        public string OllamaEndpoint
        {
            get => GetValue("TranslationOllamaEndpoint", "http://localhost:11434");
            set => SetValue("TranslationOllamaEndpoint", value);
        }

        public string GoogleModel
        {
            get => GetValue("TranslationModel_Google", "gemini-3-flash-preview");
            set => SetValue("TranslationModel_Google", value);
        }

        public string OllamaSystemPrompt
        {
            get => GetValue("TranslationSystemPrompt_Ollama", "You are a professional translator. Translate the provided text to Korean naturally. Output only the translated text.");
            set => SetValue("TranslationSystemPrompt_Ollama", value);
        }

        public string OpenAIModel
        {
            get => GetValue("TranslationModel_OpenAI", "gpt-5.2");
            set => SetValue("TranslationModel_OpenAI", value);
        }

        public string AnthropicModel
        {
            get => GetValue("TranslationModel_Anthropic", "claude-sonnet-4-6");
            set => SetValue("TranslationModel_Anthropic", value);
        }

        public string GoogleSystemPrompt
        {
            get => GetValue("TranslationSystemPrompt_Google", "Translate the provided text to Korean. Output only the translated text.");
            set => SetValue("TranslationSystemPrompt_Google", value);
        }

        public string OpenAISystemPrompt
        {
            get => GetValue("TranslationSystemPrompt_OpenAI", "You are a professional translator. Translate the provided text to Korean, preserving the nuance and style. Output only the translated text.");
            set => SetValue("TranslationSystemPrompt_OpenAI", value);
        }

        public string AnthropicSystemPrompt
        {
            get => GetValue("TranslationSystemPrompt_Anthropic", "You are an expert translator. Translate the provided text to Korean with natural phrasing. Output only the translated text.");
            set => SetValue("TranslationSystemPrompt_Anthropic", value);
        }

        public string GoogleApiKey
        {
            get => GetSecretValue("TranslationApiKey_Google");
            set => SetSecretValue("TranslationApiKey_Google", value);
        }

        public string OpenAIApiKey
        {
            get => GetSecretValue("TranslationApiKey_OpenAI");
            set => SetSecretValue("TranslationApiKey_OpenAI", value);
        }

        public string AnthropicApiKey
        {
            get => GetSecretValue("TranslationApiKey_Anthropic");
            set => SetSecretValue("TranslationApiKey_Anthropic", value);
        }

        public string ThinkingLevel
        {
            get => GetValue("TranslationThinkingLevel", DefaultThinkingLevel);
            set => SetValue("TranslationThinkingLevel", value);
        }

        public string TargetLanguage
        {
            get => GetValue("TranslationTargetLanguage", DefaultTargetLanguage);
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? DefaultTargetLanguage : value.Trim();
                SetValue("TranslationTargetLanguage", normalized);
            }
        }

        public bool PrefetchAdjacentPagesEnabled
        {
            get => GetValue("TranslationPrefetchAdjacentPagesEnabled", true);
            set => SetValue("TranslationPrefetchAdjacentPagesEnabled", value);
        }

        public int PrefetchAdjacentPageCount
        {
            get => Math.Clamp(GetValue("TranslationPrefetchAdjacentPageCount", 1), MinPrefetchAdjacentPageCount, MaxPrefetchAdjacentPageCount);
            set
            {
                int clamped = Math.Clamp(value, MinPrefetchAdjacentPageCount, MaxPrefetchAdjacentPageCount);
                SetValue("TranslationPrefetchAdjacentPageCount", clamped);
            }
        }

        public double OverlayFontSize
        {
            get => Math.Clamp(GetValue("TranslationOverlayFontSize", 13.0), MinOverlayFontSize, MaxOverlayFontSize);
            set
            {
                double clamped = Math.Clamp(value, MinOverlayFontSize, MaxOverlayFontSize);
                SetValue("TranslationOverlayFontSize", clamped);
            }
        }

        public double OverlayBoxScaleHorizontal
        {
            get
            {
                double legacy = Math.Clamp(GetValue("TranslationOverlayBoxScale", 1.0), MinOverlayBoxScale, MaxOverlayBoxScale);
                return Math.Clamp(GetValue("TranslationOverlayBoxScaleHorizontal", legacy), MinOverlayBoxScale, MaxOverlayBoxScale);
            }
            set
            {
                double clamped = Math.Clamp(value, MinOverlayBoxScale, MaxOverlayBoxScale);
                SetValue("TranslationOverlayBoxScaleHorizontal", clamped);
            }
        }

        public double OverlayBoxScaleVertical
        {
            get
            {
                double legacy = Math.Clamp(GetValue("TranslationOverlayBoxScale", 1.0), MinOverlayBoxScale, MaxOverlayBoxScale);
                return Math.Clamp(GetValue("TranslationOverlayBoxScaleVertical", legacy), MinOverlayBoxScale, MaxOverlayBoxScale);
            }
            set
            {
                double clamped = Math.Clamp(value, MinOverlayBoxScale, MaxOverlayBoxScale);
                SetValue("TranslationOverlayBoxScaleVertical", clamped);
            }
        }

        public double OverlayBoxScale
        {
            get => Math.Clamp(GetValue("TranslationOverlayBoxScale", 1.0), MinOverlayBoxScale, MaxOverlayBoxScale);
            set
            {
                double clamped = Math.Clamp(value, MinOverlayBoxScale, MaxOverlayBoxScale);
                SettingsProvider.Set("TranslationOverlayBoxScale", clamped);
                SettingsProvider.Set("TranslationOverlayBoxScaleHorizontal", clamped);
                SettingsProvider.Set("TranslationOverlayBoxScaleVertical", clamped);
                RaiseSettingsChanged();
            }
        }
    }
}
