namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Constant values for AiAuditLog.BlockReason.
    /// Using string constants keeps the DB column human-readable and avoids
    /// migration updates when adding new reason types.
    /// </summary>
    public static class AuditBlockReasons
    {
        /// <summary>Intent classified as OutOfScope above the configured confidence threshold.</summary>
        public const string OutOfScope = "OutOfScope";

        /// <summary>User message matched a prompt-injection detection pattern.</summary>
        public const string PromptInjection = "PromptInjection";

        /// <summary>Gemini returned a functionCall for an unknown (unregistered) tool name.</summary>
        public const string ToolUnknown = "ToolUnknown";

        /// <summary>The requested tool is currently disabled via configuration or runtime toggle.</summary>
        public const string ToolDisabled = "ToolDisabled";

        /// <summary>The session has exceeded the configured maximum invocations for this tool.</summary>
        public const string ToolThrottled = "ToolThrottled";

        /// <summary>The user does not meet the authorization requirement for the tool.</summary>
        public const string ToolUnauthorized = "ToolUnauthorized";

        /// <summary>One or more tool arguments failed validation.</summary>
        public const string ArgumentInvalid = "ArgumentInvalid";

        /// <summary>User message exceeded the configured maximum character limit.</summary>
        public const string InputTooLong = "InputTooLong";

        /// <summary>Authenticated user attempted to access a resource owned by another user.</summary>
        public const string OwnershipViolation = "OwnershipViolation";

        /// <summary>Ticket creation suppressed because an active ticket already exists in the current session.</summary>
        public const string DuplicateSessionTicketBlocked = "DuplicateSessionTicketBlocked";

        /// <summary>Ticket creation suppressed because the query is an explanatory why/how question, not a ticket request.</summary>
        public const string ExplanatoryQueryNotTicket = "ExplanatoryQueryNotTicket";
    }
}
