using System;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatApp.API.Services
{
    /// <summary>
    /// Result of validating tool arguments.
    /// </summary>
    public sealed record ArgumentValidationResult(bool IsValid, string? ErrorMessage)
    {
        public static ArgumentValidationResult Ok() => new(true, null);
        public static ArgumentValidationResult Fail(string reason) => new(false, reason);
    }

    /// <summary>
    /// Validates and normalizes LLM-generated tool arguments before they reach business services.
    ///
    /// Separation of concerns:
    ///   - This class handles FORMAT and SAFETY (length, encoding, pattern).
    ///   - Authorization and ownership are handled by ToolPolicy.
    ///   - Business rules (e.g., duplicate tickets) remain in TicketService.
    ///
    /// All validation is deterministic — no LLM calls, no DB queries.
    /// </summary>
    public static class ToolArgumentValidator
    {
        private const int MaxIssueLength = 1000;
        private const int MaxReasonLength = 1000;
        private const int MaxCustomerNameLength = 150;
        private const int MaxEmailLength = 320;
        private const int MaxQueryLength = 500;
        private const int MaxTicketRefLength = 50;

        // RFC 5322-simplified: local@domain.tld
        private static readonly Regex EmailRegex = new(
            @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        // Valid ticket references: "12004", "#12004", "TICK-12004", "TICK-12004"
        private static readonly Regex TicketRefRegex = new(
            @"^#?\d{1,10}$|^(TICK-|tick-)?\d{1,10}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        // Characters that should never appear in human-entered names or issue text
        // (null byte, ASCII control chars 0-8, 11-12, 14-31 — allow TAB=9, LF=10, CR=13)
        private static readonly Regex DangerousControlChars = new(
            @"[\x00-\x08\x0B\x0C\x0E-\x1F]",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        /// <summary>
        /// Validates arguments for the named tool.
        /// Returns a failure result with a user-safe message if validation fails.
        /// </summary>
        public static ArgumentValidationResult Validate(string toolName, JsonObject? args)
        {
            if (args == null)
                return ArgumentValidationResult.Ok(); // Some tools have no required args

            return toolName.ToLowerInvariant() switch
            {
                "createticket"    => ValidateCreateTicket(args),
                "escalatetohuman" => ValidateEscalateToHuman(args),
                "getticketstatus" => ValidateGetTicketStatus(args),
                "searchchathistory" => ValidateSearchChatHistory(args),
                _ => ArgumentValidationResult.Fail($"Unknown tool '{toolName}'.") // Should not reach here after ToolRegistry
            };
        }

        // ── createTicket ──────────────────────────────────────────────────────────

        private static ArgumentValidationResult ValidateCreateTicket(JsonObject args)
        {
            var issue = args["issue"]?.ToString();
            var category = args["category"]?.ToString();
            var priority = args["priority"]?.ToString();
            var name = args["customerName"]?.ToString();
            var email = args["customerEmail"]?.ToString();

            if (string.IsNullOrWhiteSpace(issue))
                return ArgumentValidationResult.Fail("createTicket requires a non-empty 'issue'.");

            if (issue.Length > MaxIssueLength)
                return ArgumentValidationResult.Fail($"Issue description is too long (max {MaxIssueLength} characters).");

            if (ContainsDangerousChars(issue))
                return ArgumentValidationResult.Fail("Issue description contains invalid characters.");

            if (!string.IsNullOrWhiteSpace(category))
            {
                var cat = category.Trim().ToLowerInvariant();
                if (cat is not ("billing" or "technical" or "account" or "security" or "security_incident" or "other"))
                    return ArgumentValidationResult.Fail($"Invalid category '{category}'. Must be one of: billing, technical, account, security, other.");
            }

            if (!string.IsNullOrWhiteSpace(priority))
            {
                var prio = priority.Trim().ToLowerInvariant();
                if (prio is not ("low" or "medium" or "high" or "critical"))
                    return ArgumentValidationResult.Fail($"Invalid priority '{priority}'. Must be one of: low, medium, high, critical.");
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                if (name.Length > MaxCustomerNameLength)
                    return ArgumentValidationResult.Fail($"Customer name is too long (max {MaxCustomerNameLength} characters).");
                if (ContainsDangerousChars(name))
                    return ArgumentValidationResult.Fail("Customer name contains invalid characters.");
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                if (!IsValidEmail(email))
                    return ArgumentValidationResult.Fail($"Customer email '{TruncateForLog(email, 50)}' is not a valid email address.");
            }

            return ArgumentValidationResult.Ok();
        }

        // ── escalateToHuman ───────────────────────────────────────────────────────

        private static ArgumentValidationResult ValidateEscalateToHuman(JsonObject args)
        {
            var reason = args["reason"]?.ToString();
            var urgency = args["urgency"]?.ToString();
            var name = args["customerName"]?.ToString();
            var email = args["customerEmail"]?.ToString();

            if (string.IsNullOrWhiteSpace(reason))
                return ArgumentValidationResult.Fail("escalateToHuman requires a non-empty 'reason'.");

            if (reason.Length > MaxReasonLength)
                return ArgumentValidationResult.Fail($"Escalation reason is too long (max {MaxReasonLength} characters).");

            if (ContainsDangerousChars(reason))
                return ArgumentValidationResult.Fail("Escalation reason contains invalid characters.");

            if (!string.IsNullOrWhiteSpace(urgency))
            {
                var u = urgency.Trim().ToLowerInvariant();
                if (u is not ("high" or "critical"))
                    return ArgumentValidationResult.Fail($"Invalid urgency '{urgency}'. Must be one of: high, critical.");
            }

            if (!string.IsNullOrWhiteSpace(name) && name.Length > MaxCustomerNameLength)
                return ArgumentValidationResult.Fail($"Customer name is too long (max {MaxCustomerNameLength} characters).");

            if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
                return ArgumentValidationResult.Fail($"Customer email is not a valid email address.");

            return ArgumentValidationResult.Ok();
        }

        // ── getTicketStatus ───────────────────────────────────────────────────────

        private static ArgumentValidationResult ValidateGetTicketStatus(JsonObject args)
        {
            var ticketRef = args["ticketIdOrRef"]?.ToString();

            if (string.IsNullOrWhiteSpace(ticketRef))
                return ArgumentValidationResult.Ok(); // Allowed — means "look up my latest ticket"

            ticketRef = ticketRef.Trim();

            if (ticketRef.Length > MaxTicketRefLength)
                return ArgumentValidationResult.Fail($"Ticket reference is too long (max {MaxTicketRefLength} characters).");

            if (!TicketRefRegex.IsMatch(ticketRef))
                return ArgumentValidationResult.Fail(
                    $"'{TruncateForLog(ticketRef, 20)}' is not a valid ticket reference. Expected format: #12004 or TICK-12004.");

            return ArgumentValidationResult.Ok();
        }

        // ── searchChatHistory ─────────────────────────────────────────────────────

        private static ArgumentValidationResult ValidateSearchChatHistory(JsonObject args)
        {
            var query = args["query"]?.ToString();

            if (string.IsNullOrWhiteSpace(query))
                return ArgumentValidationResult.Fail("searchChatHistory requires a non-empty 'query'.");

            if (query.Length > MaxQueryLength)
                return ArgumentValidationResult.Fail($"Search query is too long (max {MaxQueryLength} characters).");

            return ArgumentValidationResult.Ok();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email) || email.Length > MaxEmailLength)
                return false;

            try { return EmailRegex.IsMatch(email.Trim()); }
            catch { return false; }
        }

        private static bool ContainsDangerousChars(string value)
        {
            // Explicit null-byte check (in case regex engine strips it from JSON-derived strings)
            if (value.Contains('\0')) return true;
            try { return DangerousControlChars.IsMatch(value); }
            catch { return false; }
        }

        private static string TruncateForLog(string value, int max)
            => value.Length <= max ? value : value[..max] + "...";
    }
}
