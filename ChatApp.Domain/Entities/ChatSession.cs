using System;
using System.Collections.Generic;

namespace ChatApp.Domain.Entities
{
    public class ChatSession
    {
        public Guid Id { get; set; }
        public int CustomerId { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// JSON-serialized key-value working memory extracted during the session
        /// (e.g. user name, reported issue). Persists across messages.
        /// </summary>
        public string? MemoryJson { get; set; }

        /// <summary>
        /// AI-generated summary of conversation history. Replaces raw message history
        /// in the prompt once message count exceeds the summarization threshold.
        /// </summary>
        public string? HistorySummary { get; set; }

        public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
    }
}
