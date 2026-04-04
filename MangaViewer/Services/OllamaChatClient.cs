using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace MangaViewer.Services
{
    public sealed class OllamaChatClient : IChatClient
    {
        private static readonly HttpClient s_httpClient = new();
        private static readonly TimeSpan BaseRequestTimeout = TimeSpan.FromSeconds(180);
        private static readonly TimeSpan ThinkingRequestTimeout = TimeSpan.FromMinutes(10);
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly string _endpoint;
        private readonly string _model;
        private readonly string _thinkingLevel;

        public OllamaChatClient(string endpoint, string model, string thinkingLevel = "Off")
        {
            _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint.TrimEnd('/');
            _model = model;
            _thinkingLevel = NormalizeThinkingLevel(thinkingLevel);
        }

        public OllamaChatClient(string endpoint, string model, bool enableThinking)
            : this(endpoint, model, enableThinking ? "On" : "Off")
        {
        }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var messages = new List<object>();
            foreach (var msg in chatMessages)
            {
                messages.Add(new
                {
                    role = msg.Role == ChatRole.System ? "system" : msg.Role == ChatRole.Assistant ? "assistant" : "user",
                    content = msg.Text ?? string.Empty,
                });
            }

            var payload = new Dictionary<string, object?>
            {
                ["model"] = _model,
                ["stream"] = false,
                ["messages"] = messages,
            };

            payload["think"] = BuildThinkParameter(_thinkingLevel);

            string requestJson = JsonSerializer.Serialize(payload, s_jsonOptions);

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/chat")
            {
                Content = new StringContent(requestJson, new UTF8Encoding(false), "application/json")
            };

            bool thinkingEnabled = BuildThinkParameter(_thinkingLevel);
            using var requestLease = OllamaRequestLoadCoordinator.Acquire(
                thinkingEnabled ? ThinkingRequestTimeout : BaseRequestTimeout,
                thinkingEnabled);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestLease.EffectiveTimeout);

            using var response = await s_httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            string text = string.Empty;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.Object
                && messageElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                text = contentElement.GetString() ?? string.Empty;
            }
            else if (root.TryGetProperty("response", out var responseElement) && responseElement.ValueKind == JsonValueKind.String)
            {
                text = responseElement.GetString() ?? string.Empty;
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        }

        private static string NormalizeThinkingLevel(string? thinkingLevel)
        {
            if (string.IsNullOrWhiteSpace(thinkingLevel)) return "Off";
            if (thinkingLevel.Equals("Off", StringComparison.OrdinalIgnoreCase)
                || thinkingLevel.Equals("False", StringComparison.OrdinalIgnoreCase)
                || thinkingLevel.Equals("0", StringComparison.OrdinalIgnoreCase))
                return "Off";
            return "On";
        }

        private static bool BuildThinkParameter(string thinkingLevel)
        {
            return !NormalizeThinkingLevel(thinkingLevel).Equals("Off", StringComparison.OrdinalIgnoreCase);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetStreamingResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ChatClientMetadata Metadata => new("Ollama");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
