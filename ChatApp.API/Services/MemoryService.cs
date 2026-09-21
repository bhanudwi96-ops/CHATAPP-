using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Services
{
    /// <summary>
    /// Represents an excerpt retrieved from past conversation history beyond the active sliding window.
    /// </summary>
    public record HistoricalExcerpt(
        int TurnIndex,
        string Role,
        string Content,
        DateTime Timestamp,
        double Score
    );

    /// <summary>
    /// Represents a typed fact stored in working memory with conflict/invalidation tracking.
    /// </summary>
    public class FactRecord
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? SupersededValue { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Multi-tier enterprise session memory for AI conversations:
    /// Tier 1: Immediate verbatim sliding buffer (managed by controllers/GeminiService)
    /// Tier 2: Anchored rolling summary with milestone checkpoints (mitigates recursive summary drift)
    /// Tier 3: Historical dialogue retrieval (semantic/keyword recall over deep message history)
    /// Tier 4: Validated fact store with conflict resolution and invalidation
    /// </summary>
    public interface IMemoryService
    {
        Task<Dictionary<string, string>> LoadWorkingMemoryAsync(Guid sessionId);
        Task SaveWorkingMemoryAsync(Guid sessionId, Dictionary<string, string> facts);
        Task SaveFactAsync(Guid sessionId, string key, string value, string? category = null);
        Task ReconcileUserFactsAsync(Guid sessionId, string userMessage, string? assistantReply = null);
        Task<List<HistoricalExcerpt>> RetrievePastMessagesAsync(Guid sessionId, string query, int topK = 3, int excludeRecentCount = 20);
        Task<string?> LoadHistorySummaryAsync(Guid sessionId);
        Task<string> BuildMemoryContextAsync(Guid sessionId);
        Task MaybeSummarizeHistoryAsync(Guid sessionId, string apiKey, string model, List<ChatMessage>? messages = null);
    }

    public class MemoryService : IMemoryService
    {
        private readonly ChatDbContext _dbContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MemoryService> _logger;

        private const string ThresholdKey = "AI:HistorySummarizationThreshold";
        private const int ThresholdDefault = 12;

        public MemoryService(
            ChatDbContext dbContext,
            IConfiguration configuration,
            ILogger<MemoryService> logger)
        {
            _dbContext = dbContext;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<Dictionary<string, string>> LoadWorkingMemoryAsync(Guid sessionId)
        {
            var session = await _dbContext.ChatSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (string.IsNullOrWhiteSpace(session?.MemoryJson))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Try structured format first
                var structured = JsonSerializer.Deserialize<Dictionary<string, FactRecord>>(session.MemoryJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (structured != null && structured.Count > 0)
                {
                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, r) in structured)
                    {
                        result[k] = r.Value;
                    }
                    return result;
                }
            }
            catch { }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(session.MemoryJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize MemoryJson for session {SessionId} — starting fresh.", sessionId);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task SaveWorkingMemoryAsync(Guid sessionId, Dictionary<string, string> facts)
        {
            if (facts == null || facts.Count == 0) return;

            var session = await _dbContext.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
            {
                _logger.LogWarning("SaveWorkingMemory: Session {SessionId} not found.", sessionId);
                return;
            }

            var existingFacts = await LoadStructuredFactsAsync(session.MemoryJson);

            foreach (var (key, value) in facts)
            {
                if (existingFacts.TryGetValue(key, out var oldRecord))
                {
                    if (!string.Equals(oldRecord.Value, value, StringComparison.OrdinalIgnoreCase))
                    {
                        oldRecord.SupersededValue = oldRecord.Value;
                        oldRecord.Value = value;
                        oldRecord.UpdatedAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    existingFacts[key] = new FactRecord
                    {
                        Key = key,
                        Value = value,
                        UpdatedAt = DateTime.UtcNow
                    };
                }
            }

            session.MemoryJson = JsonSerializer.Serialize(existingFacts);
            await _dbContext.SaveChangesAsync();
        }

        public async Task SaveFactAsync(Guid sessionId, string key, string value, string? category = null)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return;

            var session = await _dbContext.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null) return;

            var existingFacts = await LoadStructuredFactsAsync(session.MemoryJson);

            if (existingFacts.TryGetValue(key, out var oldRecord))
            {
                if (!string.Equals(oldRecord.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("MemoryService: Invalidation on session {SessionId} — key '{Key}' updated from '{OldVal}' to '{NewVal}'",
                        sessionId, key, oldRecord.Value, value);
                    oldRecord.SupersededValue = oldRecord.Value;
                    oldRecord.Value = value;
                    oldRecord.Category = category ?? oldRecord.Category;
                    oldRecord.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                existingFacts[key] = new FactRecord
                {
                    Key = key,
                    Value = value,
                    Category = category,
                    UpdatedAt = DateTime.UtcNow
                };
            }

            session.MemoryJson = JsonSerializer.Serialize(existingFacts);
            await _dbContext.SaveChangesAsync();
        }

        public async Task ReconcileUserFactsAsync(Guid sessionId, string userMessage, string? assistantReply = null)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return;

            var factsToUpdate = new Dictionary<string, (string Value, string Category)>(StringComparer.OrdinalIgnoreCase);
            var lower = userMessage.ToLowerInvariant();

            // 1. Operating System detection & correction
            if (lower.Contains("windows 11") || lower.Contains("win 11"))
                factsToUpdate["os_platform"] = ("Windows 11", "environment");
            else if (lower.Contains("windows 10") || lower.Contains("win 10") || lower.Contains("windows"))
                factsToUpdate["os_platform"] = ("Windows", "environment");
            else if (lower.Contains("mac os") || lower.Contains("macos") || lower.Contains("macbook") || lower.Contains("mac"))
                factsToUpdate["os_platform"] = ("macOS", "environment");
            else if (lower.Contains("android"))
                factsToUpdate["os_platform"] = ("Android", "environment");
            else if (lower.Contains("ios") || lower.Contains("iphone") || lower.Contains("ipad"))
                factsToUpdate["os_platform"] = ("iOS", "environment");
            else if (lower.Contains("linux") || lower.Contains("ubuntu"))
                factsToUpdate["os_platform"] = ("Linux", "environment");

            // 2. Browser detection & correction
            if (lower.Contains("chrome"))
                factsToUpdate["browser"] = ("Google Chrome", "environment");
            else if (lower.Contains("firefox"))
                factsToUpdate["browser"] = ("Mozilla Firefox", "environment");
            else if (lower.Contains("safari"))
                factsToUpdate["browser"] = ("Apple Safari", "environment");
            else if (lower.Contains("edge"))
                factsToUpdate["browser"] = ("Microsoft Edge", "environment");

            // 3. Email pattern
            var emailMatch = System.Text.RegularExpressions.Regex.Match(userMessage, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
            if (emailMatch.Success && !emailMatch.Value.EndsWith("chatapp.com", StringComparison.OrdinalIgnoreCase))
            {
                factsToUpdate["contact_email"] = (emailMatch.Value, "identity");
            }

            // 4. Ticket reference or Account number pattern
            var ticketMatch = System.Text.RegularExpressions.Regex.Match(userMessage, @"(TICK-\d+|#\d{4,6})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (ticketMatch.Success)
            {
                // Store as unconfirmed - this is a raw text extraction, not a validated lookup.
                factsToUpdate["referenced_ticket_mentioned"] = (ticketMatch.Value, "support_unverified");
            }

            var accountMatch = System.Text.RegularExpressions.Regex.Match(userMessage, @"(ACC-\d+|ACCT-\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (accountMatch.Success)
            {
                factsToUpdate["account_number"] = (accountMatch.Value, "identity");
            }

            if (factsToUpdate.Count > 0)
            {
                var session = await _dbContext.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
                if (session == null) return;

                var existingFacts = await LoadStructuredFactsAsync(session.MemoryJson);

                foreach (var (key, (val, cat)) in factsToUpdate)
                {
                    if (existingFacts.TryGetValue(key, out var oldRecord))
                    {
                        if (!string.Equals(oldRecord.Value, val, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("Fact conflict resolved for session {SessionId}: '{Key}' changed from '{OldVal}' to '{NewVal}'",
                                sessionId, key, oldRecord.Value, val);

                            oldRecord.SupersededValue = oldRecord.Value;
                            oldRecord.Value = val;
                            oldRecord.Category = cat;
                            oldRecord.UpdatedAt = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        existingFacts[key] = new FactRecord
                        {
                            Key = key,
                            Value = val,
                            Category = cat,
                            UpdatedAt = DateTime.UtcNow
                        };
                    }
                }

                session.MemoryJson = JsonSerializer.Serialize(existingFacts);
                await _dbContext.SaveChangesAsync();
            }
        }

        public async Task<List<HistoricalExcerpt>> RetrievePastMessagesAsync(
            Guid sessionId, 
            string query, 
            int topK = 3, 
            int excludeRecentCount = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<HistoricalExcerpt>();

            var allMessages = await _dbContext.ChatMessages
                .Where(m => m.SessionId == sessionId)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

            if (allMessages.Count == 0)
                return new List<HistoricalExcerpt>();

            // Search across all messages in session so queries are never falsely rejected when
            // messages are within the active sliding window.
            var searchPool = allMessages;

            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "a", "an", "the", "and", "or", "but", "if", "in", "on", "at", "to", "for", "with",
                "is", "was", "are", "were", "what", "how", "did", "i", "you", "my", "your", "earlier",
                "before", "say", "said", "tell", "me", "please", "can", "check", "know"
            };

            var queryTokens = query
                .Split(new[] { ' ', ',', '.', '?', '!', ';', ':', '-', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 1 && !stopWords.Contains(t))
                .Distinct()
                .ToList();

            if (queryTokens.Count == 0)
                return new List<HistoricalExcerpt>();

            var scored = new List<HistoricalExcerpt>();
            var cleanQuery = query.Trim().ToLowerInvariant();

            for (int i = 0; i < searchPool.Count; i++)
            {
                var msg = searchPool[i];
                var content = msg.Content;
                var contentLower = content.ToLowerInvariant();

                double score = 0.0;

                // Exact phrase bonus
                if (contentLower.Contains(cleanQuery))
                {
                    score += 6.0;
                }

                // Ordinal bonus for first / initial question queries on early turns
                if ((cleanQuery.Contains("first") || cleanQuery.Contains("initial") || cleanQuery.Contains("earliest") || cleanQuery.Contains("beginning")) && i <= 1)
                {
                    score += 4.0;
                }

                foreach (var token in queryTokens)
                {
                    if (contentLower.Contains(token))
                    {
                        bool isCodeOrNumber = token.Any(char.IsDigit) || token.Length > 6;
                        score += isCodeOrNumber ? 3.0 : 1.0;
                    }
                }

                if (score > 0)
                {
                    // Conversational exchange pairing:
                    // If user message, pair with subsequent assistant response if available.
                    // If assistant message, pair with preceding user message if available.
                    string formattedExchange;
                    if (msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase) && i + 1 < searchPool.Count && searchPool[i + 1].Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        formattedExchange = $"User: {msg.Content}\nAssistant: {searchPool[i + 1].Content}";
                    }
                    else if (msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) && i > 0 && searchPool[i - 1].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                    {
                        formattedExchange = $"User: {searchPool[i - 1].Content}\nAssistant: {msg.Content}";
                    }
                    else
                    {
                        formattedExchange = $"{msg.Role}: {msg.Content}";
                    }

                    scored.Add(new HistoricalExcerpt(i + 1, msg.Role, formattedExchange, msg.CreatedAt, score));
                }
            }

            return scored
                .GroupBy(x => x.Content)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Timestamp)
                .Take(topK)
                .ToList();
        }

        public async Task<string?> LoadHistorySummaryAsync(Guid sessionId)
        {
            var session = await _dbContext.ChatSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId);
            return session?.HistorySummary;
        }

        public async Task<string> BuildMemoryContextAsync(Guid sessionId)
        {
            var session = await _dbContext.ChatSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null) return string.Empty;

            var sb = new StringBuilder();

            // 1. Structured Active Working Memory (Tier 4)
            if (!string.IsNullOrWhiteSpace(session.MemoryJson))
            {
                var structured = await LoadStructuredFactsAsync(session.MemoryJson);
                if (structured.Count > 0)
                {
                    sb.AppendLine("USER-MENTIONED DETAILS (extracted from messages - NOT independently verified against the database):");
                    foreach (var (key, record) in structured)
                    {
                        var historyNote = !string.IsNullOrWhiteSpace(record.SupersededValue)
                            ? $" (updated; superseded '{record.SupersededValue}')"
                            : "";
                        var verifiedNote = string.Equals(record.Category, "support_unverified", StringComparison.OrdinalIgnoreCase)
                            ? " [UNVERIFIED - do not assume this ticket exists or is relevant without calling getTicketStatus/searchChatHistory]"
                            : "";
                        sb.AppendLine($"  - {record.Key}: {record.Value}{historyNote}{verifiedNote}");
                    }
                }
            }

            // 2. Anchored Milestone Checkpoint Summary (Tier 2)
            if (!string.IsNullOrWhiteSpace(session.HistorySummary))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("CONVERSATION HISTORY SUMMARY (ANCHORED CHECKPOINTS):");
                sb.AppendLine(session.HistorySummary);
            }

            return sb.ToString().Trim();
        }

        public async Task MaybeSummarizeHistoryAsync(
            Guid sessionId,
            string apiKey,
            string model,
            List<ChatMessage>? messages = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            var allMessages = messages;
            if (allMessages == null || allMessages.Count == 0)
            {
                allMessages = await _dbContext.ChatMessages
                    .Where(m => m.SessionId == sessionId)
                    .OrderBy(m => m.CreatedAt)
                    .ToListAsync();
            }

            var threshold = _configuration.GetValue<int>(ThresholdKey, ThresholdDefault);
            if (allMessages.Count < threshold) return;

            var session = await _dbContext.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null) return;

            // Don't re-summarize every message — re-anchor at 5-message intervals
            if (!string.IsNullOrWhiteSpace(session.HistorySummary) && allMessages.Count % 5 != 0) return;

            _logger.LogInformation("Generating anchored checkpoint summary for session {SessionId} ({MsgCount} total messages).",
                sessionId, allMessages.Count);

            // Re-anchoring strategy: Anchor to raw messages 1-6 permanently, plus full transcript to eliminate recursive drift
            var anchorTurns = allMessages.Take(Math.Min(6, allMessages.Count)).ToList();
            var anchorText = new StringBuilder();
            foreach (var msg in anchorTurns)
                anchorText.AppendLine($"{msg.Role.ToUpperInvariant()}: {msg.Content}");

            var fullHistoryText = new StringBuilder();
            foreach (var msg in allMessages)
                fullHistoryText.AppendLine($"{msg.Role.ToUpperInvariant()}: {msg.Content}");

            var summarizationPrompt =
                "You are an enterprise conversation summarizer for ChatApp. Produce a structured, anchored milestone summary of the support chat history below.\n" +
                "CRITICAL RULES:\n" +
                "- Do NOT lose early customer details, identifiers, or initial goals.\n" +
                "- Output EXACTLY three structured sections:\n\n" +
                "[CORE OBJECTIVE & INITIAL INQUIRY]\n" +
                "- What the user initially sought, account/ticket numbers, or primary goal (anchored from early turns).\n\n" +
                "[TECHNICAL MILESTONES & ATTEMPTED STEPS]\n" +
                "- Key troubleshooting steps, settings checked, or actions attempted.\n\n" +
                "[CURRENT RESOLUTION STATE]\n" +
                "- The latest status, open questions, or confirmed resolutions.\n\n" +
                "Do NOT include pleasantries or commentary. Output only the structured sections.\n\n" +
                "INITIAL ANCHOR TURNS:\n" + anchorText + "\n\n" +
                "FULL CONVERSATION:\n" + fullHistoryText;

            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var payload = new
            {
                contents = new[]
                {
                    new { role = "user", parts = new[] { new { text = summarizationPrompt } } }
                },
                generationConfig = new { temperature = 0.0, maxOutputTokens = 450 }
            };

            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var content = new System.Net.Http.StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await http.PostAsync(endpoint, content, cts.Token);
                if (!response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync();
                var root = System.Text.Json.Nodes.JsonNode.Parse(json);
                var summaryText = root?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();

                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    session.HistorySummary = summaryText.Trim();
                    await _dbContext.SaveChangesAsync();
                    _logger.LogInformation("Anchored checkpoint summary saved for session {SessionId}.", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Anchored checkpoint summarization failed for session {SessionId} — continuing without update.", sessionId);
            }
        }

        private static async Task<Dictionary<string, FactRecord>> LoadStructuredFactsAsync(string? memoryJson)
        {
            await Task.Yield();
            if (string.IsNullOrWhiteSpace(memoryJson))
                return new Dictionary<string, FactRecord>(StringComparer.OrdinalIgnoreCase);

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, FactRecord>>(memoryJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new Dictionary<string, FactRecord>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                try
                {
                    var flat = JsonSerializer.Deserialize<Dictionary<string, string>>(memoryJson);
                    var dict = new Dictionary<string, FactRecord>(StringComparer.OrdinalIgnoreCase);
                    if (flat != null)
                    {
                        foreach (var kvp in flat)
                        {
                            dict[kvp.Key] = new FactRecord
                            {
                                Key = kvp.Key,
                                Value = kvp.Value,
                                UpdatedAt = DateTime.UtcNow
                            };
                        }
                    }
                    return dict;
                }
                catch
                {
                    return new Dictionary<string, FactRecord>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }
}
