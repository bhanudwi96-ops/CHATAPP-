using System;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.API.Models;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class SessionsController : ControllerBase
    {
        private readonly ChatDbContext _dbContext;
        private readonly ILogger<SessionsController> _logger;

        public SessionsController(ChatDbContext dbContext, ILogger<SessionsController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequestDto? request)
        {
            var customerId = request?.CustomerId is > 0 ? request.CustomerId.Value : 1;
            var session = new ChatSession
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.ChatSessions.Add(session);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("New chat session created: {SessionId} for customer {CustomerId}", session.Id, customerId);

            return Ok(new SessionResponseDto
            {
                SessionId = session.Id
            });
        }

        [HttpGet("{id:guid}/messages")]
        public async Task<IActionResult> GetSessionMessages(Guid id)
        {
            var sessionExists = await _dbContext.ChatSessions.AnyAsync(s => s.Id == id);
            if (!sessionExists)
            {
                return NotFound(new { error = $"Session {id} was not found." });
            }

            var messages = await _dbContext.ChatMessages
                .Where(m => m.SessionId == id)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new MessageHistoryDto
                {
                    Id = m.Id,
                    SessionId = m.SessionId,
                    Role = m.Role,
                    Content = m.Content,
                    CreatedAt = m.CreatedAt
                })
                .ToListAsync();

            return Ok(messages);
        }
    }
}
