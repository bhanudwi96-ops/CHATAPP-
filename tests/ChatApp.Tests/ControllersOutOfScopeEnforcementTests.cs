using System.Text;
using System.Text.Json;
using ChatApp.API.Controllers;
using ChatApp.API.Models;
using ChatApp.API.Services;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ChatApp.Tests
{
    public class ControllersOutOfScopeEnforcementTests
    {
        private ChatDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ChatDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            return new ChatDbContext(options);
        }

        private IConfiguration CreateConfiguration()
        {
            var configData = new Dictionary<string, string?>
            {
                { "AI:OutOfScopeThreshold", "0.80" },
                { "Gemini:Model", "gemini-3.5-flash-lite" }
            };
            return new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        }

        [Fact]
        public async Task ChatController_HighConfidenceOutOfScope_DoesNotCallGemini_AndReturnsRefusal()
        {
            // Arrange
            var dbContext = CreateInMemoryDbContext();
            var config = CreateConfiguration();
            var policy = new ChatScopePolicy(config);

            var mockKb = new Mock<IKnowledgeBaseService>();
            mockKb.Setup(k => k.GetFormattedContextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("Some KB context");

            var mockGemini = new Mock<IGeminiService>();
            var mockIntent = new Mock<IIntentService>();
            mockIntent.Setup(i => i.ClassifyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IntentResult("OutOfScope", 0.99f, "External entity trivia: Lionel Messi and Sachin Tendulkar"));

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.BuildMemoryContextAsync(It.IsAny<Guid>()))
                .ReturnsAsync(string.Empty);

            var mockLogger = new Mock<ILogger<ChatController>>();
            var mockScopeFactory = new Mock<IServiceScopeFactory>();

            var controller = new ChatController(
                dbContext,
                mockKb.Object,
                mockGemini.Object,
                mockIntent.Object,
                mockMemory.Object,
                mockLogger.Object,
                config,
                mockScopeFactory.Object,
                policy)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            var sessionId = Guid.NewGuid();
            var request = new ChatRequestDto
            {
                SessionId = sessionId,
                CustomerId = 1,
                DisplayName = "Alice",
                Message = "Who is Lionel Messi and Sachin Tendulkar?"
            };

            // Act
            var actionResult = await controller.SendMessage(request);

            // Assert: 1. Controller returned Ok
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var responseDto = Assert.IsType<ChatResponseDto>(okResult.Value);
            Assert.Contains("ChatApp customer support assistant", responseDto.Reply);
            Assert.DoesNotContain("footballer", responseDto.Reply, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cricketer", responseDto.Reply, StringComparison.OrdinalIgnoreCase);
            Assert.False(responseDto.TicketCreated);

            // Assert: 2. Gemini was NEVER called
            mockGemini.Verify(g => g.ProcessChatAsync(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<List<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<Guid?>(),
                It.IsAny<IntentResult?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
                Times.Never);

            // Assert: 3. Messages persisted to DB
            var messages = await dbContext.ChatMessages.Where(m => m.SessionId == sessionId).ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.Equal("user", messages[0].Role);
            Assert.Equal("Who is Lionel Messi and Sachin Tendulkar?", messages[0].Content);
            Assert.Equal("assistant", messages[1].Role);
            Assert.Equal(responseDto.Reply, messages[1].Content);

            // Assert: 4. AiAuditLog recorded with DeterministicGuard
            var auditLog = await dbContext.AiAuditLogs.FirstOrDefaultAsync(a => a.SessionId == sessionId);
            Assert.NotNull(auditLog);
            Assert.Equal("OutOfScope", auditLog.Intent);
            Assert.Equal(0.99f, auditLog.IntentConfidence);
            Assert.Equal("DeterministicGuard", auditLog.ModelUsed);
            Assert.Equal("[]", auditLog.KbArticlesUsedJson);
        }

        [Fact]
        public async Task ChatController_ValidChatAppQuery_CallsGemini()
        {
            // Arrange
            var dbContext = CreateInMemoryDbContext();
            var config = CreateConfiguration();
            var policy = new ChatScopePolicy(config);

            var mockKb = new Mock<IKnowledgeBaseService>();
            mockKb.Setup(k => k.GetFormattedContextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ChatApp group chat article");

            var mockGemini = new Mock<IGeminiService>();
            mockGemini.Setup(g => g.ProcessChatAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<IntentResult?>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(new GeminiChatResult("To add a member to your group, tap Group Info > Add Member.", false, null, Guid.NewGuid(), "FAQ"));

            var mockIntent = new Mock<IIntentService>();
            mockIntent.Setup(i => i.ClassifyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IntentResult("FAQ", 0.98f, "User asked about adding user to group chat"));

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.BuildMemoryContextAsync(It.IsAny<Guid>()))
                .ReturnsAsync(string.Empty);

            var mockLogger = new Mock<ILogger<ChatController>>();
            var mockScopeFactory = new Mock<IServiceScopeFactory>();

            var controller = new ChatController(
                dbContext,
                mockKb.Object,
                mockGemini.Object,
                mockIntent.Object,
                mockMemory.Object,
                mockLogger.Object,
                config,
                mockScopeFactory.Object,
                policy)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            var sessionId = Guid.NewGuid();
            var request = new ChatRequestDto
            {
                SessionId = sessionId,
                CustomerId = 1,
                DisplayName = "Bob",
                Message = "How do I add Messi to my group chat?"
            };

            // Act
            var actionResult = await controller.SendMessage(request);

            // Assert: 1. Controller returned Ok with Gemini reply
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var responseDto = Assert.IsType<ChatResponseDto>(okResult.Value);
            Assert.Contains("tap Group Info > Add Member", responseDto.Reply);

            // Assert: 2. Gemini was called exactly once
            mockGemini.Verify(g => g.ProcessChatAsync(
                sessionId,
                1,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                "How do I add Messi to my group chat?",
                It.IsAny<List<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<Guid?>(),
                It.IsAny<IntentResult?>(),
                It.IsAny<string?>(),
                "Bob"),
                Times.Once);
        }

        [Fact]
        public async Task ChatStreamController_HighConfidenceOutOfScope_DoesNotCallGemini_AndStreamsRefusal()
        {
            // Arrange
            var dbContext = CreateInMemoryDbContext();
            var config = CreateConfiguration();
            var policy = new ChatScopePolicy(config);

            var mockKb = new Mock<IKnowledgeBaseService>();
            mockKb.Setup(k => k.GetFormattedContextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("Some KB context");

            var mockGemini = new Mock<IGeminiService>();
            var mockIntent = new Mock<IIntentService>();
            mockIntent.Setup(i => i.ClassifyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IntentResult("OutOfScope", 0.99f, "Unrelated general knowledge: quantum computing"));

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.BuildMemoryContextAsync(It.IsAny<Guid>()))
                .ReturnsAsync(string.Empty);

            var mockLogger = new Mock<ILogger<ChatStreamController>>();
            var mockScopeFactory = new Mock<IServiceScopeFactory>();

            var controller = new ChatStreamController(
                dbContext,
                mockKb.Object,
                mockGemini.Object,
                mockIntent.Object,
                mockMemory.Object,
                mockLogger.Object,
                config,
                mockScopeFactory.Object,
                policy);

            var httpContext = new DefaultHttpContext();
            var responseBodyStream = new MemoryStream();
            httpContext.Response.Body = responseBodyStream;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var sessionId = Guid.NewGuid();
            var request = new ChatRequestDto
            {
                SessionId = sessionId,
                CustomerId = 1,
                DisplayName = "Charlie",
                Message = "Explain quantum computing in simple terms."
            };

            // Act
            await controller.StreamMessage(request);

            // Assert: 1. Stream output contains refusal and done event
            responseBodyStream.Seek(0, SeekOrigin.Begin);
            var sseOutput = Encoding.UTF8.GetString(responseBodyStream.ToArray());

            Assert.Contains("data: {\"chunk\":\"", sseOutput);
            Assert.Contains("ChatApp customer support assistant", sseOutput);
            Assert.Contains("data: {\"event\":\"done\"", sseOutput);
            Assert.Contains("\"ticketCreated\":false", sseOutput);

            // Assert: 2. Gemini StreamChatAsync was NEVER called
            mockGemini.Verify(g => g.StreamChatAsync(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<List<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<Guid?>(),
                It.IsAny<IntentResult?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
                Times.Never);

            // Assert: 3. Messages persisted to DB
            var messages = await dbContext.ChatMessages.Where(m => m.SessionId == sessionId).ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.Equal("user", messages[0].Role);
            Assert.Equal("Explain quantum computing in simple terms.", messages[0].Content);
            Assert.Equal("assistant", messages[1].Role);
            Assert.Contains("ChatApp customer support assistant", messages[1].Content);

            // Assert: 4. AiAuditLog recorded with DeterministicGuard
            var auditLog = await dbContext.AiAuditLogs.FirstOrDefaultAsync(a => a.SessionId == sessionId);
            Assert.NotNull(auditLog);
            Assert.Equal("OutOfScope", auditLog.Intent);
            Assert.Equal(0.99f, auditLog.IntentConfidence);
            Assert.Equal("DeterministicGuard", auditLog.ModelUsed);
            Assert.Equal("[]", auditLog.KbArticlesUsedJson);
        }

        [Fact]
        public async Task ChatStreamController_ValidSupportRequestWithExternalName_Allowed()
        {
            // Arrange
            var dbContext = CreateInMemoryDbContext();
            var config = CreateConfiguration();
            var policy = new ChatScopePolicy(config);

            var mockKb = new Mock<IKnowledgeBaseService>();
            mockKb.Setup(k => k.GetFormattedContextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("Harassment and reporting policy");

            // Mock async stream yielding chunks
            async IAsyncEnumerable<StreamChunk> GetMockChunks()
            {
                await Task.Yield();
                yield return new StreamChunk("I am sorry to hear that. I will help file a report.");
                yield return new StreamChunk(Text: null, Done: true, TicketCreated: true, TicketId: 101);
            }


            var mockGemini = new Mock<IGeminiService>();
            mockGemini.Setup(g => g.StreamChatAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<IntentResult?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
                .Returns(GetMockChunks());

            var mockIntent = new Mock<IIntentService>();
            mockIntent.Setup(i => i.ClassifyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IntentResult("CreateTicket", 0.99f, "User reported harassment by user named Sachin"));

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.BuildMemoryContextAsync(It.IsAny<Guid>()))
                .ReturnsAsync(string.Empty);

            var mockLogger = new Mock<ILogger<ChatStreamController>>();
            var mockScopeFactory = new Mock<IServiceScopeFactory>();

            var controller = new ChatStreamController(
                dbContext,
                mockKb.Object,
                mockGemini.Object,
                mockIntent.Object,
                mockMemory.Object,
                mockLogger.Object,
                config,
                mockScopeFactory.Object,
                policy);

            var httpContext = new DefaultHttpContext();
            var responseBodyStream = new MemoryStream();
            httpContext.Response.Body = responseBodyStream;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var sessionId = Guid.NewGuid();
            var request = new ChatRequestDto
            {
                SessionId = sessionId,
                CustomerId = 1,
                DisplayName = "Dave",
                Message = "A user named Sachin is harassing me. Create a support ticket."
            };

            // Act
            await controller.StreamMessage(request);

            // Assert: 1. Stream output contains ticketCreated = true
            responseBodyStream.Seek(0, SeekOrigin.Begin);
            var sseOutput = Encoding.UTF8.GetString(responseBodyStream.ToArray());

            Assert.Contains("\"ticketCreated\":true", sseOutput);
            Assert.Contains("\"ticketId\":101", sseOutput);

            // Assert: 2. Gemini was invoked
            mockGemini.Verify(g => g.StreamChatAsync(
                sessionId,
                1,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                "A user named Sachin is harassing me. Create a support ticket.",
                It.IsAny<List<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<Guid?>(),
                It.IsAny<IntentResult?>(),
                It.IsAny<string?>(),
                "Dave",
                It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }
}
