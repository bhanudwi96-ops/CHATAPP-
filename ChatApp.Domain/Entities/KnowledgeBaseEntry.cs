namespace ChatApp.Domain.Entities
{
    public class KnowledgeBaseEntry
    {
        public int Id { get; set; }
        public string Topic { get; set; } = null!;
        public string Content { get; set; } = null!;

        /// <summary>
        /// JSON-serialized float[] vector from Gemini text-embedding-004.
        /// Null until first embedding run; cached to avoid re-embedding on restart.
        /// </summary>
        public string? EmbeddingJson { get; set; }
    }
}
