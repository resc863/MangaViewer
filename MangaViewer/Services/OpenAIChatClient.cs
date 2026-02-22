using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MangaViewer.Services
{
    public class OpenAIChatClient : IChatClient
    {
        private readonly IChatClient _innerClient;

        public OpenAIChatClient(string apiKey, string model, string endpoint = "https://api.openai.com/v1/")
        {
            var credential = new ApiKeyCredential(apiKey);
            var openAIClient = endpoint.TrimEnd('/') == "https://api.openai.com/v1"
                ? new OpenAIClient(credential)
                : new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            _innerClient = openAIClient.GetChatClient(model).AsIChatClient();
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

        public ChatClientMetadata Metadata => new ChatClientMetadata("OpenAI");

        public object? GetService(Type serviceType, object? serviceKey = null)
            => _innerClient.GetService(serviceType, serviceKey);
    }
}
