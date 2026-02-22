using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Microsoft.Extensions.AI;

namespace MangaViewer.Services
{
    public class AnthropicChatClient : IChatClient
    {
        private readonly IChatClient _innerClient;

        public AnthropicChatClient(string apiKey, string model)
        {
            var client = new AnthropicClient() { ApiKey = apiKey };
            _innerClient = client.AsIChatClient(model);
        }

        public void Dispose() => _innerClient.Dispose();

        public Task<ChatResponse> GetResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _innerClient.GetResponseAsync(chatMessages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IList<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetStreamingResponseAsync((IEnumerable<ChatMessage>)chatMessages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _innerClient.GetStreamingResponseAsync(chatMessages, options, cancellationToken);

        public ChatClientMetadata Metadata => new ChatClientMetadata("Anthropic");

        public object? GetService(Type serviceType, object? serviceKey = null)
            => _innerClient.GetService(serviceType, serviceKey);
    }
}
