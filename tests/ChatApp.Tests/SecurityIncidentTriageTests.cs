using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.API.Services;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatApp.Tests
{
    public class SecurityIncidentTriageTests
    {
        private ChatDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ChatDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            return new ChatDbContext(options);
        }

        private IConfiguration CreateTestConfiguration()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "EmailSettings:SupportInboxEmail", "admin-security@chatapp.com" }
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        [Theory]
        [InlineData("my chats are getting leaked")]
        [InlineData("someone hacked my account")]
        [InlineData("unauthorized person is seeing my messages")]
        [InlineData("massive data breach in chat records")]
        [InlineData("my chats are compromised")]
        [InlineData("stolen chat messages")]
        public void IsSecurityIncident_DetectsSecurityKeywords(string text)
        {
            Assert.True(TicketService.IsSecurityIncident(text));
        }

        [Theory]
        [InlineData("how do I change my profile theme?")]
        [InlineData("I cannot see my friend in search")]
        [InlineData("billing question about Pro subscription")]
        [InlineData("i m upset")]
        public void IsSecurityIncident_IgnoresNormalRequests(string text)
        {
            Assert.False(TicketService.IsSecurityIncident(text));
        }

        [Fact]
        public async Task CreateTicketAsync_SecurityLeak_ElevatesPriorityAndDispatchesAdminAlert()
        {
            using var db = CreateInMemoryDbContext();
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var config = CreateTestConfiguration();

            var ticketService = new TicketService(db, scopeFactory, NullLogger<TicketService>.Instance, config);

            // User reports chat leak with default 'technical' / 'medium'
            var ticket = await ticketService.CreateTicketAsync(
                customerId: 123,
                issue: "Emergency: my chats are getting leaked to other users!",
                category: "technical",
                priority: "medium",
                customerName: "Alice User",
                customerEmail: "alice@example.com"
            );

            // Assert Ticket entity
            Assert.Equal("security", ticket.Category);
            Assert.Equal("critical", ticket.Priority);

            // Assert SupportTicket entity in DB
            var supportTicket = await db.SupportTickets.FirstOrDefaultAsync(st => st.ReferenceNumber == $"TICK-{ticket.Id}");
            Assert.NotNull(supportTicket);
            Assert.Equal(TicketCategory.SecurityIncident, supportTicket.Category);
            Assert.StartsWith("[SECURITY INCIDENT]", supportTicket.Subject);

            // Assert EmailOutbox entries: Should have 2 entries (1 for User, 1 for Admin Alert)
            var outboxEntries = await db.EmailOutboxes.ToListAsync();
            Assert.Equal(2, outboxEntries.Count);

            var userEmail = outboxEntries.FirstOrDefault(e => e.ToEmail == "alice@example.com");
            Assert.NotNull(userEmail);
            Assert.Contains("URGENT SECURITY TICKET", userEmail.Subject);

            var adminEmail = outboxEntries.FirstOrDefault(e => e.ToEmail == "admin-security@chatapp.com");
            Assert.NotNull(adminEmail);
            Assert.Equal("SupportTicketEscalation", adminEmail.TemplateType);
            Assert.Contains("[ALERT: CRITICAL SECURITY INCIDENT]", adminEmail.Subject);
        }

        [Fact]
        public async Task CreateTicketAsync_NormalIssue_DoesNotTriggerSecurityAlert()
        {
            using var db = CreateInMemoryDbContext();
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var config = CreateTestConfiguration();

            var ticketService = new TicketService(db, scopeFactory, NullLogger<TicketService>.Instance, config);

            var ticket = await ticketService.CreateTicketAsync(
                customerId: 456,
                issue: "Unable to upload PNG avatar image.",
                category: "technical",
                priority: "low",
                customerName: "Bob User",
                customerEmail: "bob@example.com"
            );

            Assert.Equal("technical", ticket.Category);
            Assert.Equal("low", ticket.Priority);

            var supportTicket = await db.SupportTickets.FirstOrDefaultAsync(st => st.ReferenceNumber == $"TICK-{ticket.Id}");
            Assert.NotNull(supportTicket);
            Assert.Equal(TicketCategory.TechnicalIssue, supportTicket.Category);
            Assert.False(supportTicket.Subject.StartsWith("[SECURITY INCIDENT]"));

            var outboxEntries = await db.EmailOutboxes.ToListAsync();
            Assert.Single(outboxEntries);
            Assert.Equal("bob@example.com", outboxEntries[0].ToEmail);
            Assert.DoesNotContain("SECURITY", outboxEntries[0].Subject);
        }
    }
}

