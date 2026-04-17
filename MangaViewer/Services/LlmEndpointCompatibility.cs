using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    internal enum LlmApiFlavor
    {
        Unknown,
        Ollama,
        OpenAiCompatible
    }

    internal static class LlmEndpointCompatibility
    {
        private static readonly ConcurrentDictionary<string, LlmApiFlavor> s_flavorCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan ModelLoadPollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan ModelLoadMaxWait = TimeSpan.FromSeconds(30);

        private sealed class ModelEntry
        {
            public string Id { get; init; } = string.Empty;
            public string Status { get; init; } = string.Empty;
        }

        public static string NormalizeEndpoint(string? endpoint, string defaultEndpoint = "http://localhost:11434")
            => string.IsNullOrWhiteSpace(endpoint) ? defaultEndpoint : endpoint.Trim().TrimEnd('/');

        public static async Task<LlmApiFlavor> DetectApiFlavorAsync(HttpClient http, string endpoint, CancellationToken cancellationToken)
        {
            endpoint = NormalizeEndpoint(endpoint);
            if (s_flavorCache.TryGetValue(endpoint, out var cached))
            {
                DebugLog($"DetectApiFlavor cache hit: endpoint={endpoint}, flavor={cached}");
                return cached;
            }

            DebugLog($"DetectApiFlavor probing endpoint={endpoint}");

            if (await HasOllamaTagsEndpointAsync(http, endpoint, cancellationToken).ConfigureAwait(false))
                return RememberFlavor(endpoint, LlmApiFlavor.Ollama);

            if (await HasOpenAiModelsEndpointAsync(http, endpoint + "/models", cancellationToken).ConfigureAwait(false)
                || await HasOpenAiModelsEndpointAsync(http, endpoint + "/v1/models", cancellationToken).ConfigureAwait(false))
                return RememberFlavor(endpoint, LlmApiFlavor.OpenAiCompatible);

            DebugLog($"DetectApiFlavor failed: endpoint={endpoint}, flavor=Unknown");
            return LlmApiFlavor.Unknown;
        }

        public static async Task<List<string>> GetModelIdsAsync(HttpClient http, string endpoint, CancellationToken cancellationToken)
        {
            endpoint = NormalizeEndpoint(endpoint);
            var flavor = await DetectApiFlavorAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
            DebugLog($"GetModelIds start: endpoint={endpoint}, flavor={flavor}");
            if (flavor == LlmApiFlavor.Ollama)
                return await GetOllamaModelIdsAsync(http, endpoint, cancellationToken).ConfigureAwait(false);

            var modelEntries = await GetOpenAiModelEntriesAsync(http, endpoint + "/models", cancellationToken).ConfigureAwait(false);
            if (modelEntries.Count > 0)
            {
                DebugLog($"GetModelIds /models success: count={modelEntries.Count}, ids=[{string.Join(", ", modelEntries.Select(static m => m.Id))}]");
                return modelEntries.Select(static m => m.Id).ToList();
            }

            var fallbackIds = (await GetOpenAiModelEntriesAsync(http, endpoint + "/v1/models", cancellationToken).ConfigureAwait(false))
                .Select(static model => model.Id)
                .ToList();
            DebugLog($"GetModelIds /v1/models fallback: count={fallbackIds.Count}, ids=[{string.Join(", ", fallbackIds)}]");
            return fallbackIds;
        }

        public static async Task EnsureModelLoadedAsync(HttpClient http, string endpoint, string model, CancellationToken cancellationToken)
        {
            endpoint = NormalizeEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(model))
                return;

            var flavor = await DetectApiFlavorAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
            if (flavor != LlmApiFlavor.OpenAiCompatible)
                return;

            var modelEntries = await GetOpenAiModelEntriesAsync(http, endpoint + "/models", cancellationToken).ConfigureAwait(false);
            if (modelEntries.Count == 0)
            {
                DebugLog($"EnsureModelLoaded skipped: endpoint={endpoint}, model={model}, no /models entries returned");
                return;
            }

            var modelEntry = modelEntries.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
            if (modelEntry == null)
            {
                DebugLog($"EnsureModelLoaded skipped: endpoint={endpoint}, model={model}, model not found in /models");
                return;
            }

            if (IsLoadedStatus(modelEntry.Status))
            {
                DebugLog($"EnsureModelLoaded already loaded: model={model}, status={modelEntry.Status}");
                return;
            }

            if (!IsLoadingStatus(modelEntry.Status))
                await RequestModelLoadAsync(http, endpoint, model, cancellationToken).ConfigureAwait(false);

            await WaitForModelLoadedAsync(http, endpoint, model, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<(bool Vision, bool Thinking)> GetOpenAiCompatibleModelCapabilitiesAsync(HttpClient http, string endpoint, string model, CancellationToken cancellationToken)
        {
            endpoint = NormalizeEndpoint(endpoint);
            DebugLog($"GetOpenAiCompatibleModelCapabilities start: endpoint={endpoint}, model={model}");

            var modelSpecific = await TryGetPropsCapabilitiesAsync(http, endpoint + "/props?model=" + Uri.EscapeDataString(model) + "&autoload=false", cancellationToken).ConfigureAwait(false);
            if (modelSpecific.HasValue)
            {
                DebugLog($"GetOpenAiCompatibleModelCapabilities from model props: model={model}, vision={modelSpecific.Value.Vision}, thinking={modelSpecific.Value.Thinking}");
                return modelSpecific.Value;
            }

            var singleModel = await TryGetPropsCapabilitiesAsync(http, endpoint + "/props", cancellationToken).ConfigureAwait(false);
            if (singleModel.HasValue)
            {
                DebugLog($"GetOpenAiCompatibleModelCapabilities from single props: model={model}, vision={singleModel.Value.Vision}, thinking={singleModel.Value.Thinking}");
                return singleModel.Value;
            }

            var inferred = (InferVisionSupportFromModelId(model), InferThinkingSupportFromModelId(model));
            DebugLog($"GetOpenAiCompatibleModelCapabilities inferred: model={model}, vision={inferred.Item1}, thinking={inferred.Item2}");
            return inferred;
        }

        public static void ApplyOpenAiThinkingOptions(IDictionary<string, object?> payload, bool thinkingEnabled)
        {
            payload["reasoning_format"] = "none";
            payload["chat_template_kwargs"] = new Dictionary<string, object?>
            {
                ["enable_thinking"] = thinkingEnabled
            };
        }

        public static string ExtractAssistantText(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return string.Empty;

            if (root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.Object
                && TryReadMessageContent(messageElement, out var messageContent))
                return messageContent;

            if (root.TryGetProperty("response", out var responseElement)
                && responseElement.ValueKind == JsonValueKind.String)
                return responseElement.GetString() ?? string.Empty;

            if (root.TryGetProperty("choices", out var choicesElement)
                && choicesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choicesElement.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.Object)
                        continue;

                    if (choice.TryGetProperty("message", out var openAiMessage)
                        && openAiMessage.ValueKind == JsonValueKind.Object
                        && TryReadMessageContent(openAiMessage, out var openAiMessageContent))
                        return openAiMessageContent;

                    if (choice.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String)
                        return textElement.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static async Task<bool> HasOllamaTagsEndpointAsync(HttpClient http, string endpoint, CancellationToken cancellationToken)
        {
            using var doc = await TryGetJsonAsync(http, endpoint + "/api/tags", cancellationToken).ConfigureAwait(false);
            return doc != null
                && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("models", out var modelsElement)
                && modelsElement.ValueKind == JsonValueKind.Array;
        }

        private static async Task<bool> HasOpenAiModelsEndpointAsync(HttpClient http, string url, CancellationToken cancellationToken)
        {
            using var doc = await TryGetJsonAsync(http, url, cancellationToken).ConfigureAwait(false);
            return doc != null
                && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var dataElement)
                && dataElement.ValueKind == JsonValueKind.Array;
        }

        private static async Task<List<string>> GetOllamaModelIdsAsync(HttpClient http, string endpoint, CancellationToken cancellationToken)
        {
            using var doc = await TryGetJsonAsync(http, endpoint + "/api/tags", cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Failed to query model list from endpoint.");

            var modelIds = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in modelsElement.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object)
                        continue;

                    if (model.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                    {
                        var id = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                            modelIds.Add(id);
                    }
                }
            }

            modelIds.Sort(StringComparer.OrdinalIgnoreCase);
            DebugLog($"GetOllamaModelIds: endpoint={endpoint}, count={modelIds.Count}, ids=[{string.Join(", ", modelIds)}]");
            return modelIds;
        }

        private static async Task<List<ModelEntry>> GetOpenAiModelEntriesAsync(HttpClient http, string url, CancellationToken cancellationToken)
        {
            using var doc = await TryGetJsonAsync(http, url, cancellationToken).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                DebugLog($"GetOpenAiModelEntries: url={url}, no data array");
                return new List<ModelEntry>();
            }

            var modelEntries = new List<ModelEntry>();
            foreach (var model in dataElement.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object)
                    continue;

                if (!model.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                    continue;

                string id = idElement.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string status = string.Empty;
                if (model.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.Object
                    && statusElement.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String)
                {
                    status = valueElement.GetString() ?? string.Empty;
                }

                modelEntries.Add(new ModelEntry { Id = id, Status = status });
            }

            modelEntries.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            DebugLog($"GetOpenAiModelEntries: url={url}, count={modelEntries.Count}, entries=[{string.Join(", ", modelEntries.Select(m => $"{m.Id}:{m.Status}"))}]");
            return modelEntries;
        }

        private static async Task RequestModelLoadAsync(HttpClient http, string endpoint, string model, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new { model });
            DebugLog($"RequestModelLoad: endpoint={endpoint}, model={model}, payload={payload}");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/models/load")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        private static async Task WaitForModelLoadedAsync(HttpClient http, string endpoint, string model, CancellationToken cancellationToken)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(ModelLoadMaxWait);
            DebugLog($"WaitForModelLoaded start: endpoint={endpoint}, model={model}, timeout={ModelLoadMaxWait}");

            while (true)
            {
                waitCts.Token.ThrowIfCancellationRequested();

                var modelEntries = await GetOpenAiModelEntriesAsync(http, endpoint + "/models", waitCts.Token).ConfigureAwait(false);
                var modelEntry = modelEntries.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
                if (modelEntry == null)
                {
                    DebugLog($"WaitForModelLoaded stop: model={model} disappeared from /models");
                    return;
                }

                if (IsLoadedStatus(modelEntry.Status))
                {
                    DebugLog($"WaitForModelLoaded complete: model={model}, status={modelEntry.Status}");
                    return;
                }

                DebugLog($"WaitForModelLoaded polling: model={model}, status={modelEntry.Status}");

                await Task.Delay(ModelLoadPollInterval, waitCts.Token).ConfigureAwait(false);
            }
        }

        private static bool IsLoadedStatus(string status)
            => string.Equals(status, "loaded", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "sleeping", StringComparison.OrdinalIgnoreCase);

        private static bool IsLoadingStatus(string status)
            => string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase);

        private static bool InferVisionSupportFromModelId(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return false;

            string normalized = model.Trim();
            return normalized.Contains("ocr", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("vlm", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("qwen-vl", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("gemma-3", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("gemma-4", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("multimodal", StringComparison.OrdinalIgnoreCase);
        }

        private static bool InferThinkingSupportFromModelId(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return true;

            string normalized = model.Trim();
            if (normalized.Contains("ocr", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("asr", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("embedding", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("rerank", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static async Task<(bool Vision, bool Thinking)?> TryGetPropsCapabilitiesAsync(HttpClient http, string url, CancellationToken cancellationToken)
        {
            using var doc = await TryGetJsonAsync(http, url, cancellationToken).ConfigureAwait(false);
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                DebugLog($"TryGetPropsCapabilities: url={url}, no object response");
                return null;
            }

            if (doc.RootElement.TryGetProperty("role", out var roleElement)
                && roleElement.ValueKind == JsonValueKind.String
                && string.Equals(roleElement.GetString(), "router", StringComparison.OrdinalIgnoreCase))
            {
                DebugLog($"TryGetPropsCapabilities: url={url}, router-level props detected, ignoring for model capabilities");
                return null;
            }

            bool vision = false;
            if (doc.RootElement.TryGetProperty("modalities", out var modalitiesElement)
                && modalitiesElement.ValueKind == JsonValueKind.Object
                && modalitiesElement.TryGetProperty("vision", out var visionElement)
                && (visionElement.ValueKind == JsonValueKind.True || visionElement.ValueKind == JsonValueKind.False))
            {
                vision = visionElement.GetBoolean();
            }

            bool thinking = true;
            if (doc.RootElement.TryGetProperty("chat_template_caps", out var capsElement))
            {
                bool? capsThinking = TryReadThinkingSupport(capsElement);
                if (capsThinking.HasValue)
                    thinking = capsThinking.Value;
            }

            DebugLog($"TryGetPropsCapabilities: url={url}, vision={vision}, thinking={thinking}, keys=[{string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))}]");
            return (vision, thinking);
        }

        private static bool? TryReadThinkingSupport(JsonElement capsElement)
        {
            if (capsElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in capsElement.EnumerateObject())
            {
                if (!property.Name.Contains("think", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Contains("reason", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (property.Value.ValueKind == JsonValueKind.True || property.Value.ValueKind == JsonValueKind.False)
                    return property.Value.GetBoolean();

                if (property.Value.ValueKind == JsonValueKind.String)
                    return !string.IsNullOrWhiteSpace(property.Value.GetString());

                return true;
            }

            return null;
        }

        private static bool TryReadMessageContent(JsonElement messageElement, out string content)
        {
            content = string.Empty;
            if (!messageElement.TryGetProperty("content", out var contentElement))
                return false;

            if (contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString() ?? string.Empty;
                return true;
            }

            if (contentElement.ValueKind != JsonValueKind.Array)
                return false;

            var parts = new List<string>();
            foreach (var part in contentElement.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    parts.Add(part.GetString() ?? string.Empty);
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                    parts.Add(textElement.GetString() ?? string.Empty);
            }

            content = string.Join(string.Empty, parts);
            return true;
        }

        private static async Task<JsonDocument?> TryGetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DebugLog($"HTTP GET failed: url={url}, status={(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            DebugLog($"HTTP GET success: url={url}, body={Truncate(json)}");
            return JsonDocument.Parse(json);
        }

        private static LlmApiFlavor RememberFlavor(string endpoint, LlmApiFlavor flavor)
        {
            s_flavorCache[endpoint] = flavor;
            DebugLog($"DetectApiFlavor resolved: endpoint={endpoint}, flavor={flavor}");
            return flavor;
        }

        private static void DebugLog(string message)
            => Debug.WriteLine($"[LlmEndpointCompatibility] {message}");

        private static string Truncate(string text, int maxLength = 2000)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text[..maxLength] + " ... (truncated)";
        }
    }
}
