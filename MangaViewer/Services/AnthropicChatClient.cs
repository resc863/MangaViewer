using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace MangaViewer.Services
{
    public sealed class AnthropicChatClient : DelegatingChatClientBase
    {
        private readonly AnthropicClient _client;
        private readonly string _model;
        private readonly string _thinkingLevel;

        public AnthropicChatClient(string apiKey, string model, string thinkingLevel = "Off")
            : this(CreateClients(apiKey, model), model, thinkingLevel)
        {
        }

        private AnthropicChatClient((AnthropicClient Client, IChatClient InnerClient) clients, string model, string thinkingLevel)
            : base("Anthropic", clients.InnerClient)
        {
            _client = clients.Client;
            _model = model;
            _thinkingLevel = thinkingLevel;
        }

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

            return await GetInnerResponseAsync(chatMessages, options, cancellationToken);
        }

        private static (AnthropicClient Client, IChatClient InnerClient) CreateClients(string apiKey, string model)
        {
            var client = new AnthropicClient() { ApiKey = apiKey };
            return (client, client.AsIChatClient(model));
        }
    }
}
