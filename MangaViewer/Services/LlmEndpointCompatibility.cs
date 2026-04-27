using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private static readonly ConcurrentDictionary<string, bool> s_slotManagementSupport = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] s_openAiModelPaths = ["/models", "/v1/models"];
        private static readonly TimeSpan ModelLoadPollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan ModelLoadMaxWait = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SlotEraseTimeout = TimeSpan.FromSeconds(5);

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

            var (_, modelEntries) = await GetOpenAiModelEntriesAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
            if (modelEntries.Count > 0)
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

            var (url, modelEntries) = await GetOpenAiModelEntriesAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
            var modelIds = modelEntries.Select(static model => model.Id).ToList();
            DebugLog($"GetModelIds {url} result: count={modelIds.Count}, ids=[{string.Join(", ", modelIds)}]");
            return modelIds;
        }

        public static async Task EnsureModelLoadedAsync(HttpClient http, string endpoint, string model, CancellationToken cancellationToken)
        {
            endpoint = NormalizeEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(model))
                return;

            var flavor = await DetectApiFlavorAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
            if (flavor != LlmApiFlavor.OpenAiCompatible)
                return;

            var (url, modelEntries) = await GetOpenAiModelEntriesAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
            if (modelEntries.Count == 0)
            {
                DebugLog($"EnsureModelLoaded skipped: endpoint={endpoint}, model={model}, no model entries returned");
                return;
            }

            var modelEntry = modelEntries.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
            if (modelEntry == null)
            {
                DebugLog($"EnsureModelLoaded skipped: endpoint={endpoint}, model={model}, model not found in {url}");
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

        public static bool SupportsManagedSlots(string endpoint, string? model)
        {
            string key = BuildSlotManagementKey(endpoint, model);
            return !s_slotManagementSupport.TryGetValue(key, out bool supported) || supported;
        }

        public static async Task<int?> GetOpenAiCompatibleTotalSlotsAsync(HttpClient http, string endpoint, string? model, CancellationToken cancellationToken)
        {
            endpoint = NormalizeEndpoint(endpoint);
            var flavor = await DetectApiFlavorAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
            if (flavor != LlmApiFlavor.OpenAiCompatible)
                return null;

            if (!string.IsNullOrWhiteSpace(model))
            {
                int? modelSpecific = await TryGetTotalSlotsAsync(
                    http,
                    endpoint + "/props?model=" + Uri.EscapeDataString(model) + "&autoload=false",
                    cancellationToken).ConfigureAwait(false);
                if (modelSpecific.HasValue)
                    return modelSpecific.Value;
            }

            return await TryGetTotalSlotsAsync(http, endpoint + "/props", cancellationToken).ConfigureAwait(false);
        }

        public static async Task<bool> TryEraseSlotAsync(HttpClient http, string endpoint, int slotId, string? model = null)
        {
            if (slotId < 0)
                return false;

            endpoint = NormalizeEndpoint(endpoint);
            string key = BuildSlotManagementKey(endpoint, model);

            try
            {
                using var timeoutCts = new CancellationTokenSource(SlotEraseTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/slots/" + slotId + "?action=erase");
                string body = string.IsNullOrWhiteSpace(model)
                    ? "{}"
                    : BuildModelRequestJson(model);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    s_slotManagementSupport[key] = true;
                    DebugLog($"TryEraseSlotAsync success: endpoint={endpoint}, slot={slotId}, model={model ?? "<none>"}");
                    return true;
                }

                if ((int)response.StatusCode is 400 or 404 or 405 or 501)
                {
                    s_slotManagementSupport[key] = false;
                    DebugLog($"TryEraseSlotAsync disabling managed slots: endpoint={endpoint}, slot={slotId}, model={model ?? "<none>"}, status={(int)response.StatusCode} {response.ReasonPhrase}");
                    return false;
                }

                DebugLog($"TryEraseSlotAsync failed: endpoint={endpoint}, slot={slotId}, model={model ?? "<none>"}, status={(int)response.StatusCode} {response.ReasonPhrase}");
                return false;
            }
            catch (Exception ex)
            {
                DebugLog($"TryEraseSlotAsync exception: endpoint={endpoint}, slot={slotId}, model={model ?? "<none>"}, error={ex.Message}");
                return false;
            }
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

        private static async Task<(string Url, List<ModelEntry> Entries)> GetOpenAiModelEntriesAsync(HttpClient http, string endpoint, CancellationToken cancellationToken)
        {
            foreach (var url in GetOpenAiModelUrls(endpoint))
            {
                var entries = await GetOpenAiModelEntriesFromUrlAsync(http, url, cancellationToken).ConfigureAwait(false);
                if (entries.Count > 0)
                    return (url, entries);
            }

            return (endpoint + s_openAiModelPaths[0], []);
        }

        private static IEnumerable<string> GetOpenAiModelUrls(string endpoint)
        {
            foreach (var path in s_openAiModelPaths)
                yield return endpoint + path;
        }

        private static async Task<List<ModelEntry>> GetOpenAiModelEntriesFromUrlAsync(HttpClient http, string url, CancellationToken cancellationToken)
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
            var payload = BuildModelRequestJson(model);
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

                var (url, modelEntries) = await GetOpenAiModelEntriesAsync(http, endpoint, waitCts.Token).ConfigureAwait(false);
                var modelEntry = modelEntries.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
                if (modelEntry == null)
                {
                    DebugLog($"WaitForModelLoaded stop: model={model} disappeared from {url}");
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

        private static async Task<int?> TryGetTotalSlotsAsync(HttpClient http, string url, CancellationToken cancellationToken)
        {
            using var doc = await TryGetJsonAsync(http, url, cancellationToken).ConfigureAwait(false);
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                DebugLog($"TryGetTotalSlots: url={url}, no object response");
                return null;
            }

            if (!doc.RootElement.TryGetProperty("total_slots", out var totalSlotsElement)
                || totalSlotsElement.ValueKind != JsonValueKind.Number
                || !totalSlotsElement.TryGetInt32(out int totalSlots)
                || totalSlots <= 0)
            {
                DebugLog($"TryGetTotalSlots: url={url}, total_slots missing or invalid");
                return null;
            }

            DebugLog($"TryGetTotalSlots: url={url}, total_slots={totalSlots}");
            return totalSlots;
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

        internal static string BuildModelRequestJson(string model)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static LlmApiFlavor RememberFlavor(string endpoint, LlmApiFlavor flavor)
        {
            s_flavorCache[endpoint] = flavor;
            DebugLog($"DetectApiFlavor resolved: endpoint={endpoint}, flavor={flavor}");
            return flavor;
        }

        private static string BuildSlotManagementKey(string endpoint, string? model)
            => NormalizeEndpoint(endpoint) + "|model=" + (model?.Trim() ?? string.Empty);

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
