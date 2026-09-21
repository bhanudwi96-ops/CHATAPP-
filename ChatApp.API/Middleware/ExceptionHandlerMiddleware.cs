using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using ChatApp.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Middleware
{
    /// <summary>
    /// Global exception handler that maps custom exceptions to proper HTTP status codes
    /// </summary>
    public class ExceptionHandlerMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlerMiddleware> _logger;

        public ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(context, ex);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            var (statusCode, message) = exception switch
            {
                NotFoundException e => (HttpStatusCode.NotFound, e.Message),
                ForbiddenException e => (HttpStatusCode.Forbidden, e.Message),
                BusinessRuleException e => (HttpStatusCode.BadRequest, e.Message),
                ConflictException e => (HttpStatusCode.Conflict, e.Message),
                UnauthorizedAccessException e => (HttpStatusCode.Unauthorized, e.Message),
                _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred")
            };

            if (statusCode == HttpStatusCode.InternalServerError)
            {
                _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);
            }
            else
            {
                _logger.LogWarning("Handled exception ({StatusCode}): {Message}", (int)statusCode, exception.Message);
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)statusCode;

            var response = JsonSerializer.Serialize(new { error = message });
            await context.Response.WriteAsync(response);
        }
    }
}
