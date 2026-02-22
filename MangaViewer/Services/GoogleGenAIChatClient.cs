using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using SystemType = System.Type;

namespace MangaViewer.Services
{
    public class GoogleGenAIChatClient : IChatClient
    {
        private readonly Client _client;
        private readonly string _model;

        public GoogleGenAIChatClient(string apiKey, string model)
        {
            _client = new Client(apiKey: apiKey);
            _model = model;
        }

        public void Dispose() { }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            string prompt = BuildPrompt(chatMessages);

            var config = new GenerateContentConfig();
            if (options?.MaxOutputTokens.HasValue == true)
                config.MaxOutputTokens = options.MaxOutputTokens.Value;

            var response = await _client.Models.GenerateContentAsync(
                model: _model,
                contents: prompt,
                config: config
            );

            var text = response?.Candidates?[0]?.Content?.Parts?[0]?.Text ?? string.Empty;
            return new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, text));
        }

        public Task<ChatResponse> GetResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetStreamingResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public ChatClientMetadata Metadata => new ChatClientMetadata("Google");

        public object? GetService(SystemType serviceType, object? serviceKey = null) => null;

        private static string BuildPrompt(IEnumerable<ChatMessage> messages)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var msg in messages)
            {
                if (msg.Role == Microsoft.Extensions.AI.ChatRole.System)
                    sb.AppendLine($"[System]: {msg.Text}");
                else if (msg.Role == Microsoft.Extensions.AI.ChatRole.User)
                    sb.AppendLine(msg.Text);
                else
                    sb.AppendLine($"[Assistant]: {msg.Text}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
