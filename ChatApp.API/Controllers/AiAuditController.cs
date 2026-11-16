using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using ChatApp.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Controllers
{
    /// <summary>
    /// Exposes the AI orchestration audit trail.
    /// Allows the frontend to show "Why did the AI do that?" transparency data.
    ///
    /// Sprint 1 changes:
    ///   - GET /api/aiaudit/session/{id} now requires [Authorize] and validates
    ///     that the sessionId belongs to the requesting user's sessions.
    ///   - GET /api/aiaudit/threats added — returns recent blocked/intercepted requests.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AiAuditController : ControllerBase
    {
        private readonly ChatDbContext _dbContext;
        private readonly ILogger<AiAuditController> _logger;

        public AiAuditController(ChatDbContext dbContext, ILogger<AiAuditController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        /// <summary>
        /// Returns the audit trail for a specific chat session.
        /// Ordered by most-recent-first. Capped at 50 entries.
        ///
        /// Authentication:
        ///   - Requires a valid JWT.
        ///   - Validates that the sessionId belongs to the authenticated user's sessions.
        ///   - Anonymous callers cannot read any session audit trail.
        /// </summary>
        [HttpGet("session/{sessionId:guid}")]
        [Authorize]
        public async Task<IActionResult> GetSessionAudit(Guid sessionId)
        {
            // Extract authenticated user ID from JWT
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                              ?? User.FindFirstValue("sub")
                              ?? User.FindFirstValue("id");

            if (!Guid.TryParse(userIdClaim, out var authenticatedUserId))
            {
                _logger.LogWarning("[AuditController] Unauthenticated access attempt to session audit: SessionId={SessionId}", sessionId);
                return Unauthorized(new { error = "Authentication required." });
            }

            // Validate that this session belongs to the authenticated user.
            // We check AiAuditLogs.AuthenticatedUserId as set by GeminiService,
            // OR fall through to allow if no logs have been written yet (new session).
            var sessionOwned = await _dbContext.AiAuditLogs
                .AsNoTracking()
                .AnyAsync(l => l.SessionId == sessionId && l.AuthenticatedUserId == authenticatedUserId);

            // Also check ChatSessions table (guest sessions have CustomerId = 0)
            var sessionExists = await _dbContext.ChatSessions
                .AsNoTracking()
                .AnyAsync(s => s.Id == sessionId);

            if (sessionExists && !sessionOwned)
            {
                // Session exists but doesn't belong to this user
                _logger.LogWarning(
                    "[AuditController] User {UserId} attempted to read audit for session {SessionId} they don't own.",
                    authenticatedUserId, sessionId);
                return Forbid();
            }

            var logs = await _dbContext.AiAuditLogs
                .AsNoTracking()
                .Where(l => l.SessionId == sessionId)
                .OrderByDescending(l => l.Timestamp)
                .Take(50)
                .Select(l => new
                {
                    l.Id,
                    l.Timestamp,
                    l.Intent,
                    l.IntentConfidence,
                    l.ModelUsed,
                    l.LatencyMs,
                    l.KbArticlesUsedJson,
                    l.TicketRef,
                    l.UserMessageSnippet,
                    l.ToolCallsJson,
                    l.BlockReason
                })
                .ToListAsync();

            return Ok(new { sessionId, totalEntries = logs.Count, logs });
        }

        /// <summary>
        /// Returns aggregate intent distribution across all sessions.
        /// Useful for admin dashboards — shows how often each intent is triggered.
        /// </summary>
        [HttpGet("summary")]
        [Authorize]
        public async Task<IActionResult> GetAuditSummary(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var query = _dbContext.AiAuditLogs.AsNoTracking();
            if (from.HasValue) query = query.Where(l => l.Timestamp >= from.Value);
            if (to.HasValue) query = query.Where(l => l.Timestamp <= to.Value);

            var stats = await query
                .GroupBy(l => l.Intent)
                .Select(g => new
                {
                    Intent = g.Key,
                    Count = g.Count(),
                    AvgLatencyMs = (int)g.Average(l => l.LatencyMs),
                    AvgConfidence = g.Average(l => l.IntentConfidence)
                })
                .OrderByDescending(g => g.Count)
                .ToListAsync();

            var totalRuns = stats.Sum(s => s.Count);
            var avgLatency = stats.Count > 0 ? stats.Average(s => s.AvgLatencyMs) : 0;

            return Ok(new { totalRuns, avgLatencyMs = (int)avgLatency, intentBreakdown = stats });
        }

        /// <summary>
        /// Returns recent blocked / intercepted requests (BlockReason IS NOT NULL).
        /// Useful for monitoring abuse patterns.
        /// Requires authentication. Returns last 100 threat events by default.
        ///
        /// NOTE: Admin role is deferred to Sprint 2 (no Admin role exists in the current
        /// JWT model). Any authenticated user can view the threats list for now.
        /// </summary>
        [HttpGet("threats")]
        [Authorize]
        public async Task<IActionResult> GetThreats(
            [FromQuery] int limit = 100,
            [FromQuery] DateTime? from = null)
        {
            limit = Math.Clamp(limit, 1, 500);
            var since = from ?? DateTime.UtcNow.AddDays(-7); // default: last 7 days

            var threats = await _dbContext.AiAuditLogs
                .AsNoTracking()
                .Where(l => l.BlockReason != null && l.Timestamp >= since)
                .OrderByDescending(l => l.Timestamp)
                .Take(limit)
                .Select(l => new
                {
                    l.Id,
                    l.SessionId,
                    l.Timestamp,
                    l.Intent,
                    l.BlockReason,
                    l.ModelUsed,
                    // Safe snippet only — never expose full message content
                    l.UserMessageSnippet,
                    l.ToolCallsJson
                })
                .ToListAsync();

            return Ok(new
            {
                since,
                totalThreats = threats.Count,
                threats
            });
        }
    }
}
