using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace MangaViewer.Services
{
    public abstract class DelegatingChatClientBase : ChatClientBase
    {
        private readonly IChatClient _innerClient;

        protected DelegatingChatClientBase(string providerName, IChatClient innerClient)
            : base(providerName)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
        }

        protected IChatClient InnerClient => _innerClient;

        protected Task<ChatResponse> GetInnerResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _innerClient.GetResponseAsync(chatMessages, options, cancellationToken);

        public override void Dispose() => _innerClient.Dispose();

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _innerClient.GetStreamingResponseAsync(chatMessages, options, cancellationToken);

        public override object? GetService(Type serviceType, object? serviceKey = null)
            => _innerClient.GetService(serviceType, serviceKey);
    }
}
