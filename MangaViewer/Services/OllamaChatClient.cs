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
    public sealed class OllamaChatClient : ChatClientBase
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
            : base("Ollama")
        {
            _endpoint = LlmEndpointCompatibility.NormalizeEndpoint(endpoint);
            _model = model;
            _thinkingLevel = ThinkingLevelHelper.NormalizeOllama(thinkingLevel);
        }

        public OllamaChatClient(string endpoint, string model, bool enableThinking)
            : this(endpoint, model, enableThinking ? "On" : "Off")
        {
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
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

            bool thinkingEnabled = BuildThinkParameter(_thinkingLevel);
            using var requestLease = OllamaRequestLoadCoordinator.Acquire(
                thinkingEnabled ? ThinkingRequestTimeout : BaseRequestTimeout,
                thinkingEnabled);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestLease.EffectiveTimeout);

            var flavor = await LlmEndpointCompatibility.DetectApiFlavorAsync(s_httpClient, _endpoint, timeoutCts.Token).ConfigureAwait(false);
            await LlmEndpointCompatibility.EnsureModelLoadedAsync(s_httpClient, _endpoint, _model, timeoutCts.Token).ConfigureAwait(false);
            using var request = BuildRequest(messages, thinkingEnabled, flavor);

            using var response = await s_httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            string text = string.Empty;
            using var doc = JsonDocument.Parse(json);
            text = LlmEndpointCompatibility.ExtractAssistantText(doc.RootElement);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        }

        private HttpRequestMessage BuildRequest(List<object> messages, bool thinkingEnabled, LlmApiFlavor flavor)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = _model,
                ["stream"] = false,
                ["messages"] = messages,
            };

            string path;
            if (flavor == LlmApiFlavor.OpenAiCompatible)
            {
                path = "/v1/chat/completions";
                LlmEndpointCompatibility.ApplyOpenAiThinkingOptions(payload, thinkingEnabled);
            }
            else
            {
                path = "/api/chat";
                payload["think"] = thinkingEnabled;
            }

            string requestJson = JsonSerializer.Serialize(payload, s_jsonOptions);
            return new HttpRequestMessage(HttpMethod.Post, _endpoint + path)
            {
                Content = new StringContent(requestJson, new UTF8Encoding(false), "application/json")
            };
        }

        private static bool BuildThinkParameter(string thinkingLevel)
        {
            return !ThinkingLevelHelper.NormalizeOllama(thinkingLevel).Equals("Off", StringComparison.OrdinalIgnoreCase);
        }
    }
}
