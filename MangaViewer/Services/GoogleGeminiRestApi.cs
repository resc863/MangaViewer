using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    internal readonly record struct GeminiInteractionTurn(string Role, string Content);

    internal static class GoogleGeminiRestApi
    {
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
        private static readonly HttpClient s_httpClient = new();

        public static async Task<string> CreateTextInteractionAsync(
            string apiKey,
            string model,
            IReadOnlyList<GeminiInteractionTurn> turns,
            string? systemInstruction,
            int? maxOutputTokens,
            string? thinkingLevel,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildGenerateContentUrl(model));
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Content = new StringContent(
                BuildGenerateContentRequestJson(model, turns, systemInstruction, maxOutputTokens, thinkingLevel),
                Encoding.UTF8,
                "application/json");

            using var response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ExtractErrorMessage(json) ?? $"Google Gemini API request failed with status {(int)response.StatusCode}.");

            return ExtractOutputText(json);
        }

        public static async Task<List<string>> ListModelIdsAsync(string apiKey, CancellationToken cancellationToken)
        {
            var modelIds = new List<string>();
            string? nextPageToken = null;

            do
            {
                string url = BaseUrl + "/models?pageSize=1000";
                if (!string.IsNullOrWhiteSpace(nextPageToken))
                    url += "&pageToken=" + Uri.EscapeDataString(nextPageToken);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-goog-api-key", apiKey);

                using var response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(ExtractErrorMessage(json) ?? $"Google Gemini API model listing failed with status {(int)response.StatusCode}.");

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var modelElement in modelsElement.EnumerateArray())
                    {
                        if (modelElement.ValueKind == JsonValueKind.Object
                            && modelElement.TryGetProperty("name", out var nameElement)
                            && nameElement.ValueKind == JsonValueKind.String)
                        {
                            string? name = nameElement.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                                modelIds.Add(name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name[7..] : name);
                        }
                    }
                }

                nextPageToken = root.TryGetProperty("nextPageToken", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
                    ? tokenElement.GetString()
                    : null;
            }
            while (!string.IsNullOrWhiteSpace(nextPageToken));

            return modelIds;
        }

        private static string BuildGenerateContentUrl(string model)
        {
            string normalizedModel = string.IsNullOrWhiteSpace(model) ? "gemini-3-flash-preview" : model.Trim();
            if (normalizedModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                normalizedModel = normalizedModel[7..];

            return BaseUrl + "/models/" + Uri.EscapeDataString(normalizedModel) + ":generateContent";
        }

        private static string BuildGenerateContentRequestJson(
            string model,
            IReadOnlyList<GeminiInteractionTurn> turns,
            string? systemInstruction,
            int? maxOutputTokens,
            string? thinkingLevel)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                if (!string.IsNullOrWhiteSpace(systemInstruction))
                {
                    writer.WritePropertyName("systemInstruction");
                    writer.WriteStartObject();
                    writer.WritePropertyName("parts");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("text", systemInstruction);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WritePropertyName("contents");
                writer.WriteStartArray();
                foreach (var turn in turns)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", turn.Role);
                    writer.WritePropertyName("parts");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("text", turn.Content);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                if (maxOutputTokens.HasValue || !string.IsNullOrWhiteSpace(thinkingLevel))
                {
                    writer.WritePropertyName("generationConfig");
                    writer.WriteStartObject();
                    if (maxOutputTokens.HasValue)
                        writer.WriteNumber("maxOutputTokens", maxOutputTokens.Value);
                    if (!string.IsNullOrWhiteSpace(thinkingLevel))
                    {
                        writer.WritePropertyName("thinkingConfig");
                        writer.WriteStartObject();
                        if (IsGemini25Model(model))
                            writer.WriteNumber("thinkingBudget", GetThinkingBudget(thinkingLevel));
                        else
                            writer.WriteString("thinkingLevel", thinkingLevel);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static bool IsGemini25Model(string? model)
            => model?.Contains("2.5", StringComparison.OrdinalIgnoreCase) == true;

        private static int GetThinkingBudget(string? thinkingLevel)
        {
            return thinkingLevel switch
            {
                "minimal" => 128,
                "low" => 1024,
                "medium" => 8192,
                "high" => 24576,
                _ => 0
            };
        }

        private static string ExtractOutputText(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (TryExtractGenerateContentText(doc.RootElement, out string generateContentText))
                return generateContentText;

            if (!doc.RootElement.TryGetProperty("outputs", out var outputsElement) || outputsElement.ValueKind != JsonValueKind.Array)
                return string.Empty;

            string fallback = string.Empty;
            foreach (var output in outputsElement.EnumerateArray())
            {
                if (output.ValueKind != JsonValueKind.Object)
                    continue;

                if (output.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    string text = textElement.GetString() ?? string.Empty;
                    if (output.TryGetProperty("type", out var typeElement)
                        && typeElement.ValueKind == JsonValueKind.String
                        && string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                    {
                        fallback = text;
                    }
                    else if (string.IsNullOrEmpty(fallback))
                    {
                        fallback = text;
                    }
                }
            }

            return fallback;
        }

        private static bool TryExtractGenerateContentText(JsonElement root, out string text)
        {
            text = string.Empty;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("candidates", out var candidatesElement)
                || candidatesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var candidate in candidatesElement.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object
                    || !candidate.TryGetProperty("content", out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.Object
                    || !contentElement.TryGetProperty("parts", out var partsElement)
                    || partsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var parts = new List<string>();
                foreach (var part in partsElement.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(textElement.GetString() ?? string.Empty);
                    }
                }

                text = string.Join(string.Empty, parts).Trim();
                return true;
            }

            return true;
        }

        private static string? ExtractErrorMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var errorElement)
                    && errorElement.ValueKind == JsonValueKind.Object
                    && errorElement.TryGetProperty("message", out var messageElement)
                    && messageElement.ValueKind == JsonValueKind.String)
                {
                    return messageElement.GetString();
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
