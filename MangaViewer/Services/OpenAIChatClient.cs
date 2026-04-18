using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001

namespace MangaViewer.Services
{
    public sealed class OpenAIChatClient : DelegatingChatClientBase
    {
        private readonly OpenAI.Chat.ChatClient _chatClient;
        private readonly string _thinkingLevel;

        public OpenAIChatClient(string apiKey, string model, string endpoint = "https://api.openai.com/v1/", string thinkingLevel = "Off")
            : this(CreateClients(apiKey, model, endpoint), thinkingLevel)
        {
        }

        private OpenAIChatClient((OpenAI.Chat.ChatClient ChatClient, IChatClient InnerClient) clients, string thinkingLevel)
            : base("OpenAI", clients.InnerClient)
        {
            _chatClient = clients.ChatClient;
            _thinkingLevel = thinkingLevel;
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!ThinkingLevelHelper.IsOff(_thinkingLevel))
            {
                var msgs = chatMessages.Select(m =>
                {
                    if (m.Role == ChatRole.System)
                        return new SystemChatMessage(m.Text ?? "");
                    if (m.Role == ChatRole.Assistant)
                        return (OpenAI.Chat.ChatMessage)new AssistantChatMessage(m.Text ?? "");
                    return new UserChatMessage(m.Text ?? "");
                }).ToList();

                var chatOptions = new ChatCompletionOptions
                {
                    ReasoningEffortLevel = GetReasoningEffortLevel()
                };

                var result = await _chatClient.CompleteChatAsync(msgs, chatOptions, cancellationToken);
                var text = result.Value?.Content?.FirstOrDefault()?.Text ?? "";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
            }

            return await GetInnerResponseAsync(chatMessages, options, cancellationToken);
        }

        private static (OpenAI.Chat.ChatClient ChatClient, IChatClient InnerClient) CreateClients(string apiKey, string model, string endpoint)
        {
            var credential = new ApiKeyCredential(apiKey);
            var openAIClient = endpoint.TrimEnd('/') == "https://api.openai.com/v1"
                ? new OpenAIClient(credential)
                : new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            var chatClient = openAIClient.GetChatClient(model);
            return (chatClient, chatClient.AsIChatClient());
        }

        private ChatReasoningEffortLevel? GetReasoningEffortLevel()
        {
            return _thinkingLevel switch
            {
                "Minimal" or "Low" => ChatReasoningEffortLevel.Low,
                "Medium" => ChatReasoningEffortLevel.Medium,
                "High" => ChatReasoningEffortLevel.High,
                _ => null
            };
        }
    }
}
