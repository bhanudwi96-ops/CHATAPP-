using System;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Middleware
{
    /// <summary>
    /// Sprint 1 enhanced rate-limiting middleware.
    ///
    /// Previous behavior: single IP-based bucket, 20 req/min for everyone.
    /// New behavior:
    ///   - Authenticated users:  keyed by userId from JWT (configurable, default 40/min)
    ///   - Anonymous users:      keyed by IP                (configurable, default 10/min)
    ///
    /// This prevents NAT collapse (many users behind one IP all sharing a bucket)
    /// and closes the gap where anonymous spamming benefited from the same limit as
    /// legitimate authenticated users.
    ///
    /// LIMITATION: In-memory state, resets on restart. Acceptable for single-instance.
    /// </summary>
    public class ChatRateLimitingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ChatRateLimitingMiddleware> _logger;
        private readonly IConfiguration _configuration;

        // Key: userId (authenticated) or IP (anonymous)  →  (count, windowStart)
        private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _counters = new();
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        public ChatRateLimitingMiddleware(
            RequestDelegate next,
            ILogger<ChatRateLimitingMiddleware> logger,
            IConfiguration configuration)
        {
            _next = next;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            // Only rate-limit AI chat endpoints
            if (path.StartsWith("/api/chat", StringComparison.OrdinalIgnoreCase) &&
                HttpMethods.IsPost(context.Request.Method))
            {
                var now = DateTime.UtcNow;

                // Determine identity key and applicable limit
                var (bucketKey, maxRequests, isAuthenticated) = GetBucketKey(context);

                var entry = _counters.AddOrUpdate(
                    bucketKey,
                    _ => (1, now),
                    (_, existing) =>
                    {
                        if (now - existing.WindowStart > Window)
                            return (1, now);
                        return (existing.Count + 1, existing.WindowStart);
                    });

                if (entry.Count > maxRequests)
                {
                    _logger.LogWarning(
                        "Rate limit exceeded for {KeyType}={Key} on /api/chat ({Count}/{Max} requests/min)",
                        isAuthenticated ? "UserId" : "IP",
                        isAuthenticated ? "[redacted]" : bucketKey,  // don't log userId in warning
                        entry.Count, maxRequests);

                    context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        "{\"error\":\"Too many requests. Please wait a moment before sending another message.\"}");
                    return;
                }
            }

            await _next(context);
        }

        private (string BucketKey, int MaxRequests, bool IsAuthenticated) GetBucketKey(HttpContext context)
        {
            var authedLimit = _configuration.GetValue<int?>("AI:RateLimit:AuthenticatedRequestsPerMinute") ?? 40;
            var anonLimit   = _configuration.GetValue<int?>("AI:RateLimit:AnonymousRequestsPerMinute")    ?? 10;

            // Try to read the userId from the Authorization: Bearer token
            // without triggering full middleware auth pipeline (avoids coupling)
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader["Bearer ".Length..].Trim();
                try
                {
                    var jwtHandler = new JwtSecurityTokenHandler();
                    if (jwtHandler.CanReadToken(token))
                    {
                        var jwt = jwtHandler.ReadJwtToken(token);
                        var sub = jwt.Claims.FirstOrDefault(c =>
                            c.Type == "nameid" ||
                            c.Type == System.Security.Claims.ClaimTypes.NameIdentifier ||
                            c.Type == "sub" ||
                            c.Type == "id")?.Value;

                        // Validate token hasn't expired
                        if (!string.IsNullOrWhiteSpace(sub) && jwt.ValidTo > DateTime.UtcNow)
                        {
                            return ($"user:{sub}", authedLimit, true);
                        }
                    }
                }
                catch
                {
                    // Malformed or invalid token — fall through to IP-based limiting
                }
            }

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown_client";
            return ($"ip:{ip}", anonLimit, false);
        }
    }
}
