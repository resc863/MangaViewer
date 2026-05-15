using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace MangaViewer.Services
{
    public sealed class AnthropicChatClient : ChatClientBase
    {
        private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";
        private const int DefaultMaxOutputTokens = 4096;
        private static readonly HttpClient s_httpClient = new();

        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _thinkingLevel;

        public AnthropicChatClient(string apiKey, string model, string thinkingLevel = "Off")
            : base("Anthropic")
        {
            _apiKey = apiKey;
            _model = model;
            _thinkingLevel = thinkingLevel;
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prepared = PrepareMessages(chatMessages);
            int? thinkingBudgetTokens = ThinkingLevelHelper.IsOff(_thinkingLevel)
                ? null
                : ThinkingLevelHelper.GetAnthropicBudgetTokens(_thinkingLevel);

            int maxOutputTokens = options?.MaxOutputTokens ?? DefaultMaxOutputTokens;
            if (thinkingBudgetTokens.HasValue)
                maxOutputTokens = Math.Max(maxOutputTokens, thinkingBudgetTokens.Value + DefaultMaxOutputTokens);

            using var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint);
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
            request.Content = new StringContent(
                BuildRequestJson(_model, prepared.Messages, prepared.SystemPrompt, maxOutputTokens, thinkingBudgetTokens),
                new UTF8Encoding(false),
                "application/json");

            using var response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ExtractErrorMessage(json) ?? $"Anthropic API request failed with status {(int)response.StatusCode}.");

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, ExtractText(json)));
        }

        private static (string? SystemPrompt, IReadOnlyList<AnthropicMessage> Messages) PrepareMessages(IEnumerable<ChatMessage> chatMessages)
        {
            string? systemPrompt = null;
            var messages = new List<AnthropicMessage>();

            foreach (var message in chatMessages)
            {
                string text = message.Text ?? string.Empty;
                if (message.Role == ChatRole.System)
                {
                    systemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                        ? text
                        : string.Join(Environment.NewLine + Environment.NewLine, systemPrompt, text);
                    continue;
                }

                string role = message.Role == ChatRole.Assistant ? "assistant" : "user";
                messages.Add(new AnthropicMessage(role, text));
            }

            if (messages.Count == 0)
                messages.Add(new AnthropicMessage("user", string.Empty));

            return (systemPrompt, messages);
        }

        private static string BuildRequestJson(
            string model,
            IReadOnlyList<AnthropicMessage> messages,
            string? systemPrompt,
            int maxOutputTokens,
            int? thinkingBudgetTokens)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteNumber("max_tokens", maxOutputTokens);

                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    writer.WriteString("system", systemPrompt);

                if (thinkingBudgetTokens.HasValue)
                {
                    writer.WritePropertyName("thinking");
                    writer.WriteStartObject();
                    writer.WriteString("type", "enabled");
                    writer.WriteNumber("budget_tokens", thinkingBudgetTokens.Value);
                    writer.WriteEndObject();
                }

                writer.WritePropertyName("messages");
                writer.WriteStartArray();
                foreach (var message in messages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", message.Role);
                    writer.WriteString("content", message.Content);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string ExtractText(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var textBlocks = new List<string>();
            foreach (var block in contentElement.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || !block.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String
                    || !string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                    || !block.TryGetProperty("text", out var textElement)
                    || textElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? text = textElement.GetString();
                if (!string.IsNullOrEmpty(text))
                    textBlocks.Add(text);
            }

            return string.Join(Environment.NewLine, textBlocks);
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

        private readonly record struct AnthropicMessage(string Role, string Content);
    }
}
