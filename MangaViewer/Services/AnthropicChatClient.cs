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
    public class AnthropicChatClient : ChatClientBase
    {
        private readonly AnthropicClient _client;
        private readonly IChatClient _innerClient;
        private readonly string _model;
        private readonly string _thinkingLevel;

        public AnthropicChatClient(string apiKey, string model, string thinkingLevel = "Off")
            : base("Anthropic")
        {
            _client = new AnthropicClient() { ApiKey = apiKey };
            _innerClient = _client.AsIChatClient(model);
            _model = model;
            _thinkingLevel = thinkingLevel;
        }

        public override void Dispose() => _innerClient.Dispose();

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!ThinkingLevelHelper.IsOff(_thinkingLevel))
            {
                int budgetTokens = ThinkingLevelHelper.GetAnthropicBudgetTokens(_thinkingLevel);

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

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _innerClient.GetStreamingResponseAsync(chatMessages, options, cancellationToken);

        public override object? GetService(SystemType serviceType, object? serviceKey = null)
            => _innerClient.GetService(serviceType, serviceKey);
    }
}
