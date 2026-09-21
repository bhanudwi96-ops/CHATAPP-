using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Services
{
    /// <summary>
    /// Contract for Knowledge Base search. Implemented by VectorSidecarKBService (Python FAISS sidecar).
    /// </summary>
    public interface IKnowledgeBaseService
    {
        Task<List<KnowledgeBaseEntry>> SearchAsync(string query, int maxResults = 4, CancellationToken cancellationToken = default);
        Task<string> GetFormattedContextAsync(string query, int maxResults = 4, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Replaces KnowledgeBaseService + EmbeddingService entirely.
    ///
    /// Primary path: delegates semantic KB search to the Python FAISS sidecar
    /// running on localhost:8001 via HTTP POST /search.
    ///
    /// Fallback path: if the sidecar is unreachable or times out, falls back
    /// to a lightweight inline keyword search against the SQL database.
    /// No crash, no user-visible error — the bot keeps working.
    ///
    /// The response format from the sidecar must match:
    ///   { "results": [ { "id": 1, "topic": "...", "content": "...", "score": 0.87 } ] }
    /// </summary>
    public class VectorSidecarKBService : IKnowledgeBaseService
    {
        private readonly HttpClient _http;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<VectorSidecarKBService> _logger;
        private readonly string _sidecarUrl;
        private readonly int _timeoutSeconds;

        // ── Stop words for the inline keyword fallback ─────────────────────────
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","and","are","as","at","be","by","for","from","has","he",
            "in","is","it","its","of","on","that","the","to","was","were",
            "will","with","i","my","me","you","your","how","what","where",
            "when","why","who","can","could","should"
        };

        public VectorSidecarKBService(
            HttpClient httpClient,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<VectorSidecarKBService> logger)
        {
            _http           = httpClient;
            _scopeFactory   = scopeFactory;
            _logger         = logger;
            _sidecarUrl     = configuration["VectorSidecar:Url"] ?? "http://localhost:8001";
            _timeoutSeconds = int.TryParse(configuration["VectorSidecar:TimeoutSeconds"], out var t) ? t : 3;

            _http.BaseAddress = new Uri(_sidecarUrl);
            _http.Timeout     = TimeSpan.FromSeconds(_timeoutSeconds + 1); // +1 for TCP overhead
        }

        // ── Primary: Python FAISS sidecar ─────────────────────────────────────
        public async Task<List<KnowledgeBaseEntry>> SearchAsync(string query, int maxResults = 4, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                return await dbContext.KnowledgeBase.AsNoTracking().Take(maxResults).ToListAsync(cancellationToken);
            }

            try
            {
                var payload = new
                {
                    query     = query,
                    top_k     = maxResults,
                    threshold = 0.3f
                };

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var response = await _http.PostAsJsonAsync("/search", payload, linkedCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var json    = await response.Content.ReadAsStringAsync(linkedCts.Token);
                    var result  = JsonSerializer.Deserialize<SidecarSearchResponse>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (result?.Results?.Count > 0)
                    {
                        _logger.LogDebug(
                            "VectorSidecar returned {Count} results for query '{Query}'.",
                            result.Results.Count,
                            query.Length > 60 ? query[..60] + "..." : query);

                        return result.Results.Select(r => new KnowledgeBaseEntry
                        {
                            Id      = r.Id,
                            Topic   = r.Topic,
                            Content = r.Content
                        }).ToList();
                    }

                    _logger.LogDebug(
                        "VectorSidecar returned 0 results above threshold — using keyword fallback.");
                }
                else
                {
                    _logger.LogWarning(
                        "VectorSidecar returned HTTP {Status} — using keyword fallback.",
                        (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (
                ex is HttpRequestException or
                     TaskCanceledException or
                     OperationCanceledException)
            {
                // Sidecar is down or timed out — expected when sidecar isn't running
                _logger.LogWarning(
                    "VectorSidecar unreachable ({Reason}) — using keyword fallback.",
                    ex.Message);
            }

            // ── Fallback: inline keyword search ───────────────────────────────
            return await KeywordFallbackAsync(query, maxResults, cancellationToken);
        }

        public async Task<string> GetFormattedContextAsync(string query, int maxResults = 4, CancellationToken cancellationToken = default)
        {
            var entries = await SearchAsync(query, maxResults, cancellationToken);

            if (entries.Count == 0)
                return "No specific KnowledgeBase articles found.";

            // Exact same output format as the old KnowledgeBaseService.
            // GeminiService sees no difference whatsoever.
            var builder = new StringBuilder();
            builder.AppendLine("=== RELEVANT KNOWLEDGE BASE ARTICLES ===");
            foreach (var entry in entries)
            {
                builder.AppendLine($"[Topic: {entry.Topic}]");
                builder.AppendLine(entry.Content);
                builder.AppendLine();
            }
            builder.AppendLine("=========================================");
            return builder.ToString();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Lightweight keyword-frequency scorer against the SQL KnowledgeBase table.
        /// Used automatically when the Python sidecar is unavailable.
        /// Uses an isolated DbContext scope for EF Core thread safety.
        /// </summary>
        private async Task<List<KnowledgeBaseEntry>> KeywordFallbackAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var allEntries = await dbContext.KnowledgeBase.AsNoTracking().ToListAsync(cancellationToken);
            if (allEntries.Count == 0) return new List<KnowledgeBaseEntry>();

            var words = Regex.Matches(query, @"\b[a-zA-Z0-9]{3,}\b")
                .Select(m => m.Value.ToLowerInvariant())
                .Where(w => !StopWords.Contains(w))
                .Distinct()
                .Take(6)
                .ToList();

            if (words.Count == 0)
                return allEntries.Take(maxResults).ToList();

            return allEntries
                .Select(e => new
                {
                    Entry = e,
                    Score = words.Sum(w =>
                        (e.Topic.Contains(w,    StringComparison.OrdinalIgnoreCase) ? 3 : 0) +
                        (e.Content.Contains(w,  StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(maxResults)
                .Select(x => x.Entry)
                .ToList();
        }

        // ── Private DTOs for deserialising sidecar JSON response ──────────────

        private sealed class SidecarSearchResponse
        {
            public List<SidecarResult> Results { get; set; } = new();
        }

        private sealed class SidecarResult
        {
            public int    Id      { get; set; }
            public string Topic   { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
            public float  Score   { get; set; }
        }
    }
}
