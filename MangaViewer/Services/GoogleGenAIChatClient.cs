using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace MangaViewer.Services
{
    public sealed class GoogleGenAIChatClient : ChatClientBase
    {
        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _thinkingLevel;

        public GoogleGenAIChatClient(string apiKey, string model, string thinkingLevel = "Off")
            : base("Google")
        {
            _apiKey = apiKey;
            _model = model;
            _thinkingLevel = thinkingLevel;
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prepared = BuildTurns(chatMessages);
            var text = await GoogleGeminiRestApi.CreateTextInteractionAsync(
                _apiKey,
                _model,
                prepared.Turns,
                prepared.SystemInstruction,
                options?.MaxOutputTokens,
                ThinkingLevelHelper.NormalizeGoogle(_thinkingLevel),
                cancellationToken).ConfigureAwait(false);

            return new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, text));
        }

        private static (string? SystemInstruction, IReadOnlyList<GeminiInteractionTurn> Turns) BuildTurns(IEnumerable<ChatMessage> messages)
        {
            string? systemInstruction = null;
            var turns = new List<GeminiInteractionTurn>();
            foreach (var msg in messages)
            {
                if (msg.Role == Microsoft.Extensions.AI.ChatRole.System)
                {
                    string text = msg.Text ?? string.Empty;
                    systemInstruction = string.IsNullOrWhiteSpace(systemInstruction)
                        ? text
                        : string.Join(Environment.NewLine + Environment.NewLine, systemInstruction, text);
                }
                else if (msg.Role == Microsoft.Extensions.AI.ChatRole.User)
                {
                    turns.Add(new GeminiInteractionTurn("user", msg.Text ?? string.Empty));
                }
                else
                {
                    turns.Add(new GeminiInteractionTurn("model", msg.Text ?? string.Empty));
                }
            }

            if (turns.Count == 0)
                turns.Add(new GeminiInteractionTurn("user", string.Empty));

            return (systemInstruction, turns.ToList());
        }
    }
}
