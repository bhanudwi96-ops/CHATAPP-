using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Services
{
    /// <summary>
    /// Result of a throttle check.
    /// </summary>
    public sealed record ThrottleResult(bool Allowed, int CurrentCount, int MaxAllowed);

    public interface IToolThrottle
    {
        /// <summary>
        /// Atomically increments the invocation counter for (sessionId, toolName)
        /// and returns whether the invocation is within the configured limit.
        /// Blocked calls are NOT counted (the counter is not incremented when the limit is exceeded).
        /// </summary>
        ThrottleResult TryConsume(Guid sessionId, string toolName, int maxPerSession);

        /// <summary>Returns the current count for diagnostic purposes without incrementing.</summary>
        int GetCount(Guid sessionId, string toolName);
    }

    /// <summary>
    /// Singleton per-session tool invocation throttle.
    /// Tracks (sessionId, toolName) → invocation count using a thread-safe in-memory dictionary.
    ///
    /// LIMITATION: State is in-memory and is lost on application restart.
    /// For a multi-instance deployment, this would need to be backed by a distributed cache.
    /// Acceptable for the current single-instance configuration.
    /// </summary>
    public sealed class ToolThrottle : IToolThrottle
    {
        // Key: "sessionId:toolName"
        private readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<ToolThrottle> _logger;

        public ToolThrottle(ILogger<ToolThrottle> logger)
        {
            _logger = logger;
        }

        private static string MakeKey(Guid sessionId, string toolName)
            => $"{sessionId:N}:{toolName}";

        public ThrottleResult TryConsume(Guid sessionId, string toolName, int maxPerSession)
        {
            var key = MakeKey(sessionId, toolName);

            // Atomically read then conditionally increment
            while (true)
            {
                var current = _counters.GetOrAdd(key, 0);

                if (current >= maxPerSession)
                {
                    _logger.LogWarning(
                        "[ToolThrottle] Session {SessionId} exceeded limit for '{Tool}': {Current}/{Max}",
                        sessionId, toolName, current, maxPerSession);
                    return new ThrottleResult(false, current, maxPerSession);
                }

                // Try to atomically increment — retry loop handles concurrent updates
                var next = current + 1;
                if (_counters.TryUpdate(key, next, current))
                {
                    return new ThrottleResult(true, next, maxPerSession);
                }
                // Another thread updated the key concurrently — retry
            }
        }

        public int GetCount(Guid sessionId, string toolName)
            => _counters.GetOrAdd(MakeKey(sessionId, toolName), 0);
    }
}
