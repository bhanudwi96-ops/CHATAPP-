using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ChatApp.Application.Interfaces;
using ChatApp.Application.DTOs;

namespace ChatApp.API.Controllers
{
    /// <summary>
    /// Controller for conversation management endpoints
    /// </summary>
    [Authorize]
    public class ConversationController : BaseApiController
    {
        private readonly IConversationService _conversationService;

        public ConversationController(IConversationService conversationService)
        {
            _conversationService = conversationService;
        }

        /// <summary>
        /// Get all conversations for the current user
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUserConversations()
        {
            var userId = GetCurrentUserId();
            var conversations = await _conversationService.GetUserConversationsAsync(userId);
            return Ok(conversations);
        }

        /// <summary>
        /// Get a specific conversation by ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetConversationById(Guid id)
        {
            var userId = GetCurrentUserId();
            var conversation = await _conversationService.GetConversationByIdAsync(id, userId);
            return Ok(conversation);
        }

        /// <summary>
        /// Create a new conversation
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateConversation([FromBody] CreateConversationDto dto)
        {
            var userId = GetCurrentUserId();
            var conversation = await _conversationService.CreateConversationAsync(userId, dto);
            return Ok(conversation);
        }

        /// <summary>
        /// Get or create a private conversation with another user
        /// </summary>
        [HttpPost("private/{otherUserId}")]
        public async Task<IActionResult> GetOrCreatePrivateConversation(Guid otherUserId)
        {
            var userId = GetCurrentUserId();
            var conversation = await _conversationService.GetOrCreatePrivateConversationAsync(userId, otherUserId);
            return Ok(conversation);
        }

        /// <summary>
        /// Add participants to a group conversation
        /// </summary>
        [HttpPost("{id}/participants")]
        public async Task<IActionResult> AddParticipants(Guid id, [FromBody] AddParticipantsDto dto)
        {
            var userId = GetCurrentUserId();
            var updated = await _conversationService.AddParticipantsAsync(id, userId, dto.UserIds);
            return Ok(updated);
        }

        /// <summary>
        /// Remove a participant from a group conversation
        /// </summary>
        [HttpDelete("{id}/participants/{participantId}")]
        public async Task<IActionResult> RemoveParticipant(Guid id, Guid participantId)
        {
            var userId = GetCurrentUserId();
            await _conversationService.RemoveParticipantAsync(id, userId, participantId);
            return Ok(new { message = "Participant removed successfully" });
        }

        /// <summary>
        /// Leave a group conversation
        /// </summary>
        [HttpPost("{id}/leave")]
        public async Task<IActionResult> LeaveConversation(Guid id)
        {
            var userId = GetCurrentUserId();
            await _conversationService.LeaveConversationAsync(id, userId);
            return Ok(new { message = "Left conversation successfully" });
        }

        /// <summary>
        /// Update admin status of a participant
        /// </summary>
        [HttpPut("{id}/participants/{participantId}/admin")]
        public async Task<IActionResult> UpdateAdminStatus(Guid id, Guid participantId, [FromBody] UpdateAdminDto dto)
        {
            var userId = GetCurrentUserId();
            await _conversationService.UpdateAdminStatusAsync(id, userId, participantId, dto.IsAdmin);
            return Ok(new { message = "Admin status updated successfully" });
        }

        /// <summary>
        /// Delete a conversation
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteConversation(Guid id)
        {
            var userId = GetCurrentUserId();
            await _conversationService.DeleteConversationAsync(id, userId);
            return Ok(new { message = "Conversation deleted successfully" });
        }
    }
}
