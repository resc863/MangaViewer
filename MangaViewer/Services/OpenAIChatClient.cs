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
    public class OpenAIChatClient : IChatClient
    {
        private readonly IChatClient _innerClient;
        private readonly OpenAI.Chat.ChatClient _chatClient;
        private readonly string _thinkingLevel;

        public OpenAIChatClient(string apiKey, string model, string endpoint = "https://api.openai.com/v1/", string thinkingLevel = "Off")
        {
            var credential = new ApiKeyCredential(apiKey);
            var openAIClient = endpoint.TrimEnd('/') == "https://api.openai.com/v1"
                ? new OpenAIClient(credential)
                : new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            _chatClient = openAIClient.GetChatClient(model);
            _innerClient = _chatClient.AsIChatClient();
            _thinkingLevel = thinkingLevel;
        }

        public void Dispose() => _innerClient.Dispose();

        public Task<ChatResponse> GetResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (_thinkingLevel is not "Off")
            {
                var msgs = chatMessages.Select(m =>
                {
                    if (m.Role == ChatRole.System)
                        return new SystemChatMessage(m.Text ?? "");
                    if (m.Role == ChatRole.Assistant)
                        return (OpenAI.Chat.ChatMessage)new AssistantChatMessage(m.Text ?? "");
                    return new UserChatMessage(m.Text ?? "");
                }).ToList();

                var chatOptions = new ChatCompletionOptions();
                chatOptions.ReasoningEffortLevel = _thinkingLevel switch
                {
                    "Minimal" or "Low" => ChatReasoningEffortLevel.Low,
                    "Medium" => ChatReasoningEffortLevel.Medium,
                    "High" => ChatReasoningEffortLevel.High,
                    _ => null
                };

                var result = await _chatClient.CompleteChatAsync(msgs, chatOptions, cancellationToken);
                var text = result.Value?.Content?.FirstOrDefault()?.Text ?? "";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
            }

            return await _innerClient.GetResponseAsync(chatMessages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetStreamingResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _innerClient.GetStreamingResponseAsync(chatMessages, options, cancellationToken);

        public ChatClientMetadata Metadata => new ChatClientMetadata("OpenAI");

        public object? GetService(Type serviceType, object? serviceKey = null)
            => _innerClient.GetService(serviceType, serviceKey);
    }
}
