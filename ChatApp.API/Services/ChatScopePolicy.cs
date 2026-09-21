using Microsoft.Extensions.Configuration;

namespace ChatApp.API.Services
{
    public record ScopeValidationResult(
        bool IsBlocked,
        string? RefusalMessage,
        string Intent,
        float Confidence,
        string? Reason);

    public interface IChatScopePolicy
    {
        float OutOfScopeThreshold { get; }
        ScopeValidationResult Evaluate(IntentResult? intentResult, string? displayName = null, string? userMessage = null);
        bool IsConversationMetaQuery(string? userMessage);
    }

    public class ChatScopePolicy : IChatScopePolicy
    {
        private const float DefaultThreshold = 0.80f;
        public float OutOfScopeThreshold { get; }

        public ChatScopePolicy(IConfiguration configuration)
        {
            var configuredThreshold = configuration.GetValue<float?>("AI:OutOfScopeThreshold");
            OutOfScopeThreshold = configuredThreshold ?? DefaultThreshold;
        }

        public bool IsConversationMetaQuery(string? userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return false;
            var text = userMessage.Trim().ToLowerInvariant();

            return text.Contains("first question")
                || text.Contains("previous question")
                || text.Contains("last question")
                || text.Contains("earlier question")
                || text.Contains("prior question")
                || text.Contains("what did i ask")
                || text.Contains("what did i say")
                || text.Contains("what did you say")
                || text.Contains("what you said")
                || text.Contains("what was my question")
                || text.Contains("what were my questions")
                || text.Contains("when was this question asked")
                || text.Contains("when was that asked")
                || text.Contains("when did i ask")
                || text.Contains("what did i ask at")
                || text.Contains("what did we discuss")
                || text.Contains("what did we talk about")
                || text.Contains("summarize our chat")
                || text.Contains("summarize our conversation")
                || text.Contains("remember me")
                || text.Contains("remember our")
                || text.Contains("remember what")
                || text.Contains("chatting earlier")
                || text.Contains("chatted earlier")
                || text.Contains("spoke earlier")
                || text.Contains("talked earlier")
                || text.Contains("repeat")
                || text.Contains("who are you")
                || text.Contains("who am i talking to");
        }


        public ScopeValidationResult Evaluate(IntentResult? intentResult, string? displayName = null, string? userMessage = null)
        {
            if (intentResult == null)
            {
                return new ScopeValidationResult(
                    IsBlocked: false,
                    RefusalMessage: null,
                    Intent: "ClassificationUnavailable",
                    Confidence: 0.0f,
                    Reason: "Intent result was null.");
            }

            // In-session conversational recall queries are always in-scope
            if (IsConversationMetaQuery(userMessage))
            {
                return new ScopeValidationResult(
                    IsBlocked: false,
                    RefusalMessage: null,
                    Intent: "FAQ",
                    Confidence: intentResult.Confidence,
                    Reason: "Conversation meta/recall query is in-scope.");
            }

            var intent = intentResult.Intent ?? "ClassificationUnavailable";
            var confidence = intentResult.Confidence;

            if (string.Equals(intent, "OutOfScope", System.StringComparison.OrdinalIgnoreCase)
                && confidence >= OutOfScopeThreshold)
            {
                var greeting = !string.IsNullOrWhiteSpace(displayName)
                    ? $"Hello {displayName}! "
                    : string.Empty;

                var refusal = $"{greeting}I am the ChatApp customer support assistant. I can only assist with ChatApp-related questions such as messaging features, accounts, passwords, billing, and technical support. Please let me know how I can help you with ChatApp!";

                return new ScopeValidationResult(
                    IsBlocked: true,
                    RefusalMessage: refusal,
                    Intent: intent,
                    Confidence: confidence,
                    Reason: $"Deterministic guard triggered: Intent={intent}, Confidence={confidence:F2} >= Threshold={OutOfScopeThreshold:F2}");
            }

            return new ScopeValidationResult(
                IsBlocked: false,
                RefusalMessage: null,
                Intent: intent,
                Confidence: confidence,
                Reason: "Request allowed.");
        }
    }

}
