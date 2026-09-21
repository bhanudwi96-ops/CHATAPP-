using System;

namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Records one AI orchestration run per user message.
    /// Powers the admin dashboard and the "Why did the AI do that?" transparency panel.
    /// </summary>
    public class AiAuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>The chat session this log entry belongs to.</summary>
        public Guid SessionId { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>Classified intent: FAQ | CreateTicket | EscalateToHuman | OutOfScope</summary>
        public string Intent { get; set; } = "FAQ";

        /// <summary>0.0–1.0 confidence score from the intent router.</summary>
        public float IntentConfidence { get; set; }

        /// <summary>JSON array of KB topic names that were injected into the prompt.</summary>
        public string? KbArticlesUsedJson { get; set; }

        /// <summary>Which Gemini model actually produced the final response.</summary>
        public string ModelUsed { get; set; } = string.Empty;

        /// <summary>Approximate input token count (from usageMetadata if available).</summary>
        public int InputTokens { get; set; }

        /// <summary>Approximate output token count (from usageMetadata if available).</summary>
        public int OutputTokens { get; set; }

        /// <summary>Wall-clock latency in milliseconds for the full orchestration pipeline.</summary>
        public int LatencyMs { get; set; }

        /// <summary>Ticket reference number (e.g. "TICK-42") if a ticket was created.</summary>
        public string? TicketRef { get; set; }

        /// <summary>
        /// Snapshot of the user message that triggered this orchestration run.
        /// Truncated to 500 chars to avoid unbounded storage.
        /// </summary>
        public string? UserMessageSnippet { get; set; }
    }
}
