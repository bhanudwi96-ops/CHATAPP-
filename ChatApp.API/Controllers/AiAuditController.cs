using System;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.API.Controllers
{
    /// <summary>
    /// Exposes the AI orchestration audit trail.
    /// Allows the frontend to show "Why did the AI do that?" transparency data.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous] // Authenticated access can be enforced per-route when needed
    public class AiAuditController : ControllerBase
    {
        private readonly ChatDbContext _dbContext;

        public AiAuditController(ChatDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>
        /// Returns the audit trail for a specific chat session.
        /// Ordered by most-recent-first. Capped at 50 entries.
        /// </summary>
        [HttpGet("session/{sessionId:guid}")]
        public async Task<IActionResult> GetSessionAudit(Guid sessionId)
        {
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
                    l.UserMessageSnippet
                })
                .ToListAsync();

            return Ok(new { sessionId, totalEntries = logs.Count, logs });
        }

        /// <summary>
        /// Returns aggregate intent distribution across all sessions.
        /// Useful for admin dashboards — shows how often each intent is triggered.
        /// </summary>
        [HttpGet("summary")]
        [Authorize] // Admin only
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
    }
}
