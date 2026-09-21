using System.Net;
using System.Text.Json.Nodes;
using ChatApp.API.Services;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace ChatApp.Tests
{
    public class GeminiDefenseInDepthTests
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
                { "Gemini:ApiKey", "AIzaSyFakeKeyForTesting12345" },
                { "Gemini:Model", "gemini-3.5-flash-lite" },
                { "AI:Agents:OutOfScope", "Politely decline. Do not define entities." }
            };
            return new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        }

        [Fact]
        public async Task ProcessChatAsync_OutOfScope_OmitsToolsAndKbContextFromPayload()
        {
            // Arrange
            var dbContext = CreateInMemoryDbContext();
            var config = CreateConfiguration();
            var mockTicket = new Mock<ITicketService>();
            var mockMemory = new Mock<IMemoryService>();
            var mockLogger = new Mock<ILogger<GeminiService>>();

            string capturedPayloadJson = string.Empty;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
                {
                    capturedPayloadJson = req.Content != null
                        ? await req.Content.ReadAsStringAsync(ct)
                        : string.Empty;


                    var fakeResponse = new JsonObject
                    {
                        ["candidates"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["content"] = new JsonObject
                                {
                                    ["parts"] = new JsonArray
                                    {
                                        new JsonObject { ["text"] = "I can only assist with ChatApp." }
                                    }
                                }
                            }
                        }
                    };

                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(fakeResponse.ToJsonString())
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var geminiService = new GeminiService(config, mockTicket.Object, mockMemory.Object, dbContext, mockLogger.Object, httpClient);

            var intent = new IntentResult("OutOfScope", 0.75f, "Below threshold but out of scope");

            // Act
            var result = await geminiService.ProcessChatAsync(
                sessionId: Guid.NewGuid(),
                customerId: 1,
                username: "Alice",
                email: "alice@example.com",
                userMessage: "What is quantum computing?",
                conversationHistory: new List<ChatMessage>(),
                kbContext: "SECRET KB CONTEXT THAT SHOULD NOT BE SENT",
                authenticatedUserId: null,
                intent: intent);

            // Assert
            Assert.False(string.IsNullOrEmpty(capturedPayloadJson));
            var parsedPayload = JsonNode.Parse(capturedPayloadJson);

            // Verify: tools was omitted
            Assert.Null(parsedPayload?["tools"]);

            // Verify: KB context was omitted
            var systemText = parsedPayload?["systemInstruction"]?["parts"]?[0]?["text"]?.ToString();
            Assert.NotNull(systemText);
            Assert.DoesNotContain("SECRET KB CONTEXT THAT SHOULD NOT BE SENT", systemText);
            Assert.DoesNotContain("KNOWLEDGE BASE CONTEXT:", systemText);
        }

        [Fact]
        public async Task ProcessChatAsync_FAQ_IncludesToolsAndKbContextInPayload()
        {
            // Arrange
            var dbContext = CreateInMemoryDbContext();
            var config = CreateConfiguration();
            var mockTicket = new Mock<ITicketService>();
            var mockMemory = new Mock<IMemoryService>();
            var mockLogger = new Mock<ILogger<GeminiService>>();

            string capturedPayloadJson = string.Empty;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
                {
                    capturedPayloadJson = req.Content != null
                        ? await req.Content.ReadAsStringAsync(ct)
                        : string.Empty;


                    var fakeResponse = new JsonObject
                    {
                        ["candidates"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["content"] = new JsonObject
                                {
                                    ["parts"] = new JsonArray
                                    {
                                        new JsonObject { ["text"] = "ChatApp supports group chats." }
                                    }
                                }
                            }
                        }
                    };

                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(fakeResponse.ToJsonString())
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var geminiService = new GeminiService(config, mockTicket.Object, mockMemory.Object, dbContext, mockLogger.Object, httpClient);

            var intent = new IntentResult("FAQ", 0.95f, "FAQ query");

            // Act
            var result = await geminiService.ProcessChatAsync(
                sessionId: Guid.NewGuid(),
                customerId: 1,
                username: "Alice",
                email: "alice@example.com",
                userMessage: "How do I create a group?",
                conversationHistory: new List<ChatMessage>(),
                kbContext: "GROUP CHAT KB ARTICLE DETAILS",
                authenticatedUserId: null,
                intent: intent);

            // Assert
            Assert.False(string.IsNullOrEmpty(capturedPayloadJson));
            var parsedPayload = JsonNode.Parse(capturedPayloadJson);

            // Verify: tools was included
            Assert.NotNull(parsedPayload?["tools"]);

            // Verify: KB context was included
            var systemText = parsedPayload?["systemInstruction"]?["parts"]?[0]?["text"]?.ToString();
            Assert.NotNull(systemText);
            Assert.Contains("GROUP CHAT KB ARTICLE DETAILS", systemText);
            Assert.Contains("KNOWLEDGE BASE CONTEXT:", systemText);
        }
    }
}
