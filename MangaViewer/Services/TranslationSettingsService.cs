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

        public string OllamaModel
        {
            get => SettingsProvider.Get("TranslationModel_Ollama", "qwen3:8b");
            set
            {
                SettingsProvider.Set("TranslationModel_Ollama", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string OllamaEndpoint
        {
            get => SettingsProvider.Get("TranslationOllamaEndpoint", "http://localhost:11434");
            set
            {
                SettingsProvider.Set("TranslationOllamaEndpoint", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string GoogleModel
        {
            get => SettingsProvider.Get("TranslationModel_Google", "gemini-3-flash-preview");
            set
            {
                SettingsProvider.Set("TranslationModel_Google", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string OllamaSystemPrompt
        {
            get => SettingsProvider.Get("TranslationSystemPrompt_Ollama", "You are a professional translator. Translate the provided text to Korean naturally. Output only the translated text.");
            set
            {
                SettingsProvider.Set("TranslationSystemPrompt_Ollama", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string OpenAIModel
        {
            get => SettingsProvider.Get("TranslationModel_OpenAI", "gpt-5.2");
            set
            {
                SettingsProvider.Set("TranslationModel_OpenAI", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string AnthropicModel
        {
            get => SettingsProvider.Get("TranslationModel_Anthropic", "claude-sonnet-4-6");
            set
            {
                SettingsProvider.Set("TranslationModel_Anthropic", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string GoogleSystemPrompt
        {
            get => SettingsProvider.Get("TranslationSystemPrompt_Google", "Translate the provided text to Korean. Output only the translated text.");
            set
            {
                SettingsProvider.Set("TranslationSystemPrompt_Google", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string OpenAISystemPrompt
        {
            get => SettingsProvider.Get("TranslationSystemPrompt_OpenAI", "You are a professional translator. Translate the provided text to Korean, preserving the nuance and style. Output only the translated text.");
            set
            {
                SettingsProvider.Set("TranslationSystemPrompt_OpenAI", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string AnthropicSystemPrompt
        {
            get => SettingsProvider.Get("TranslationSystemPrompt_Anthropic", "You are an expert translator. Translate the provided text to Korean with natural phrasing. Output only the translated text.");
            set
            {
                SettingsProvider.Set("TranslationSystemPrompt_Anthropic", value);
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

        public string ThinkingLevel
        {
            get => SettingsProvider.Get("TranslationThinkingLevel", "Off");
            set
            {
                SettingsProvider.Set("TranslationThinkingLevel", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string TargetLanguage
        {
            get => SettingsProvider.Get("TranslationTargetLanguage", "Korean");
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? "Korean" : value.Trim();
                SettingsProvider.Set("TranslationTargetLanguage", normalized);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool PrefetchAdjacentPagesEnabled
        {
            get => SettingsProvider.Get("TranslationPrefetchAdjacentPagesEnabled", true);
            set
            {
                SettingsProvider.Set("TranslationPrefetchAdjacentPagesEnabled", value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public int PrefetchAdjacentPageCount
        {
            get => Math.Clamp(SettingsProvider.Get("TranslationPrefetchAdjacentPageCount", 1), 0, 10);
            set
            {
                int clamped = Math.Clamp(value, 0, 10);
                SettingsProvider.Set("TranslationPrefetchAdjacentPageCount", clamped);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public double OverlayFontSize
        {
            get => Math.Clamp(SettingsProvider.Get("TranslationOverlayFontSize", 13.0), 8.0, 28.0);
            set
            {
                double clamped = Math.Clamp(value, 8.0, 28.0);
                SettingsProvider.Set("TranslationOverlayFontSize", clamped);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
