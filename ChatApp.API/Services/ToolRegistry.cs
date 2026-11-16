using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Services
{
    /// <summary>
    /// Immutable definition for a single AI tool/function.
    /// </summary>
    public sealed record ToolDefinition(
        string Name,
        bool Enabled,
        bool RequiresAuth,
        int MaxPerSession);

    /// <summary>
    /// Result of resolving a tool from the registry.
    /// </summary>
    public sealed record ToolResolveResult(
        bool Success,
        ToolDefinition? Tool,
        string? BlockReason,
        string? BlockMessage);

    public interface IToolRegistry
    {
        /// <summary>
        /// Resolves a tool by name.  Returns a failure result for unknown or disabled tools.
        /// Call this BEFORE any authorization or execution step.
        /// </summary>
        ToolResolveResult Resolve(string? toolName);

        /// <summary>Returns all registered tool definitions (for diagnostics/admin).</summary>
        IReadOnlyList<ToolDefinition> AllTools { get; }
    }

    /// <summary>
    /// Singleton registry loaded from AI:Tools configuration at startup.
    /// Unknown tools fail closed. Disabled tools fail closed.
    /// The LLM can never bypass this registry.
    /// </summary>
    public sealed class ToolRegistry : IToolRegistry
    {
        private readonly Dictionary<string, ToolDefinition> _tools;
        private readonly ILogger<ToolRegistry> _logger;

        // Known tool names — changing this list requires a code review, not just config.
        private static readonly HashSet<string> KnownToolNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "createTicket",
            "escalateToHuman",
            "getTicketStatus",
            "searchChatHistory"
        };

        public IReadOnlyList<ToolDefinition> AllTools { get; }

        public ToolRegistry(IConfiguration configuration, ILogger<ToolRegistry> logger)
        {
            _logger = logger;
            _tools = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var toolName in KnownToolNames)
            {
                var section = configuration.GetSection($"AI:Tools:{toolName}");

                // Default: enabled=true, requiresAuth=false, maxPerSession=5
                var enabled = section.GetValue<bool?>("Enabled") ?? true;
                var requiresAuth = section.GetValue<bool?>("RequiresAuth") ?? false;
                var maxPerSession = section.GetValue<int?>("MaxPerSession") ?? 5;

                var def = new ToolDefinition(toolName, enabled, requiresAuth, maxPerSession);
                _tools[toolName] = def;

                _logger.LogInformation(
                    "[ToolRegistry] Registered tool '{Name}': Enabled={Enabled}, RequiresAuth={RequiresAuth}, MaxPerSession={Max}",
                    toolName, enabled, requiresAuth, maxPerSession);
            }

            AllTools = new List<ToolDefinition>(_tools.Values);
        }

        public ToolResolveResult Resolve(string? toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return new ToolResolveResult(false, null,
                    ChatApp.Domain.Entities.AuditBlockReasons.ToolUnknown,
                    "Tool name was null or empty.");
            }

            if (!KnownToolNames.Contains(toolName))
            {
                _logger.LogWarning("[ToolRegistry] Unknown tool requested by LLM: '{ToolName}'. Rejecting.", toolName);
                return new ToolResolveResult(false, null,
                    ChatApp.Domain.Entities.AuditBlockReasons.ToolUnknown,
                    $"Tool '{toolName}' is not registered. The request has been blocked.");
            }

            if (!_tools.TryGetValue(toolName, out var def))
            {
                // Defensive: should not happen if KnownToolNames and _tools are kept in sync
                return new ToolResolveResult(false, null,
                    ChatApp.Domain.Entities.AuditBlockReasons.ToolUnknown,
                    $"Tool '{toolName}' is known but not loaded from configuration.");
            }

            if (!def.Enabled)
            {
                _logger.LogWarning("[ToolRegistry] Disabled tool requested: '{ToolName}'. Rejecting.", toolName);
                return new ToolResolveResult(false, def,
                    ChatApp.Domain.Entities.AuditBlockReasons.ToolDisabled,
                    $"Tool '{toolName}' is currently disabled. Please try again later or contact support directly.");
            }

            return new ToolResolveResult(true, def, null, null);
        }
    }
}
