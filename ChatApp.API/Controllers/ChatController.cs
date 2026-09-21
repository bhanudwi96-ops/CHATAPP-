using System;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.API.Models;
using ChatApp.API.Services;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using System.Security.Claims;

namespace ChatApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class ChatController : ControllerBase
    {
        private readonly ChatDbContext _dbContext;
        private readonly IKnowledgeBaseService _kbService;
        private readonly IGeminiService _geminiService;
        private readonly IIntentService _intentService;
        private readonly IMemoryService _memoryService;
        private readonly ILogger<ChatController> _logger;
        private readonly IConfiguration _configuration;
        private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _scopeFactory;
        private readonly IChatScopePolicy _scopePolicy;

        public ChatController(
            ChatDbContext dbContext,
            IKnowledgeBaseService kbService,
            IGeminiService geminiService,
            IIntentService intentService,
            IMemoryService memoryService,
            ILogger<ChatController> logger,
            IConfiguration configuration,
            Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory,
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

        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] ChatRequestDto request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (request.SessionId == Guid.Empty)
                return BadRequest(new { error = "A valid SessionId is required." });

            if (string.IsNullOrWhiteSpace(request.Message))
                return BadRequest(new { error = "Message cannot be empty." });

            var ct = HttpContext?.RequestAborted ?? CancellationToken.None;

            // Ensure session exists; if not found, create one automatically
            var session = await _dbContext.ChatSessions.FirstOrDefaultAsync(s => s.Id == request.SessionId, ct);
            if (session == null)
            {
                session = new ChatSession
                {
                    Id = request.SessionId,
                    // CustomerId = 0 explicitly indicates an unauthenticated guest session (never silently attribute to Customer #1)
                    CustomerId = request.CustomerId > 0 ? request.CustomerId : 0,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.ChatSessions.Add(session);
                await _dbContext.SaveChangesAsync(ct);
            }

            // 1. Retrieve the last 20 messages for conversation context
            var recentMessagesDesc = await _dbContext.ChatMessages
                .Where(m => m.SessionId == request.SessionId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(20)
                .ToListAsync(ct);

            var history = recentMessagesDesc.OrderBy(m => m.CreatedAt).ToList();

            // 2. Extract authenticated user identity from JWT & DB (Sequential DB)
            var customerId = request.CustomerId > 0 ? request.CustomerId : session.CustomerId;
            var username = request.Username;
            var displayName = request.DisplayName;
            var email = request.Email;
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

            // 3. Load session working memory (Sequential DB)
            var memoryContext = await _memoryService.BuildMemoryContextAsync(request.SessionId);

            // 4. Concurrent independent HTTP calls: Intent Classification & KB Retrieval
            var contextSnippet = history.Count > 0
                ? string.Join(" | ", history.TakeLast(2).Select(m => $"{m.Role}: {m.Content}"))
                : null;

            var isMetaQuery = _scopePolicy.IsConversationMetaQuery(request.Message);

            var intentTask = _intentService.ClassifyAsync(request.Message, contextSnippet, ct);
            var kbTask = isMetaQuery
                ? Task.FromResult("No specific KnowledgeBase articles found.")
                : _kbService.GetFormattedContextAsync(request.Message, maxResults: 4, ct);

            await Task.WhenAll(intentTask, kbTask);

            var intent = intentTask.IsCompletedSuccessfully
                ? intentTask.Result
                : new IntentResult("ClassificationUnavailable", 0.0f, "Classification unavailable");
            var kbContext = isMetaQuery
                ? "No specific KnowledgeBase articles found."
                : (kbTask.IsCompletedSuccessfully ? kbTask.Result : "No specific KnowledgeBase articles found.");

            _logger.LogInformation("Intent: {Intent} ({Confidence:P0}) — {Reasoning}",
                intent.Intent, intent.Confidence, intent.Reasoning ?? "no reasoning");

            var scopeResult = _scopePolicy.Evaluate(intent, displayName, request.Message);
            bool wasBlocked = scopeResult.IsBlocked;
            GeminiChatResult result;

            if (wasBlocked)
            {
                _logger.LogInformation("Request blocked by OutOfScope policy: Intent={Intent}, Confidence={Confidence:P0}, Reason={Reason}",
                    scopeResult.Intent, scopeResult.Confidence, scopeResult.Reason);

                var refusalText = scopeResult.RefusalMessage!;
                result = new GeminiChatResult(refusalText, false, null, null, scopeResult.Intent);

                // Write audit log
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
                        LatencyMs = 0,
                        TicketRef = null,
                        UserMessageSnippet = request.Message.Length > 500 ? request.Message[..497] + "..." : request.Message
                    });
                    await _dbContext.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to record AiAuditLog for blocked request in ChatController.");
                }
            }
            else
            {
                // 5. Process via orchestrated GeminiService
                //    Passes intent (for agent prompt selection) and memoryContext (for session facts injection)
                result = await _geminiService.ProcessChatAsync(
                    request.SessionId,
                    customerId,
                    username,
                    email,
                    request.Message,
                    history,
                    kbContext,
                    authenticatedUserId,
                    intent,
                    memoryContext,
                    displayName);
            }

            // 8. Persist user message and assistant reply
            var now = DateTime.UtcNow;
            var userMsg = new ChatMessage
            {
                Id = Guid.NewGuid(),
                SessionId = request.SessionId,
                Role = "user",
                Content = request.Message.Trim(),
                CreatedAt = now
            };

            var assistantMsg = new ChatMessage
            {
                Id = Guid.NewGuid(),
                SessionId = request.SessionId,
                Role = "assistant",
                Content = result.Reply,
                CreatedAt = now.AddMilliseconds(50)
            };

            _dbContext.ChatMessages.AddRange(userMsg, assistantMsg);
            await _dbContext.SaveChangesAsync();

            // 9. Fire-and-forget memory reconciliation & milestone summarization (only if request was not blocked)
            if (!wasBlocked)
            {
                var apiKey = _configuration["GEMINI_API_KEY"] ?? _configuration["Gemini:ApiKey"] ?? string.Empty;
                var model = _configuration["Gemini:Model"] ?? "gemini-3.5-flash-lite";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var scopedMemoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
                        await scopedMemoryService.ReconcileUserFactsAsync(request.SessionId, request.Message, result.Reply);
                        await scopedMemoryService.MaybeSummarizeHistoryAsync(request.SessionId, apiKey, model);
                    }
                    catch { /* non-critical */ }
                });
            }

            // 10. Return clean response
            return Ok(new ChatResponseDto
            {
                Reply = result.Reply,
                TicketCreated = result.TicketCreated,
                TicketId = result.TicketId
            });
        }
    }
}
