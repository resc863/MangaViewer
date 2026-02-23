using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using SystemType = System.Type;

namespace MangaViewer.Services
{
    public class AnthropicChatClient : IChatClient
    {
        private readonly AnthropicClient _client;
        private readonly IChatClient _innerClient;
        private readonly string _model;
        private readonly string _thinkingLevel;

        public AnthropicChatClient(string apiKey, string model, string thinkingLevel = "Off")
        {
            _client = new AnthropicClient() { ApiKey = apiKey };
            _innerClient = _client.AsIChatClient(model);
            _model = model;
            _thinkingLevel = thinkingLevel;
        }

        public void Dispose() => _innerClient.Dispose();

        public Task<ChatResponse> GetResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (_thinkingLevel is not "Off")
            {
                int budgetTokens = _thinkingLevel switch
                {
                    "Minimal" or "Low" => 1024,
                    "Medium" => 5000,
                    "High" => 10000,
                    _ => 1024
                };

                var msgs = chatMessages.Select(m => new MessageParam
                {
                    Role = m.Role == Microsoft.Extensions.AI.ChatRole.User ? Role.User : Role.Assistant,
                    Content = m.Text ?? ""
                }).ToList();

                var parameters = new MessageCreateParams
                {
                    MaxTokens = budgetTokens + 4096,
                    Messages = msgs,
                    Model = _model,
                    Thinking = new ThinkingConfigEnabled { BudgetTokens = budgetTokens },
                };

                var message = await _client.Messages.Create(parameters);
                var textBlock = message.Content?.FirstOrDefault(b => b.TryPickText(out _));
                string text = "";
                if (textBlock is not null && textBlock.TryPickText(out var tb))
                    text = tb.Text ?? "";
                return new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, text));
            }

            return await _innerClient.GetResponseAsync(chatMessages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetStreamingResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _innerClient.GetStreamingResponseAsync(chatMessages, options, cancellationToken);

        public ChatClientMetadata Metadata => new ChatClientMetadata("Anthropic");

        public object? GetService(SystemType serviceType, object? serviceKey = null)
            => _innerClient.GetService(serviceType, serviceKey);
    }
}
