using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;

namespace MangaViewer.Services
{
    public class GoogleGenAIChatClient : ChatClientBase
    {
        private readonly Client _client;
        private readonly string _model;
        private readonly string _thinkingLevel;

        public GoogleGenAIChatClient(string apiKey, string model, string thinkingLevel = "Off")
            : base("Google")
        {
            _client = new Client(apiKey: apiKey);
            _model = model;
            _thinkingLevel = thinkingLevel;
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            string prompt = BuildPrompt(chatMessages);

            var config = new GenerateContentConfig();
            if (options?.MaxOutputTokens.HasValue == true)
                config.MaxOutputTokens = options.MaxOutputTokens.Value;

            config.ThinkingConfig = _thinkingLevel switch
            {
                "Minimal" => new ThinkingConfig { ThinkingBudget = 128 },
                "Low" => new ThinkingConfig { ThinkingBudget = 1024 },
                "Medium" => new ThinkingConfig { ThinkingBudget = 8192 },
                "High" => new ThinkingConfig { ThinkingBudget = 24576 },
                _ => new ThinkingConfig { ThinkingBudget = 0 },
            };

            var response = await _client.Models.GenerateContentAsync(
                model: _model,
                contents: prompt,
                config: config
            );

            var text = response?.Candidates?[0]?.Content?.Parts?[0]?.Text ?? string.Empty;
            return new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, text));
        }

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
