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
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/interactions");
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Content = new StringContent(
                BuildInteractionRequestJson(model, turns, systemInstruction, maxOutputTokens, thinkingLevel),
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

        private static string BuildInteractionRequestJson(
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
                writer.WriteString("model", model);
                writer.WriteBoolean("store", false);

                if (!string.IsNullOrWhiteSpace(systemInstruction))
                    writer.WriteString("system_instruction", systemInstruction);

                writer.WritePropertyName("input");
                writer.WriteStartArray();
                foreach (var turn in turns)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", turn.Role);
                    writer.WriteString("content", turn.Content);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                if (maxOutputTokens.HasValue || !string.IsNullOrWhiteSpace(thinkingLevel))
                {
                    writer.WritePropertyName("generation_config");
                    writer.WriteStartObject();
                    if (maxOutputTokens.HasValue)
                        writer.WriteNumber("max_output_tokens", maxOutputTokens.Value);
                    if (!string.IsNullOrWhiteSpace(thinkingLevel))
                        writer.WriteString("thinking_level", thinkingLevel);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string ExtractOutputText(string json)
        {
            using var doc = JsonDocument.Parse(json);
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
