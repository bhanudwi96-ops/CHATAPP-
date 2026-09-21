using System;
using System.Security.Claims;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;
using ChatApp.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SupportController : ControllerBase
    {
        private readonly ISupportTicketService _ticketService;

        public SupportController(ISupportTicketService ticketService)
        {
            _ticketService = ticketService;
        }

        /// <summary>
        /// Submit a customer support request (works for logged-in users and guests).
        /// POST /api/support/ticket
        /// </summary>
        [HttpPost("ticket")]
        public async Task<IActionResult> CreateTicket([FromBody] CreateSupportTicketDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            Guid? userId = null;
            if (User.Identity?.IsAuthenticated == true)
            {
                var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (Guid.TryParse(idClaim, out var parsedId))
                {
                    userId = parsedId;
                }
            }

            var ticket = await _ticketService.CreateTicketAsync(dto, userId);
            return Ok(ticket);
        }

        /// <summary>
        /// Retrieve ticket history for logged-in user.
        /// GET /api/support/my-tickets
        /// </summary>
        [HttpGet("my-tickets")]
        public async Task<IActionResult> GetMyTickets()
        {
            if (User.Identity?.IsAuthenticated != true)
                return Unauthorized();

            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var tickets = await _ticketService.GetUserTicketsAsync(userId);
            return Ok(tickets);
        }
    }
}
