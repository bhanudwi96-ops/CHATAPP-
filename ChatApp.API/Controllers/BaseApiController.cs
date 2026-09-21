using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.API.Controllers
{
    /// <summary>
    /// Base controller with shared utilities for all API controllers
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public abstract class BaseApiController : ControllerBase
    {
        /// <summary>
        /// Extract the current authenticated user's ID from JWT claims
        /// </summary>
        protected Guid GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userIdClaim))
            {
                throw new UnauthorizedAccessException("User not authenticated");
            }

            return Guid.Parse(userIdClaim);
        }

        /// <summary>
        /// Extract the current authenticated user's username from JWT claims
        /// </summary>
        protected string GetCurrentUsername()
        {
            return User.FindFirst(ClaimTypes.Name)?.Value
                ?? throw new UnauthorizedAccessException("User not authenticated");
        }
    }
}
