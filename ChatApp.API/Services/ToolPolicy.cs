using System;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Services
{
    /// <summary>
    /// Context passed to ToolPolicy for each authorization check.
    /// All identity comes from the trusted server-side request context (JWT/session),
    /// NEVER from LLM-generated arguments.
    /// </summary>
    public sealed record ToolAuthContext(
        /// <summary>From JWT ClaimTypes.NameIdentifier — null for anonymous users.</summary>
        Guid? AuthenticatedUserId,
        /// <summary>From JWT ClaimTypes.Email — null for anonymous users.</summary>
        string? AuthenticatedEmail,
        /// <summary>The chat session ID.</summary>
        Guid SessionId);

    /// <summary>
    /// Result of the policy authorization check.
    /// </summary>
    public sealed record ToolPolicyResult(bool IsAllowed, string? BlockReason, string? BlockMessage)
    {
        public static ToolPolicyResult Allow() => new(true, null, null);

        public static ToolPolicyResult Block(string blockReason, string blockMessage)
            => new(false, blockReason, blockMessage);
    }

    public interface IToolPolicy
    {
        /// <summary>
        /// Evaluates whether the requesting context is authorized to invoke the tool.
        /// Must be called AFTER ToolRegistry.Resolve and BEFORE ToolThrottle.TryConsume.
        /// Identity MUST come from the ToolAuthContext (server-side JWT), not from LLM args.
        /// </summary>
        ToolPolicyResult Authorize(ToolDefinition tool, ToolAuthContext context);
    }

    /// <summary>
    /// Singleton policy service for per-tool authorization.
    ///
    /// Rules enforced here:
    ///   1. If a tool requires authentication, the request must have an authenticated user.
    ///   2. Tool-specific business rules (e.g., getTicketStatus ownership) are enforced here
    ///      using ONLY server-side identity, never LLM argument values.
    /// </summary>
    public sealed class ToolPolicy : IToolPolicy
    {
        private readonly ILogger<ToolPolicy> _logger;

        public ToolPolicy(ILogger<ToolPolicy> logger)
        {
            _logger = logger;
        }

        public ToolPolicyResult Authorize(ToolDefinition tool, ToolAuthContext context)
        {
            // ── Authentication gate ───────────────────────────────────────────────
            if (tool.RequiresAuth && !context.AuthenticatedUserId.HasValue)
            {
                _logger.LogWarning(
                    "[ToolPolicy] Tool '{Tool}' requires authentication but request is anonymous (SessionId={SessionId}).",
                    tool.Name, context.SessionId);

                return ToolPolicyResult.Block(
                    ChatApp.Domain.Entities.AuditBlockReasons.ToolUnauthorized,
                    $"You must be logged in to use this feature. Please sign in and try again.");
            }

            // ── Tool-specific rules ───────────────────────────────────────────────

            // getTicketStatus: no additional auth-level block here.
            // Ownership is enforced at the TicketService.GetTicketStatusAsync level,
            // where authenticated user ID is passed from context (not from LLM args).
            // This design keeps ownership logic in the service, not duplicated here.

            // All tools currently configured with RequiresAuth=false.
            // When RequiresAuth=true is set in config, the gate above handles it.

            return ToolPolicyResult.Allow();
        }
    }
}
