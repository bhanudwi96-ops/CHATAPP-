using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Services
{
    /// <summary>
    /// Result of intent classification from IntentService.
    /// </summary>
    public record IntentResult(
        string Intent,          // FAQ | CreateTicket | EscalateToHuman | OutOfScope
        float Confidence,       // 0.0 – 1.0
        string? Reasoning       // brief explanation for audit log
    );

    public interface IIntentService
    {
        Task<IntentResult> ClassifyAsync(string userMessage, string? conversationContext = null, System.Threading.CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Lightweight Gemini router that classifies user intent before the full agent runs.
    /// Uses a cheap, fast model (configured under AI:RouterModel) for low latency.
    /// Intent definitions are config-driven — no hardcoded strings for new intent types.
    /// </summary>
    public class IntentService : IIntentService
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _configuration;
        private readonly ILogger<IntentService> _logger;

        // Config keys — all intent labels and the router model come from appsettings.json
        private const string RouterModelKey = "AI:RouterModel";
        private const string RouterModelDefault = "gemini-3.5-flash-lite";

        public IntentService(
            IConfiguration configuration,
            ILogger<IntentService> logger,
            HttpClient? httpClient = null)
        {
            _configuration = configuration;
            _logger = logger;
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public async Task<IntentResult> ClassifyAsync(string userMessage, string? conversationContext = null, System.Threading.CancellationToken cancellationToken = default)
        {
            // Load valid intents from config so new intents need no code change
            var validIntents = _configuration.GetSection("AI:Intents").Get<string[]>()
                ?? new[] { "FAQ", "CreateTicket", "EscalateToHuman", "OutOfScope" };

            var intentsFormatted = string.Join(", ", validIntents);

            var systemPrompt =
                $"You are a precise intent classifier for a customer support chatbot. " +
                $"Classify the user message into exactly ONE of these intents: {intentsFormatted}.\n\n" +
                $"Intent definitions:\n" +
                $"- FAQ: User is asking a how-to question, seeking information, troubleshooting steps, greetings, bot identity, OR asking about the conversation itself (such as recalling prior questions, 'what was my first question?', 'what did I ask earlier?', 'what did you say?').\n" +
                $"- CreateTicket: User describes a specific bug, error, billing problem, or complaint that requires a human to follow up.\n" +
                $"- EscalateToHuman: User is visibly frustrated, angry, distressed, or is explicitly asking to speak to a human agent.\n" +
                $"- OutOfScope: User is asking about external general knowledge completely unrelated to ChatApp or this chat session (celebrities, sports, history, general programming tutorials, weather, other apps). NOTE: Questions about this conversation or previous messages are IN-SCOPE conversation queries and must NEVER be classified as OutOfScope.\n\n" +
                $"Respond ONLY with valid JSON in this exact format (no markdown, no extra text):\n" +
                $"{{\"intent\": \"FAQ\", \"confidence\": 0.95, \"reasoning\": \"User is asking how to create a group\"}}";

            var contextNote = !string.IsNullOrWhiteSpace(conversationContext)
                ? $"\n\nRecent conversation context: {conversationContext}"
                : string.Empty;

            var userContent = $"User message: \"{userMessage}\"{contextNote}";

            var apiKey = _configuration["GEMINI_API_KEY"]
                         ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                         ?? _configuration["Gemini:ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_GEMINI", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("GEMINI_API_KEY not configured — IntentService fallback to ClassificationUnavailable.");
                return new IntentResult("ClassificationUnavailable", 0.0f, "API key not configured");
            }

            var model = _configuration[RouterModelKey] ?? RouterModelDefault;
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

            var payload = new JsonObject
            {
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
                },
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = userContent } }
                    }
                },
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = 0.0,      // deterministic classification
                    ["maxOutputTokens"] = 100,  // we only need a tiny JSON blob
                    ["responseMimeType"] = "application/json"
                }
            };

            try
            {
                using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                using var httpContent = new StringContent(
                    payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                    Encoding.UTF8, "application/json");

                var response = await _http.PostAsync(endpoint, httpContent, linkedCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Intent router returned {Status} — fallback to ClassificationUnavailable.", (int)response.StatusCode);
                    return new IntentResult("ClassificationUnavailable", 0.0f, $"Router API error {(int)response.StatusCode}");
                }

                var responseText = await response.Content.ReadAsStringAsync(linkedCts.Token);
                var root = JsonNode.Parse(responseText);
                var rawJson = root?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();

                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    _logger.LogWarning("Intent router returned empty text — fallback to ClassificationUnavailable.");
                    return new IntentResult("ClassificationUnavailable", 0.0f, "Empty router response");
                }

                // Strip markdown code fences if model wrapped the JSON anyway
                rawJson = rawJson.Trim().TrimStart('`').TrimEnd('`');
                if (rawJson.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    rawJson = rawJson[4..].Trim();

                var parsed = JsonNode.Parse(rawJson);
                var intent = parsed?["intent"]?.ToString() ?? "ClassificationUnavailable";
                var confidence = parsed?["confidence"]?.GetValue<float>() ?? 0.5f;
                var reasoning = parsed?["reasoning"]?.ToString();

                // Validate intent is one of the configured list
                if (!Array.Exists(validIntents, i => string.Equals(i, intent, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Intent router returned unknown intent '{Intent}' — fallback to ClassificationUnavailable.", intent);
                    return new IntentResult("ClassificationUnavailable", confidence, $"Unknown intent '{intent}'");
                }

                _logger.LogInformation("Intent classified as '{Intent}' (confidence {Confidence:P0}) — {Reasoning}",
                    intent, confidence, reasoning ?? "no reasoning");

                return new IntentResult(intent, confidence, reasoning);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Intent router timed out or was cancelled — fallback to ClassificationUnavailable.");
                return new IntentResult("ClassificationUnavailable", 0.0f, "Router timed out or was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Intent router threw unexpectedly — fallback to ClassificationUnavailable.");
                return new IntentResult("ClassificationUnavailable", 0.0f, $"Router exception: {ex.Message}");
            }
        }
    }
}
