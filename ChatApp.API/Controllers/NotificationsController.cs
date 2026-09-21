using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ChatApp.Application.Interfaces;

namespace ChatApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly IUserDeviceRepository _deviceRepository;

        public NotificationsController(IUserDeviceRepository deviceRepository)
        {
            _deviceRepository = deviceRepository;
        }

        /// <summary>
        /// Register or update an FCM device token for the current user.
        /// POST /api/notifications/device
        /// </summary>
        [HttpPost("device")]
        public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Token))
                return BadRequest(new { error = "Device token is required" });

            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await _deviceRepository.UpsertAsync(userId, dto.Token);
            return Ok(new { message = "Device registered" });
        }

        /// <summary>
        /// Remove an FCM device token (e.g. on logout).
        /// DELETE /api/notifications/device
        /// </summary>
        [HttpDelete("device")]
        public async Task<IActionResult> UnregisterDevice([FromBody] RegisterDeviceDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Token))
                return BadRequest(new { error = "Device token is required" });

            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await _deviceRepository.RemoveAsync(userId, dto.Token);
            return Ok(new { message = "Device unregistered" });
        }
    }

    public class RegisterDeviceDto
    {
        public string Token { get; set; } = string.Empty;
    }
}
