using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.API.Data
{
    /// <summary>
    /// Type alias/wrapper for ChatDbContext to fulfill the project structure requirements.
    /// </summary>
    public class AppDbContext : ChatDbContext
    {
        public AppDbContext(DbContextOptions<ChatDbContext> options) : base(options)
        {
        }
    }
}
