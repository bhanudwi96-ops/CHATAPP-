using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.API.Models;
using ChatApp.API.Services;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace ChatApp.API.Controllers
{
    /// <summary>
    /// Phase 3 — Streaming chat endpoint.
    /// POST /api/chat/stream returns a text/event-stream SSE response where
    /// each chunk is:  data: {"chunk":"<text>"}\n\n
    /// The terminal event is:  data: {"event":"done","ticketCreated":false,"ticketId":null,...}\n\n
    ///
    /// The existing POST /api/chat (ChatController) is completely untouched and
    /// remains a valid fallback.
    /// </summary>
    [ApiController]
    [Route("api/chat")]
    [AllowAnonymous]
    public class ChatStreamController : ControllerBase
    {
        private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeStreams = new();

        private readonly ChatDbContext _dbContext;
        private readonly IKnowledgeBaseService _kbService;
        private readonly IGeminiService _geminiService;
        private readonly IIntentService _intentService;
        private readonly IMemoryService _memoryService;
        private readonly ILogger<ChatStreamController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IChatScopePolicy _scopePolicy;

        public ChatStreamController(
            ChatDbContext dbContext,
            IKnowledgeBaseService kbService,
            IGeminiService geminiService,
            IIntentService intentService,
            IMemoryService memoryService,
            ILogger<ChatStreamController> logger,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory,
            IChatScopePolicy scopePolicy)
        {
            _dbContext = dbContext;
            _kbService = kbService;
            _geminiService = geminiService;
            _intentService = intentService;
            _memoryService = memoryService;
            _logger = logger;
            _configuration = configuration;
            _scopeFactory = scopeFactory;
            _scopePolicy = scopePolicy;
        }

        [HttpPost("stream")]
        public async Task StreamMessage([FromBody] ChatRequestDto request)
        {
            // ── Input validation ──────────────────────────────────────────────────
            if (request == null || request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.Message))
            {
                Response.StatusCode = 400;
                await Response.WriteAsync("{\"error\":\"Invalid request.\"}");
                return;
            }

            // ── Supersede any in-flight stream for this session ───────────────────
            var sessionCts = new CancellationTokenSource();
            if (_activeStreams.TryRemove(request.SessionId, out var existingCts))
            {
                _logger.LogInformation("[Stream] Superseding active stream for session {SessionId}", request.SessionId);
                try
                {
                    existingCts.Cancel();
                    existingCts.Dispose();
                }
                catch { /* ignore */ }
            }
            _activeStreams[request.SessionId] = sessionCts;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, sessionCts.Token);
            var ct = linkedCts.Token;

            // ── SSE headers — must be set before any writes ───────────────────────
            Response.Headers["Content-Type"]  = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx buffering if present
            Response.Headers["Connection"]    = "keep-alive";

            // Send SSE response headers immediately (asynchronous flush)
            await Response.Body.FlushAsync(ct);

            // Helper: write one SSE event and flush immediately
            async Task WriteEvent(string jsonPayload)
            {
                try
                {
                    var line = $"data: {jsonPayload}\n\n";
                    await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
                    await Response.Body.FlushAsync(ct);
                }
                catch { /* client disconnected */ }
            }

            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            var correlationId = Guid.NewGuid().ToString("N")[..8];
            var assembledReply = new StringBuilder();
            bool ticketCreated = false;
            int? ticketId = null;
            long? ttftMs = null;
            int chunkCount = 0;
            long preprocessingMs = 0;
            bool wasBlocked = false;

            try
            {
                // ── 1. Sequential DB reads (EF Core Thread-Safe) ──────────────────────
                var session = await _dbContext.ChatSessions.FirstOrDefaultAsync(s => s.Id == request.SessionId, ct);
                if (session == null)
                {
                    session = new ChatSession
                    {
                        Id = request.SessionId,
                        CustomerId = request.CustomerId > 0 ? request.CustomerId : 0,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.ChatSessions.Add(session);
                    await _dbContext.SaveChangesAsync(ct);
                }

                var recentDesc = await _dbContext.ChatMessages
                    .Where(m => m.SessionId == request.SessionId)
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(20)
                    .ToListAsync(ct);
                var history = recentDesc.OrderBy(m => m.CreatedAt).ToList();

                var customerId = request.CustomerId > 0 ? request.CustomerId : session.CustomerId;
                var username   = request.Username;
                var displayName = request.DisplayName;
                var email      = request.Email;
                Guid? authenticatedUserId = null;

                if (User.Identity?.IsAuthenticated == true)
                {
                    var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                                  ?? User.FindFirstValue("sub")
                                  ?? User.FindFirstValue("id");
                    if (Guid.TryParse(idClaim, out var parsedId))
                        authenticatedUserId = parsedId;
                    
                    var jwtUsername = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("unique_name");
                    if (!string.IsNullOrWhiteSpace(jwtUsername))
                        username = jwtUsername;

                    if (string.IsNullOrWhiteSpace(email))
                        email = User.FindFirstValue(ClaimTypes.Email);
                }

                if (authenticatedUserId.HasValue && (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(username)))
                {
                    var dbUser = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == authenticatedUserId.Value, ct);
                    if (dbUser != null)
                    {
                        if (string.IsNullOrWhiteSpace(displayName)) displayName = dbUser.DisplayName;
                        if (string.IsNullOrWhiteSpace(username)) username = dbUser.Username;
                        if (string.IsNullOrWhiteSpace(email)) email = dbUser.Email;
                    }
                }

                var memoryContext = await _memoryService.BuildMemoryContextAsync(request.SessionId);
                var dbMs = totalSw.ElapsedMilliseconds;

                // ── 2. Concurrent HTTP calls: Intent Classification & KB Retrieval ────
                var contextSnippet = history.Count > 0
                    ? string.Join(" | ", history.TakeLast(2).Select(m => $"{m.Role}: {m.Content}"))
                    : null;

                var isMetaQuery = _scopePolicy.IsConversationMetaQuery(request.Message);

                var intentSw = System.Diagnostics.Stopwatch.StartNew();
                var intentTask = _intentService.ClassifyAsync(request.Message, contextSnippet, ct);

                var kbSw = System.Diagnostics.Stopwatch.StartNew();
                var kbTask = isMetaQuery
                    ? Task.FromResult("No specific KnowledgeBase articles found.")
                    : _kbService.GetFormattedContextAsync(request.Message, maxResults: 4, ct);

                await Task.WhenAll(intentTask, kbTask);
                long intentMs = intentSw.ElapsedMilliseconds;
                long kbMs = kbSw.ElapsedMilliseconds;

                var intent = intentTask.IsCompletedSuccessfully
                    ? intentTask.Result
                    : new IntentResult("ClassificationUnavailable", 0.0f, "Task faulted or cancelled");
                var kbContext = isMetaQuery
                    ? "No specific KnowledgeBase articles found."
                    : (kbTask.IsCompletedSuccessfully ? kbTask.Result : "No specific KnowledgeBase articles found.");

                preprocessingMs = totalSw.ElapsedMilliseconds;
                _logger.LogInformation("[Stream {CorrelationId}] Preprocessing complete in {PreprocessingMs}ms (DB: {DbMs}ms, Intent: {IntentMs}ms, KB: {KbMs}ms). Intent: {Intent} ({Confidence:P0})",
                    correlationId, preprocessingMs, dbMs, intentMs, kbMs, intent.Intent, intent.Confidence);

                var scopeResult = _scopePolicy.Evaluate(intent, displayName, request.Message);
                wasBlocked = scopeResult.IsBlocked;

                if (wasBlocked)
                {
                    _logger.LogInformation("[Stream {CorrelationId}] Request blocked by OutOfScope policy: Intent={Intent}, Confidence={Confidence:P0}, Reason={Reason}",
                        correlationId, scopeResult.Intent, scopeResult.Confidence, scopeResult.Reason);

                    var refusalText = scopeResult.RefusalMessage!;
                    assembledReply.Append(refusalText);
                    chunkCount = 1;
                    ttftMs = totalSw.ElapsedMilliseconds;

                    // Emit refusal text chunk
                    var chunkPayload = JsonSerializer.Serialize(new { chunk = refusalText });
                    await WriteEvent(chunkPayload);

                    // Emit terminal done event
                    var totalMs = totalSw.ElapsedMilliseconds;
                    var donePayload = JsonSerializer.Serialize(new
                    {
                        @event        = "done",
                        ticketCreated = false,
                        ticketId      = (int?)null,
                        correlationId = correlationId,
                        ttftMs        = ttftMs.Value,
                        totalMs       = totalMs,
                        preprocessingMs = preprocessingMs,
                        chunkCount    = chunkCount
                    });
                    await WriteEvent(donePayload);

                    // Record audit log for deterministic refusal
                    try
                    {
                        _dbContext.AiAuditLogs.Add(new ChatApp.Domain.Entities.AiAuditLog
                        {
                            Id = Guid.NewGuid(),
                            SessionId = request.SessionId,
                            Timestamp = DateTime.UtcNow,
                            Intent = scopeResult.Intent,
                            IntentConfidence = scopeResult.Confidence,
                            KbArticlesUsedJson = "[]",
                            ModelUsed = "DeterministicGuard",
                            LatencyMs = (int)totalMs,
                            TicketRef = null,
                            UserMessageSnippet = request.Message.Length > 500 ? request.Message[..497] + "..." : request.Message
                        });
                        await _dbContext.SaveChangesAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Stream {CorrelationId}] Failed to record AiAuditLog for blocked request.", correlationId);
                    }
                }
                else
                {
                    // ── 3. Stream response from GeminiService ─────────────────────────────
                    await foreach (var chunk in _geminiService.StreamChatAsync(
                        request.SessionId, customerId, username, email,
                        request.Message, history, kbContext,
                        authenticatedUserId, intent, memoryContext, displayName, ct))
                    {
                        if (ct.IsCancellationRequested) break;

                        if (chunk.Done)
                        {
                            ticketCreated = chunk.TicketCreated;
                            ticketId      = chunk.TicketId;
                            var totalMs = totalSw.ElapsedMilliseconds;
                            // Write the terminal "done" event with telemetry
                            var donePayload = JsonSerializer.Serialize(new
                            {
                                @event        = "done",
                                ticketCreated = chunk.TicketCreated,
                                ticketId      = chunk.TicketId,
                                correlationId = correlationId,
                                ttftMs        = ttftMs ?? totalMs,
                                totalMs       = totalMs,
                                preprocessingMs = preprocessingMs,
                                chunkCount    = chunkCount
                            });
                            await WriteEvent(donePayload);
                            break;
                        }

                        if (!string.IsNullOrEmpty(chunk.ErrorMessage))
                        {
                            var errPayload = JsonSerializer.Serialize(new { @event = "error", message = chunk.ErrorMessage });
                            await WriteEvent(errPayload);
                            continue;
                        }

                        // Skip function-call signal chunks (they are informational for logging)
                        if (chunk.FunctionCallName != null) continue;

                        if (!string.IsNullOrEmpty(chunk.Text))
                        {
                            if (!ttftMs.HasValue)
                            {
                                ttftMs = totalSw.ElapsedMilliseconds;
                                _logger.LogInformation("[Stream {CorrelationId}] TTFT: {TtftMs}ms", correlationId, ttftMs.Value);
                            }
                            chunkCount++;
                            assembledReply.Append(chunk.Text);
                            var chunkPayload = JsonSerializer.Serialize(new { chunk = chunk.Text });
                            await WriteEvent(chunkPayload);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Stream {CorrelationId}] Stream cancelled or superseded for session {SessionId}.", correlationId, request.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Stream {CorrelationId}] Unexpected error during streaming for session {SessionId}.", correlationId, request.SessionId);
                var errPayload = JsonSerializer.Serialize(new { @event = "error", message = "An unexpected error occurred. Please try again." });
                try { await WriteEvent(errPayload); } catch { }
            }
            finally
            {
                if (_activeStreams.TryGetValue(request.SessionId, out var currentCts) && currentCts == sessionCts)
                {
                    _activeStreams.TryRemove(request.SessionId, out _);
                }
                sessionCts.Dispose();
            }

            // ── Persist user message + assembled assistant reply ───────────────────
            var finalReply = assembledReply.ToString();
            if (!string.IsNullOrWhiteSpace(finalReply))
            {
                try
                {
                    var now = DateTime.UtcNow;
                    _dbContext.ChatMessages.AddRange(
                        new ChatMessage
                        {
                            Id        = Guid.NewGuid(),
                            SessionId = request.SessionId,
                            Role      = "user",
                            Content   = request.Message.Trim(),
                            CreatedAt = now
                        },
                        new ChatMessage
                        {
                            Id        = Guid.NewGuid(),
                            SessionId = request.SessionId,
                            Role      = "assistant",
                            Content   = finalReply,
                            CreatedAt = now.AddMilliseconds(50)
                        });
                    await _dbContext.SaveChangesAsync(CancellationToken.None);

                    // Fire-and-forget memory reconciliation & milestone summarization (only if request was not blocked)
                    if (!wasBlocked)
                    {
                        var apiKey = _configuration["GEMINI_API_KEY"] ?? _configuration["Gemini:ApiKey"] ?? string.Empty;
                        var modelName = _configuration["Gemini:Model"] ?? "gemini-3.5-flash-lite";
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var scopedMemoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
                                await scopedMemoryService.ReconcileUserFactsAsync(request.SessionId, request.Message, finalReply);
                                await scopedMemoryService.MaybeSummarizeHistoryAsync(request.SessionId, apiKey, modelName);
                            }
                            catch { /* non-critical */ }
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Stream {CorrelationId}] Failed to persist messages for session {SessionId}.", correlationId, request.SessionId);
                }
            }
        }
    }
}
