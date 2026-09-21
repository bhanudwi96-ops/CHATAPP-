using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Middleware
{
    public class ChatRateLimitingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ChatRateLimitingMiddleware> _logger;
        private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> IpCounters = new();
        private const int MaxRequestsPerMinute = 20;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        public ChatRateLimitingMiddleware(RequestDelegate next, ILogger<ChatRateLimitingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            if (path.StartsWith("/api/chat", StringComparison.OrdinalIgnoreCase) && 
                HttpMethods.IsPost(context.Request.Method))
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown_client";
                var now = DateTime.UtcNow;

                var entry = IpCounters.AddOrUpdate(
                    ip,
                    _ => (1, now),
                    (_, existing) =>
                    {
                        if (now - existing.WindowStart > Window)
                        {
                            return (1, now);
                        }
                        return (existing.Count + 1, existing.WindowStart);
                    });

                if (entry.Count > MaxRequestsPerMinute)
                {
                    _logger.LogWarning("Rate limit exceeded for IP {IP} on /api/chat ({Count} requests in 1 minute)", ip, entry.Count);
                    context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Too many requests. Please wait a moment before sending another message.\"}");
                    return;
                }
            }

            await _next(context);
        }
    }
}
