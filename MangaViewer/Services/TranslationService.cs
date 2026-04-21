using MangaViewer.Services.Logging;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace MangaViewer.Services
{
    public sealed record IndexedTranslationInput(int Index, string Text);

    public sealed class TranslationService
    {
        private static readonly Lazy<TranslationService> _instance = new(() => new TranslationService());
        private static readonly ConcurrentDictionary<string, string> s_translationCache = new(StringComparer.Ordinal);
        private static readonly JsonSerializerOptions s_promptJsonSerializerOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static TranslationService Instance => _instance.Value;

        private TranslationService()
        {
        }

        public void ClearCache() => s_translationCache.Clear();

        public async Task<string> TranslateTextAsync(string text, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            var settings = TranslationSettingsService.Instance;
            var providerSettings = settings.GetCurrentProviderSettings();
            if (!TranslationClientFactory.TryCreate(settings, out var translationClient))
                return "번역 공급자를 설정해주세요.";

            string targetLanguage = GetTargetLanguage(settings);
            string cacheKey = providerSettings.UsesEndpoint
                ? $"{providerSettings.ProviderName}|{providerSettings.Endpoint}|{translationClient.Model}|think={translationClient.EffectiveThinkingLevel}|lang={targetLanguage}|{text}"
                : $"{providerSettings.ProviderName}|{translationClient.Model}|{translationClient.EffectiveThinkingLevel}|lang={targetLanguage}|{text}";
            if (!forceRefresh && s_translationCache.TryGetValue(cacheKey, out string? cached))
                return cached;

            var messages = new List<ChatMessage>();
            string effectiveSystemPrompt = string.IsNullOrWhiteSpace(translationClient.SystemPrompt)
                ? $"You are a professional translator. Translate the user text into {targetLanguage}. Return only the translated text."
                : translationClient.SystemPrompt + $" Translate the output into {targetLanguage} and return only the translated text.";
            messages.Add(new ChatMessage(ChatRole.System, effectiveSystemPrompt));
            messages.Add(new ChatMessage(ChatRole.User, text));

            Log.Info($"[Translation][Request] provider={providerSettings.ProviderName}, model={translationClient.Model}, thinking={translationClient.EffectiveThinkingLevel}, targetLanguage={targetLanguage}");
            Log.Info($"[Translation][Request][System] {effectiveSystemPrompt}");
            Log.Info($"[Translation][Request][User] {text}");

            using (translationClient.Client)
            {
                var response = await translationClient.Client.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
                string result = response.Messages.Count > 0 ? (response.Messages[0].Text ?? string.Empty) : string.Empty;
                s_translationCache[cacheKey] = result;
                return result;
            }
        }

        internal async Task<IReadOnlyDictionary<int, string>> TranslateIndexedTextAsync(
            IReadOnlyList<IndexedTranslationInput> inputs,
            string? pageText,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (inputs.Count == 0)
                return EmptyTranslations();

            string fullText = string.Join(Environment.NewLine, inputs.Select(input => input.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
            string effectivePageText = string.IsNullOrWhiteSpace(pageText) ? fullText : pageText.Trim();
            var settings = TranslationSettingsService.Instance;
            var providerSettings = settings.GetCurrentProviderSettings();
            if (!TranslationClientFactory.TryCreate(settings, out var translationClient))
                return EmptyTranslations();

            string targetLanguage = GetTargetLanguage(settings);
            string boxInputKey = string.Join("\u241E", inputs.Select(input => input.Text));
            string cacheKey = providerSettings.UsesEndpoint
                ? $"box|{providerSettings.ProviderName}|{providerSettings.Endpoint}|{translationClient.Model}|think={translationClient.EffectiveThinkingLevel}|lang={targetLanguage}|{effectivePageText}|{boxInputKey}"
                : $"box|{providerSettings.ProviderName}|{translationClient.Model}|{translationClient.EffectiveThinkingLevel}|lang={targetLanguage}|{effectivePageText}|{boxInputKey}";

            if (!forceRefresh && s_translationCache.TryGetValue(cacheKey, out string? cached))
                return ParseIndexedTranslations(cached);

            var boxPayload = new
            {
                page_text = effectivePageText,
                boxes = inputs.Select(input => new { index = input.Index, text = input.Text }).ToArray()
            };

            string jsonPayload = JsonSerializer.Serialize(boxPayload, s_promptJsonSerializerOptions);
            string boxSystemPrompt = string.IsNullOrWhiteSpace(translationClient.SystemPrompt)
                ? "You are a professional manga translator."
                : translationClient.SystemPrompt;
            boxSystemPrompt += $" Translate into {targetLanguage}. Return only strict JSON.";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, boxSystemPrompt),
                new(ChatRole.User,
                    $"Use page_text as full-page context, but translate each box independently into {targetLanguage}. " +
                    "For every output item, text must be the translation of the matching input box text with the same index. " +
                    "Do not merge boxes, skip boxes, or output full-page summaries. Preserve speaking style and tone. Return JSON only in this schema: " +
                    "{\"translations\":[{\"index\":0,\"text\":\"...\"}]}. " +
                    "Use the exact same index values from input. " +
                    "Do not add markdown, comments, or extra keys.\nInput JSON:\n" + jsonPayload)
            };

            Log.Info($"[Translation][Request] provider={providerSettings.ProviderName}, model={translationClient.Model}, thinking={translationClient.EffectiveThinkingLevel}, targetLanguage={targetLanguage}, mode=box");
            Log.Info($"[Translation][Request][System] {boxSystemPrompt}");
            Log.Info($"[Translation][Request][User] {messages[1].Text}");

            using (translationClient.Client)
            {
                var response = await translationClient.Client.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
                string content = response.Messages.Count > 0 ? (response.Messages[0].Text ?? string.Empty) : string.Empty;
                string normalizedJson = NormalizeJsonText(content);
                s_translationCache[cacheKey] = normalizedJson;
                return ParseIndexedTranslations(normalizedJson);
            }
        }

        private static string GetTargetLanguage(TranslationSettingsService settings)
            => string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "Korean" : settings.TargetLanguage.Trim();

        private static IReadOnlyDictionary<int, string> ParseIndexedTranslations(string json)
        {
            var translationMap = new Dictionary<int, string>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;

                JsonElement translationsElement;
                bool found = false;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("translations", out translationsElement) && translationsElement.ValueKind == JsonValueKind.Array)
                {
                    found = true;
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    translationsElement = root;
                    found = true;
                }
                else
                {
                    translationsElement = default;
                }

                if (!found)
                    return translationMap;

                foreach (var item in translationsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var indexElement))
                        continue;

                    int index;
                    if (indexElement.ValueKind == JsonValueKind.Number)
                    {
                        if (!indexElement.TryGetInt32(out index))
                            continue;
                    }
                    else if (indexElement.ValueKind == JsonValueKind.String && int.TryParse(indexElement.GetString(), out int parsed))
                    {
                        index = parsed;
                    }
                    else
                    {
                        continue;
                    }

                    string translatedText = string.Empty;
                    if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                        translatedText = textElement.GetString() ?? string.Empty;
                    else if (item.TryGetProperty("translated", out var translatedElement) && translatedElement.ValueKind == JsonValueKind.String)
                        translatedText = translatedElement.GetString() ?? string.Empty;
                    else if (item.TryGetProperty("translation", out var translationElement) && translationElement.ValueKind == JsonValueKind.String)
                        translatedText = translationElement.GetString() ?? string.Empty;

                    translationMap[index] = translatedText;
                }
            }
            catch
            {
            }

            return translationMap;
        }

        private static string NormalizeJsonText(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "{}";
            string trimmed = content.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return ExtractJsonPayload(trimmed);

            int firstLineBreak = trimmed.IndexOf('\n');
            if (firstLineBreak < 0)
                return trimmed.Trim('`').Trim();

            string body = trimmed[(firstLineBreak + 1)..];
            int fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                body = body[..fenceEnd];
            return ExtractJsonPayload(body.Trim());
        }

        private static string ExtractJsonPayload(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            int objStart = text.IndexOf('{');
            int arrStart = text.IndexOf('[');
            int start = objStart < 0 ? arrStart : arrStart < 0 ? objStart : Math.Min(objStart, arrStart);
            if (start < 0) return text.Trim();

            int objEnd = text.LastIndexOf('}');
            int arrEnd = text.LastIndexOf(']');
            int end = Math.Max(objEnd, arrEnd);
            if (end < start) return text[start..].Trim();

            return text.Substring(start, end - start + 1).Trim();
        }

        private static IReadOnlyDictionary<int, string> EmptyTranslations()
            => new Dictionary<int, string>();
    }
}
