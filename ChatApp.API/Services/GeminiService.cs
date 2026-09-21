using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Services
{
    /// <summary>
    /// Carries the result of one AI orchestration run.
    /// AuditLogId is populated after the audit row is written.
    /// </summary>
    public record GeminiChatResult(
        string Reply,
        bool TicketCreated,
        int? TicketId,
        Guid? AuditLogId = null,
        string? IntentUsed = null);

    /// <summary>
    /// One token-chunk yielded by the SSE streaming path.
    /// Exactly one of Text or FunctionCallName will be non-null per chunk.
    /// Done=true is the terminal sentinel.
    /// </summary>
    public record StreamChunk(
        string? Text = null,
        string? FunctionCallName = null,
        System.Text.Json.Nodes.JsonObject? FunctionCallArgs = null,
        bool Done = false,
        bool TicketCreated = false,
        int? TicketId = null,
        string? ErrorMessage = null);

    public interface IGeminiService
    {
        Task<GeminiChatResult> ProcessChatAsync(
            Guid sessionId,
            int customerId,
            string? username,
            string? email,
            string userMessage,
            List<ChatMessage> conversationHistory,
            string kbContext,
            Guid? authenticatedUserId = null,
            IntentResult? intent = null,
            string? memoryContext = null,
            string? displayName = null);

        /// <summary>
        /// Streaming variant: yields text chunks via IAsyncEnumerable.
        /// The terminal chunk has Done=true and carries ticketCreated/ticketId.
        /// Caller is responsible for persisting the assembled reply to the DB.
        /// </summary>
        IAsyncEnumerable<StreamChunk> StreamChatAsync(
            Guid sessionId,
            int customerId,
            string? username,
            string? email,
            string userMessage,
            List<ChatMessage> conversationHistory,
            string kbContext,
            Guid? authenticatedUserId = null,
            IntentResult? intent = null,
            string? memoryContext = null,
            string? displayName = null,
            System.Threading.CancellationToken cancellationToken = default);
    }

    public class GeminiService : IGeminiService
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> _clientCache = new();
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ITicketService _ticketService;
        private readonly IMemoryService _memoryService;
        private readonly ChatApp.Infrastructure.Data.ChatDbContext _dbContext;
        private readonly ILogger<GeminiService> _logger;
        private readonly string _modelName;
        private readonly string[] _fallbackModels;

        private static HttpClient GetOrCreateClient(string? fallbackIpStr)
        {
            var key = string.IsNullOrWhiteSpace(fallbackIpStr) ? "default" : fallbackIpStr.Trim();
            return _clientCache.GetOrAdd(key, _ => CreateGeminiHttpClient(fallbackIpStr));
        }

        private static HttpClient CreateGeminiHttpClient(string? fallbackIpStr = null)
        {
            IPAddress? fallbackIp = null;
            if (!string.IsNullOrWhiteSpace(fallbackIpStr) && IPAddress.TryParse(fallbackIpStr.Trim(), out var parsed))
            {
                fallbackIp = parsed;
            }

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                    var sorted = addresses
                        .OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                        .ToList();

                    // If DNS did not return IPv6 for Google APIs and a fallback IP is configured, prioritize configured fallback
                    if (fallbackIp != null &&
                        context.DnsEndPoint.Host.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase) &&
                        !sorted.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6))
                    {
                        sorted.Insert(0, fallbackIp);
                    }

                    foreach (var ip in sorted)
                    {
                        try
                        {
                            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                            {
                                NoDelay = true
                            };
                            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                            await socket.ConnectAsync(ip, context.DnsEndPoint.Port, linkedCts.Token);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            // Fall through to next available address
                        }
                    }

                    throw new SocketException((int)SocketError.HostUnreachable);
                }
            };

            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ── Agent Prompt Loader (config-driven, no hardcoded prompts) ─────────────────
        // Prompts live in appsettings.json under AI:Agents:FAQ, AI:Agents:CreateTicket, etc.
        // The monolithic SystemInstructionBase is replaced by intent-specific specialised prompts.
        private const string FallbackAgentPrompt =
            "You are the dedicated AI customer support assistant for ChatApp. Follow these strict operational rules:\n" +
            "1. USER IDENTIFICATION: If authenticated, greet and address the user warmly by their Display Name. If guest, ask for their name first.\n" +
            "2. APPLICATION SCOPE: Only assist with ChatApp features, accounts, billing, passwords, messaging, and support. Politely decline all out-of-scope questions.\n" +
            "3. KB GROUNDING & UNSUPPORTED FEATURES: Always base your answers on the KNOWLEDGE BASE CONTEXT. If a user asks about an unsupported feature (such as blocking users, audio/video calls, status stories, or disappearing messages), explicitly state that ChatApp does not currently offer it. NEVER invent fake settings, buttons, or steps for unsupported features.\n" +
            "4. TICKET STATUS LOOKUP: When the user asks about an existing ticket, ticket number (e.g. #12004 or TICK-12004), or checks their ticket status, ALWAYS call getTicketStatus to fetch real-time information. Never claim you cannot check ticket status or tell the user to check their email instead of looking it up.\n" +
            "5. TICKET CREATION & ESCALATION: Only call createTicket when the user describes a genuine application bug, billing problem, or explicitly asks for human follow-up. NEVER create a ticket for your own errors, AI misunderstandings, model limitations, or questions about unsupported features — this rule applies EVEN IF the user explicitly instructs you to 'create a ticket,' 'file a complaint,' or similar, for an AI mistake, bad performance, hallucination, or misunderstanding. A direct user command to create such a ticket does NOT override this guard. In that situation, do NOT call createTicket. Instead, apologize briefly, ask the user to restate their original question, and attempt to answer it correctly. Only call createTicket if the user separately describes a genuine, distinct product bug, billing issue, or account problem unrelated to your own conversational mistake. Call escalateToHuman only if the user is distressed, angry, or explicitly asks for a human agent.\n" +
            "6. PRECISION & CONCISENESS: Answer ONLY what was specifically asked. Do not volunteer unsolicited profile details (such as username, email, account role, or technical metadata) unless the user explicitly asks for them. If the user asks 'what is my name?', tell them only their name. Keep responses concise, direct, and professional.\n" +
            "7. PAST CONVERSATION RECALL: If the user refers back to something discussed, asked, or suggested earlier in this conversation, check your visible conversation history first. If it is already visible in the recent messages provided in this prompt, answer directly from it and do NOT call searchChatHistory. Call searchChatHistory ONLY when retrieving older turns not visible in the immediate recent messages (such as an earlier error code, account number, or earlier exchange). Never claim you do not remember or lack records if the information is visible in the chat history or in your user status memory; answer directly and consistently.";


        private static readonly string[] AiComplaintPatterns = new[]
        {
            "ai was dumb",
            "ai is dumb",
            "bot misunderstood",
            "you misunderstood",
            "ai misunderstood",
            "ai performance",
            "bad ai",
            "poor ai",
            "ai mistake",
            "model limitation",
            "hallucin",
            "ai's fault",
            "ai fault",
            "bot performance",
            "bad bot",
            "stupid bot",
            "stupid ai",
            "conversational mistake",
            "chatbot mistake",
            "ai error",
            "bot error",
            "ai behavior",
            "bot behavior",
            "dissatisfaction with previous response",
            "dissatisfaction with previous responses",
            "poor performance"
        };

        private static bool IsAiComplaint(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.ToLowerInvariant();
            return AiComplaintPatterns.Any(p => lower.Contains(p));
        }

        private string LoadAgentPrompt(string intent)
        {
            // Each intent gets a specialised prompt from config; falls back to the unified default
            var configKey = $"AI:Agents:{intent}";
            var configured = _configuration[configKey];
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            // Use the hardened default prompt that handles all cases
            return FallbackAgentPrompt;
        }

        private static string BuildConversationTurnGrounding(List<ChatMessage>? conversationHistory)
        {
            if (conversationHistory == null || conversationHistory.Count == 0)
            {
                return "\n\nCONVERSATION HISTORY GROUNDING:\n" +
                       "- This is the very beginning of the session (Turn 1). There are 0 previous turns.\n" +
                       "- The user has NOT asked any previous questions in this session yet.\n" +
                       "- If the user asks what their first question, previous question, or earlier question was, inform them that this current message is their very first question in this conversation.\n" +
                       "- If the user asks whether you remember them or whether you chatted earlier (e.g. 'do you remember me', 'we had some chatting earlier'), explicitly inform them that this is a fresh conversation session and you have no record of previous chats.";
            }

            var sb = new StringBuilder("\n\nCONVERSATION HISTORY GROUNDING & CHRONOLOGICAL USER TURNS:\n");
            int turn = 1;
            for (int i = 0; i < conversationHistory.Count; i++)
            {
                var msg = conversationHistory[i];
                if (msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    var timeStr = msg.CreatedAt.ToString("HH:mm:ss 'UTC'");
                    sb.AppendLine($"Turn {turn} [{timeStr}]: \"{msg.Content}\"");
                    turn++;
                }
            }

            if (turn == 1)
            {
                sb.AppendLine("- No prior user turns were recorded in this session yet.");
            }
            else
            {
                sb.AppendLine("- When the user asks about their 'first question', 'previous question', or what they asked at a specific time, refer strictly to the chronological turn numbers and timestamps above.");
                sb.AppendLine("- Note: Turn 1 is literally the very first question asked above. Do not skip meta-questions or conversational turns.");
            }

            return sb.ToString();
        }

        public GeminiService(
            IConfiguration configuration,
            ITicketService ticketService,
            IMemoryService memoryService,
            ChatApp.Infrastructure.Data.ChatDbContext dbContext,
            ILogger<GeminiService> logger,
            HttpClient? httpClient = null)
        {
            _configuration = configuration;
            _ticketService = ticketService;
            _memoryService = memoryService;
            _dbContext = dbContext;
            _logger = logger;
            _modelName = configuration["Gemini:Model"] ?? "gemini-3.1-flash-lite";

            var configuredFallbacks = configuration.GetSection("Gemini:FallbackModels").Get<string[]>();
            _fallbackModels = (configuredFallbacks != null && configuredFallbacks.Length > 0)
                ? configuredFallbacks
                : new[] { "gemini-3.1-flash-lite", "gemini-3.5-flash-lite", "gemini-3.7-flash", "gemini-3.8-flash" };

            var configuredFallbackIp = configuration["Network:GeminiFallbackIp"];
            _httpClient = httpClient ?? GetOrCreateClient(configuredFallbackIp);
        }

        public async Task<GeminiChatResult> ProcessChatAsync(
            Guid sessionId,
            int customerId,
            string? username,
            string? email,
            string userMessage,
            List<ChatMessage> conversationHistory,
            string kbContext,
            Guid? authenticatedUserId = null,
            IntentResult? intent = null,
            string? memoryContext = null,
            string? displayName = null)
        {
            var apiKey = _configuration["GEMINI_API_KEY"] 
                         ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") 
                         ?? _configuration["Gemini:ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_GEMINI", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("GEMINI_API_KEY is not configured. Returning offline mock response for testing.");
                return new GeminiChatResult(
                    "I am currently operating in offline demonstration mode because the Gemini API key has not been configured yet. Please configure GEMINI_API_KEY in appsettings.json or environment variables.",
                    false,
                    null,
                    AuditLogId: null,
                    IntentUsed: intent?.Intent ?? "FAQ");
            }

            var orchestrationSw = System.Diagnostics.Stopwatch.StartNew();
            var resolvedIntent = intent?.Intent ?? "FAQ";

            try
            {
                var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_modelName}:generateContent?key={apiKey}";

                // 1. Build System Instruction: intent-specific agent prompt + identity + KB context + session memory
                // Agent prompt is loaded from config (AI:Agents:<Intent>) with FallbackAgentPrompt as default.
                var systemInstructionText = LoadAgentPrompt(resolvedIntent);

                if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(displayName))
                {
                    var nameToAddress = !string.IsNullOrWhiteSpace(displayName) ? displayName : username;
                    var handleInfo = (!string.IsNullOrWhiteSpace(username) && !string.Equals(username, displayName, StringComparison.OrdinalIgnoreCase))
                        ? $", Username/Handle: '@{username}'"
                        : "";
                    systemInstructionText += $"\n\nCURRENT USER STATUS: Authenticated user. Display Name: '{nameToAddress}'{handleInfo} (Email: '{email ?? "N/A"}'). " +
                        $"Address them warmly by their Display Name '{nameToAddress}'. " +
                        $"If the user specifically asks 'what is my name?', answer with ONLY their Display Name '{nameToAddress}'. " +
                        $"Only provide their username/handle '@{username}' if they specifically ask for their username or handle. " +
                        $"Do not dump unasked profile fields. You already know who they are — do NOT ask for their name or username.";
                }
                else
                {
                    systemInstructionText += "\n\nCURRENT USER STATUS: Unauthenticated guest. If the user has not yet given their name/username in this chat, ask them for their name or username first before answering their questions.";
                }

                systemInstructionText += "\n\nACTIVE CONTEXT VISIBILITY & RECALL RULE:\n" +
                    "- The recent conversation messages are provided directly in this prompt's contents. If the user asks about an earlier question, answer, or exchange (e.g. 'what was my first question?', 'what answer did you give?'), examine the visible conversation history first.\n" +
                    "- If the exchange is visible in the conversation history provided in this prompt, answer directly from it. Do NOT call searchChatHistory for information already visible to you.\n" +
                    "- Only call searchChatHistory if the requested turn is not present in the visible conversation history.\n\n" +
                    "INTERNAL CONSISTENCY GUARD:\n" +
                    "- NEVER state that you have no record of an exchange or cannot recall an earlier interaction while simultaneously stating the fact/answer itself in the same reply.\n" +
                    "- If you know the answer (from visible history, memory context, or user status), answer the question directly and authoritatively without apologizing or claiming memory loss.";

                systemInstructionText += BuildConversationTurnGrounding(conversationHistory);

                // Inject session working memory (user name, confirmed issue, etc.)
                if (!string.IsNullOrWhiteSpace(memoryContext))
                {
                    systemInstructionText += "\n\n" + memoryContext;
                }

                var isOutOfScope = string.Equals(resolvedIntent, "OutOfScope", StringComparison.OrdinalIgnoreCase);

                if (!isOutOfScope && !string.IsNullOrWhiteSpace(kbContext))
                {
                    systemInstructionText += "\n\nKNOWLEDGE BASE CONTEXT:\n" + kbContext;
                }

                // 2. Build Contents list (conversation history + new user message)
                var contents = new JsonArray();

                foreach (var msg in conversationHistory.TakeLast(20))
                {
                    var role = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
                    contents.Add(new JsonObject
                    {
                        ["role"] = role,
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = msg.Content }
                        }
                    });
                }

                // Append current user message
                contents.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = userMessage }
                    }
                });

                // 3. Define Tools (createTicket + escalateToHuman functions)
                var tools = new JsonArray
                {
                    new JsonObject
                    {
                        ["functionDeclarations"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "createTicket",
                                ["description"] = "Creates a support ticket when the customer reports a genuine issue, complaint, or bug requiring human follow-up",
                                ["parameters"] = new JsonObject
                                {
                                    ["type"] = "OBJECT",
                                    ["properties"] = new JsonObject
                                    {
                                        ["issue"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "Summary of the customer's problem"
                                        },
                                        ["category"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "one of: billing, technical, account, other"
                                        },
                                        ["priority"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "Evaluated gravity: 'high' for core blockers (e.g. unable to type in chat window, cannot send messages, crash, billing charges), 'medium' for non-blocking bugs, 'low' for cosmetic/inquiries"
                                        },
                                        ["customerName"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "Name or username of the customer creating the ticket"
                                        },
                                        ["customerEmail"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "Email address of the customer to receive ticket updates"
                                        }
                                    },
                                    ["required"] = new JsonArray { "issue", "category", "priority" }
                                }
                            },
                            new JsonObject
                            {
                                ["name"] = "escalateToHuman",
                                ["description"] = "Immediately escalates the conversation to a human support agent when the user is visibly frustrated, angry, distressed, or explicitly asks for a human. Creates a HIGH priority ticket automatically.",
                                ["parameters"] = new JsonObject
                                {
                                    ["type"] = "OBJECT",
                                    ["properties"] = new JsonObject
                                    {
                                        ["reason"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "Brief reason for escalation (e.g. 'User expressed severe frustration after 3 failed attempts')"
                                        },
                                        ["urgency"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "one of: high, critical"
                                        },
                                        ["customerName"] = new JsonObject { ["type"] = "STRING", ["description"] = "Customer name if known" },
                                        ["customerEmail"] = new JsonObject { ["type"] = "STRING", ["description"] = "Customer email if known" }
                                    },
                                    ["required"] = new JsonArray { "reason", "urgency" }
                                }
                            },
                            new JsonObject
                            {
                                ["name"] = "getTicketStatus",
                                ["description"] = "Retrieves the real-time status, priority, and details of an existing support ticket. Call this tool ONLY when the user's message explicitly names a specific ticket (e.g. '#12004', 'TICK-12004') or unambiguously asks 'what is the status of my ticket(s)?' with no other topic mixed into the same message. NEVER call this tool in response to billing, discount, refund, or 'you promised' language - those must go through searchChatHistory first per the prior-commitment rule. Do not infer a ticket ID from memory or prior context unless the user's current message itself references a ticket.",
                                ["parameters"] = new JsonObject
                                {
                                    ["type"] = "OBJECT",
                                    ["properties"] = new JsonObject
                                    {
                                        ["ticketIdOrRef"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "Ticket ID or reference (e.g. '12004', '#12004', 'TICK-12004'). Can be omitted if user asks for their latest ticket."
                                        }
                                    }
                                }
                            },
                            new JsonObject
                            {
                                ["name"] = "searchChatHistory",
                                ["description"] = "Searches earlier conversation messages from this chat session that may have scrolled off the immediate visible history. IMPORTANT: Do NOT call this tool if the exchange, question, or answer is already visible in the recent conversation messages provided in the prompt. Call this tool only when the user references something said, asked, suggested, or promised earlier in the conversation that is NOT visible in the immediate recent messages. This tool takes priority over getTicketStatus when a message contains both a prior-commitment claim and ticket-adjacent language.",
                                ["parameters"] = new JsonObject
                                {
                                    ["type"] = "OBJECT",
                                    ["properties"] = new JsonObject
                                    {
                                        ["query"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "Keywords, error messages, topics, or numbers to search for in earlier conversation turns."
                                        }
                                    },
                                    ["required"] = new JsonArray { "query" }
                                }
                            }
                        }
                    }
                };

                // 4. Construct Request Payload
                var requestBody = new JsonObject
                {
                    ["systemInstruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = systemInstructionText }
                        }
                    },
                    ["contents"] = contents
                };
                if (!isOutOfScope)
                {
                    requestBody["tools"] = tools;
                }


                var (firstResponse, workingModel, isRateLimited) = await SendGeminiRequestWithFailoverAsync(apiKey, requestBody);
                if (firstResponse == null)
                {
                    if (isRateLimited)
                    {
                        return new GeminiChatResult(
                            "I'm temporarily paused because the Google Gemini free-tier quota (20 requests/day per model) on your API key has been exhausted. Please wait for the daily quota window to reset, or enable pay-as-you-go in Google AI Studio for unlimited access.",
                            false,
                            null);
                    }

                    return new GeminiChatResult("I'm having trouble connecting to the support assistant right now. Please try again later.", false, null);
                }

                // 5. Inspect response for function calls
                var candidate = firstResponse["candidates"]?[0];
                var contentObj = candidate?["content"];
                var parts = contentObj?["parts"]?.AsArray();
                var functionCallPart = parts?.FirstOrDefault(p => p?["functionCall"] != null)?.AsObject();

                if (functionCallPart != null && functionCallPart.ContainsKey("functionCall"))
                {
                    var functionCall = functionCallPart["functionCall"]?.AsObject();
                    var functionName = functionCall?["name"]?.ToString();

                    // ── escalateToHuman ──
                    if (string.Equals(functionName, "escalateToHuman", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = functionCall?["args"]?.AsObject();
                        var reason = args?["reason"]?.ToString() ?? userMessage;
                        var urgency = args?["urgency"]?.ToString() ?? "high";
                        var escalateName = args?["customerName"]?.ToString() ?? username;
                        var escalateEmail = args?["customerEmail"]?.ToString() ?? email;

                        _logger.LogWarning("Gemini triggered escalateToHuman: Reason='{Reason}', Urgency='{Urgency}', User='{User}'",
                            reason, urgency, escalateName ?? "Guest");

                        var escalationIssue = $"[URGENT ESCALATION - {urgency.ToUpperInvariant()}] {reason}";
                        var ticket = await _ticketService.CreateTicketAsync(
                            customerId, escalationIssue, "technical", "high",
                            escalateName, escalateEmail, authenticatedUserId);

                        orchestrationSw.Stop();
                        var auditId1 = await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            $"TICK-{ticket.Id}", userMessage);

                        return new GeminiChatResult(
                            $"I can see you're having a really difficult time, and I sincerely apologize for the frustration. I've immediately escalated your case to our senior support team with **HIGH priority** — Ticket **#{ticket.Id}** has been created and our team will contact you as soon as possible. You deserve faster help than I can provide right now.",
                            true, ticket.Id, AuditLogId: auditId1, IntentUsed: resolvedIntent);
                    }

                    // ── createTicket ──
                    if (string.Equals(functionName, "createTicket", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = functionCall?["args"]?.AsObject();
                        var issue = args?["issue"]?.ToString() ?? userMessage;
                        var category = args?["category"]?.ToString() ?? "other";
                        var priority = args?["priority"]?.ToString() ?? "medium";
                        var ticketCustomerName = args?["customerName"]?.ToString() ?? username;
                        var ticketCustomerEmail = args?["customerEmail"]?.ToString() ?? email;

                        // Code-level safety backstop: intercept complaints about AI itself
                        if (IsAiComplaint(issue) || IsAiComplaint(userMessage))
                        {
                            _logger.LogWarning("ProcessChatAsync: createTicket code backstop triggered! Issue='{Issue}', UserMessage='{UserMessage}'. Ticket creation declined.", issue, userMessage);
                            orchestrationSw.Stop();
                            var auditIdDecline = await WriteAuditLogAsync(sessionId, resolvedIntent,
                                intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                                kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                                null, userMessage);

                            var declineReply = "I apologize for misunderstanding your question earlier! I cannot create a support ticket for my own conversational mistake or AI performance, but I would like to help get this right. Could you please restate your original question? If you are experiencing a genuine issue with ChatApp (such as an application bug, billing problem, or account issue), please let me know and I will be happy to create a ticket for our team.";

                            return new GeminiChatResult(declineReply, false, null, AuditLogId: auditIdDecline, IntentUsed: resolvedIntent);
                        }

                        _logger.LogInformation("Gemini triggered createTicket: Issue='{Issue}', Category='{Category}', Priority='{Priority}', Requester='{Requester}', Email='{Email}', UserId='{UserId}'", 
                            issue, category, priority, ticketCustomerName ?? "Guest", ticketCustomerEmail ?? "N/A", authenticatedUserId?.ToString() ?? "Guest");

                        // Execute server-side Ticket creation and email
                        var ticket = await _ticketService.CreateTicketAsync(customerId, issue, category, priority, ticketCustomerName, ticketCustomerEmail, authenticatedUserId);

                        // 6. Send function response back to Gemini for the final natural-language answer
                        contents.Add(JsonNode.Parse(contentObj!.ToJsonString())!);

                        contents.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["functionResponse"] = new JsonObject
                                    {
                                        ["name"] = "createTicket",
                                        ["response"] = new JsonObject
                                        {
                                            ["ticketId"] = ticket.Id,
                                            ["status"] = "Created",
                                            ["message"] = $"Ticket #{ticket.Id} successfully created. Our team will follow up within 24 hours."
                                        }
                                    }
                                }
                            }
                        });

                        systemInstructionText +=
                            "\n\nVERIFICATION BEFORE REPLYING: Compare the tool result you just received against the " +
                            "user's actual question. If the tool result's subject does not match what the user asked " +
                            "(for example, the user asked about a billing refund but the retrieved ticket is about an " +
                            "unrelated feature request), do NOT present the tool result as the answer. Instead, tell the " +
                            "user plainly that you did not find something matching their actual question, and address " +
                            "their real question directly (referencing the KNOWLEDGE BASE CONTEXT or offering to create " +
                            "a ticket, as appropriate).";

                        var followUpRequestBody = new JsonObject
                        {
                            ["systemInstruction"] = new JsonObject
                            {
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject { ["text"] = systemInstructionText }
                                }
                            },
                            ["contents"] = JsonNode.Parse(contents.ToJsonString())
                        };

                        var followUpEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{workingModel ?? _modelName}:generateContent?key={apiKey}";
                        var (followUpResponse, _) = await SendSingleGeminiRequestAsync(followUpEndpoint, followUpRequestBody);
                        var followUpParts = followUpResponse?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                        var followUpReply = followUpParts?.FirstOrDefault(p => p?["text"] != null && p?["thought"] == null)?["text"]?.ToString()
                                            ?? followUpParts?.FirstOrDefault(p => p?["text"] != null)?["text"]?.ToString();

                        if (string.IsNullOrWhiteSpace(followUpReply))
                        {
                            followUpReply = $"I have created support ticket #{ticket.Id} for you. Our team will contact you within 24 hours.";
                        }

                        orchestrationSw.Stop();
                        var auditId2 = await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            $"TICK-{ticket.Id}", userMessage);

                        return new GeminiChatResult(followUpReply, true, ticket.Id,
                            AuditLogId: auditId2, IntentUsed: resolvedIntent);
                    }

                    // ── getTicketStatus ──
                    if (string.Equals(functionName, "getTicketStatus", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = functionCall?["args"]?.AsObject();
                        var ticketRef = args?["ticketIdOrRef"]?.ToString();
                        var statusResult = await _ticketService.GetTicketStatusAsync(ticketRef, authenticatedUserId, email);

                        _logger.LogInformation("Gemini triggered getTicketStatus: Ref='{Ref}', Found={Found}, Status='{Status}'",
                            ticketRef ?? "latest", statusResult.Found, statusResult.Status ?? "N/A");

                        contents.Add(JsonNode.Parse(contentObj!.ToJsonString())!);
                        contents.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["functionResponse"] = new JsonObject
                                    {
                                        ["name"] = "getTicketStatus",
                                        ["response"] = new JsonObject
                                        {
                                            ["found"] = statusResult.Found,
                                            ["referenceNumber"] = statusResult.ReferenceNumber,
                                            ["ticketId"] = statusResult.TicketId,
                                            ["status"] = statusResult.Status,
                                            ["priority"] = statusResult.Priority,
                                            ["category"] = statusResult.Category,
                                            ["subject"] = statusResult.Subject,
                                            ["createdAt"] = statusResult.CreatedAt?.ToString("o"),
                                            ["details"] = statusResult.Message
                                        }
                                    }
                                }
                            }
                        });

                        systemInstructionText +=
                            "\n\nVERIFICATION BEFORE REPLYING: Compare the tool result you just received against the " +
                            "user's actual question. If the tool result's subject does not match what the user asked " +
                            "(for example, the user asked about a billing refund but the retrieved ticket is about an " +
                            "unrelated feature request), do NOT present the tool result as the answer. Instead, tell the " +
                            "user plainly that you did not find something matching their actual question, and address " +
                            "their real question directly (referencing the KNOWLEDGE BASE CONTEXT or offering to create " +
                            "a ticket, as appropriate).";

                        var followUpRequestBody = new JsonObject
                        {
                            ["systemInstruction"] = new JsonObject
                            {
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject { ["text"] = systemInstructionText }
                                }
                            },
                            ["contents"] = JsonNode.Parse(contents.ToJsonString())
                        };

                        var followUpEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{workingModel ?? _modelName}:generateContent?key={apiKey}";
                        var (followUpResponse, _) = await SendSingleGeminiRequestAsync(followUpEndpoint, followUpRequestBody);
                        var followUpParts = followUpResponse?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                        var followUpReply = followUpParts?.FirstOrDefault(p => p?["text"] != null && p?["thought"] == null)?["text"]?.ToString()
                                            ?? followUpParts?.FirstOrDefault(p => p?["text"] != null)?["text"]?.ToString()
                                            ?? statusResult.Message;

                        orchestrationSw.Stop();
                        var auditId = await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            statusResult.ReferenceNumber, userMessage);

                        return new GeminiChatResult(followUpReply, false, statusResult.TicketId,
                            AuditLogId: auditId, IntentUsed: resolvedIntent);
                    }

                    // ── searchChatHistory ──
                    if (string.Equals(functionName, "searchChatHistory", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = functionCall?["args"]?.AsObject();
                        var query = args?["query"]?.ToString() ?? userMessage;
                        var pastExcerpts = await _memoryService.RetrievePastMessagesAsync(sessionId, query);

                        _logger.LogInformation("Gemini triggered searchChatHistory: Query='{Query}', Found={Count}",
                            query, pastExcerpts.Count);

                        var excerptsArray = new JsonArray();
                        foreach (var ex in pastExcerpts)
                        {
                            excerptsArray.Add(new JsonObject
                            {
                                ["turn"] = ex.TurnIndex,
                                ["speaker"] = ex.Role,
                                ["message"] = ex.Content,
                                ["timestamp"] = ex.Timestamp.ToString("o")
                            });
                        }

                        contents.Add(JsonNode.Parse(contentObj!.ToJsonString())!);
                        contents.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["functionResponse"] = new JsonObject
                                    {
                                        ["name"] = "searchChatHistory",
                                        ["response"] = new JsonObject
                                        {
                                            ["query"] = query,
                                            ["found"] = pastExcerpts.Count > 0,
                                            ["matchCount"] = pastExcerpts.Count,
                                            ["results"] = excerptsArray,
                                            ["notes"] = pastExcerpts.Count > 0 
                                                ? "These are verbatim past messages matching the query." 
                                                : "No older messages matching the query were found outside the visible recent history. Note: The recent turns are already provided directly in the conversation context."
                                        }
                                    }
                                }
                            }
                        });

                        systemInstructionText +=
                            "\n\nVERIFICATION & CONSISTENCY RULES:\n" +
                            "1. Compare the tool result against the user's actual question. If the tool result's subject does not match what the user asked, do NOT present the tool result as the answer. Instead, tell the user plainly that you did not find something matching their actual question, and address their real question directly.\n" +
                            "2. INTERNAL CONSISTENCY GUARD: Never claim you have no record of an interaction if the information is visible in the conversation context or known from user status/memory. Never pair an apology about having no record with an answer that contains the very information requested. If you know the answer or it is in the context, state it directly and consistently.";

                        var followUpRequestBody = new JsonObject
                        {
                            ["systemInstruction"] = new JsonObject
                            {
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject { ["text"] = systemInstructionText }
                                }
                            },
                            ["contents"] = JsonNode.Parse(contents.ToJsonString())
                        };

                        var followUpEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{workingModel ?? _modelName}:generateContent?key={apiKey}";
                        var (followUpResponse, _) = await SendSingleGeminiRequestAsync(followUpEndpoint, followUpRequestBody);
                        var followUpParts = followUpResponse?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                        var followUpReply = followUpParts?.FirstOrDefault(p => p?["text"] != null && p?["thought"] == null)?["text"]?.ToString()
                                            ?? followUpParts?.FirstOrDefault(p => p?["text"] != null)?["text"]?.ToString();

                        if (string.IsNullOrWhiteSpace(followUpReply))
                        {
                            followUpReply = pastExcerpts.Count > 0
                                ? $"Earlier in our chat, you mentioned: \"{pastExcerpts.First().Content}\""
                                : "I searched our earlier messages but couldn't find a matching reference.";
                        }

                        orchestrationSw.Stop();
                        var auditId = await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            null, userMessage);

                        return new GeminiChatResult(followUpReply, false, null,
                            AuditLogId: auditId, IntentUsed: resolvedIntent);
                    }
                }


                // Normal natural-language response (skipping thought blocks if present)
                var naturalReply = parts?.FirstOrDefault(p => p?["text"] != null && p?["thought"] == null)?["text"]?.ToString()
                                   ?? parts?.FirstOrDefault(p => p?["text"] != null)?["text"]?.ToString()
                                   ?? "I'm sorry, I couldn't process your request. How else can I assist you?";

                orchestrationSw.Stop();
                var auditId3 = await WriteAuditLogAsync(sessionId, resolvedIntent,
                    intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                    kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                    null, userMessage);

                return new GeminiChatResult(naturalReply, false, null,
                    AuditLogId: auditId3, IntentUsed: resolvedIntent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in GeminiService.ProcessChatAsync");
                return new GeminiChatResult("I experienced an unexpected issue processing your request. Please try again or submit a support ticket.", false, null,
                    AuditLogId: null, IntentUsed: resolvedIntent);
            }
        }

        /// <summary>
        /// Writes one AiAuditLog row for the completed orchestration run.
        /// Failure is non-fatal — any exception is swallowed and logged so the chat response is never blocked.
        /// </summary>
        private async Task<Guid?> WriteAuditLogAsync(
            Guid sessionId,
            string intent,
            float intentConfidence,
            string modelUsed,
            string kbContext,
            int latencyMs,
            string? ticketRef,
            string userMessage)
        {
            try
            {
                // Extract KB topic names from the formatted context string
                var kbTopics = System.Text.RegularExpressions.Regex
                    .Matches(kbContext ?? string.Empty, @"\[Topic: ([^\]]+)\]")
                    .Select(m => m.Groups[1].Value)
                    .ToList();

                var auditLog = new ChatApp.Domain.Entities.AiAuditLog
                {
                    Id = Guid.NewGuid(),
                    SessionId = sessionId,
                    Timestamp = DateTime.UtcNow,
                    Intent = intent,
                    IntentConfidence = intentConfidence,
                    KbArticlesUsedJson = System.Text.Json.JsonSerializer.Serialize(kbTopics),
                    ModelUsed = modelUsed,
                    LatencyMs = latencyMs,
                    TicketRef = ticketRef,
                    UserMessageSnippet = userMessage.Length > 500 ? userMessage[..497] + "..." : userMessage
                };

                _dbContext.AiAuditLogs.Add(auditLog);
                await _dbContext.SaveChangesAsync();
                return auditLog.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write AiAuditLog for session {SessionId} — continuing without audit record.", sessionId);
                return null;
            }
        }

        private async Task<(JsonObject? Response, string? WorkingModel, bool IsRateLimited)> SendGeminiRequestWithFailoverAsync(
            string apiKey, 
            JsonObject payload)
        {
            var candidateModels = new List<string> { _modelName };
            foreach (var m in _fallbackModels)
            {
                if (!candidateModels.Contains(m, StringComparer.OrdinalIgnoreCase))
                {
                    candidateModels.Add(m);
                }
            }

            var allRateLimited = true;

            for (int i = 0; i < candidateModels.Count; i++)
            {
                var model = candidateModels[i];
                var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                var (responseObj, statusCode) = await SendSingleGeminiRequestAsync(endpoint, payload);

                if (responseObj != null)
                {
                    if (i > 0)
                    {
                        _logger.LogWarning("Gemini request succeeded using fallback model '{WorkingModel}' after previous model(s) failed.", model);
                    }
                    return (responseObj, model, false);
                }

                // If temporary 503 (high demand), pause briefly and retry once before skipping to the next model
                if (statusCode == (int)System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    await Task.Delay(300);
                    var (retryObj, retryStatus) = await SendSingleGeminiRequestAsync(endpoint, payload);
                    if (retryObj != null)
                    {
                        if (i > 0)
                        {
                            _logger.LogWarning("Gemini request succeeded using fallback model '{WorkingModel}' on retry.", model);
                        }
                        return (retryObj, model, false);
                    }
                    statusCode = retryStatus;
                }

                if (i < candidateModels.Count - 1)
                {
                    _logger.LogWarning("Gemini model '{CurrentModel}' failed with status {StatusCode}. Attempting fallback model '{NextModel}'...",
                        model, statusCode, candidateModels[i + 1]);
                }

                if (statusCode != 429 && statusCode != (int)System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    allRateLimited = false;
                }
            }

            return (null, null, allRateLimited);
        }

        private async Task<(JsonObject? Response, int StatusCode)> SendSingleGeminiRequestAsync(string endpoint, JsonObject payload)
        {
            var jsonString = payload.ToJsonString();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending Gemini request to {Endpoint} (payload size {Len} bytes)...", endpoint.Split('?')[0], jsonString.Length);
                var response = await _httpClient.PostAsync(endpoint, content, cts.Token);
                sw.Stop();
                var statusCode = (int)response.StatusCode;
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Gemini API call to {Endpoint} succeeded in {ElapsedMs}ms", endpoint.Split('?')[0], sw.ElapsedMilliseconds);
                    return (JsonNode.Parse(responseContent)?.AsObject(), statusCode);
                }

                _logger.LogWarning("Gemini API call to {Endpoint} returned status {StatusCode} in {ElapsedMs}ms: {Response}", 
                    endpoint.Split('?')[0], statusCode, sw.ElapsedMilliseconds, responseContent);

                return (null, statusCode);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _logger.LogWarning("Gemini request timed out after {ElapsedMs}ms: {Endpoint}", sw.ElapsedMilliseconds, endpoint.Split('?')[0]);
                return (null, 408);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "HTTP exception calling Gemini endpoint after {ElapsedMs}ms: {Endpoint}", sw.ElapsedMilliseconds, endpoint.Split('?')[0]);
                return (null, 500);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Phase 3 — SSE Streaming path
        // ══════════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            Guid sessionId,
            int customerId,
            string? username,
            string? email,
            string userMessage,
            List<ChatMessage> conversationHistory,
            string kbContext,
            Guid? authenticatedUserId = null,
            IntentResult? intent = null,
            string? memoryContext = null,
            string? displayName = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            System.Threading.CancellationToken cancellationToken = default)
        {
            var apiKey = _configuration["GEMINI_API_KEY"]
                         ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                         ?? _configuration["Gemini:ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_GEMINI", StringComparison.OrdinalIgnoreCase))
            {
                yield return new StreamChunk(Text: "I am operating in offline mode — please configure GEMINI_API_KEY.");
                yield return new StreamChunk(Done: true);
                yield break;
            }

            var resolvedIntent = intent?.Intent ?? "FAQ";

            // ── Build system instruction (identical to ProcessChatAsync) ──────────
            var systemInstructionText = LoadAgentPrompt(resolvedIntent);

            if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(displayName))
            {
                var nameToAddress = !string.IsNullOrWhiteSpace(displayName) ? displayName : username;
                var handleInfo = (!string.IsNullOrWhiteSpace(username) && !string.Equals(username, displayName, StringComparison.OrdinalIgnoreCase))
                    ? $", Username/Handle: '@{username}'"
                    : "";
                systemInstructionText += $"\n\nCURRENT USER STATUS: Authenticated user. Display Name: '{nameToAddress}'{handleInfo} (Email: '{email ?? "N/A"}'). " +
                    $"Address them warmly by their Display Name '{nameToAddress}'. " +
                    $"If the user specifically asks 'what is my name?', answer with ONLY their Display Name '{nameToAddress}'. " +
                    $"Only provide their username/handle '@{username}' if they specifically ask for their username or handle. " +
                    $"Do not dump unasked profile fields. You already know who they are — do NOT ask for their name or username.";
            }
            else
            {
                systemInstructionText += "\n\nCURRENT USER STATUS: Unauthenticated guest. If the user has not yet given their name/username in this chat, ask them for their name or username first before answering their questions.";
            }

            systemInstructionText += "\n\nACTIVE CONTEXT VISIBILITY & RECALL RULE:\n" +
                "- The recent conversation messages are provided directly in this prompt's contents. If the user asks about an earlier question, answer, or exchange (e.g. 'what was my first question?', 'what answer did you give?'), examine the visible conversation history first.\n" +
                "- If the exchange is visible in the conversation history provided in this prompt, answer directly from it. Do NOT call searchChatHistory for information already visible to you.\n" +
                "- Only call searchChatHistory if the requested turn is not present in the visible conversation history.\n\n" +
                "INTERNAL CONSISTENCY GUARD:\n" +
                "- NEVER state that you have no record of an exchange or cannot recall an earlier interaction while simultaneously stating the fact/answer itself in the same reply.\n" +
                "- If you know the answer (from visible history, memory context, or user status), answer the question directly and authoritatively without apologizing or claiming memory loss.";

            systemInstructionText += BuildConversationTurnGrounding(conversationHistory);

            if (!string.IsNullOrWhiteSpace(memoryContext))
                systemInstructionText += "\n\n" + memoryContext;

            var isOutOfScope = string.Equals(intent?.Intent ?? "FAQ", "OutOfScope", StringComparison.OrdinalIgnoreCase);

            if (!isOutOfScope && !string.IsNullOrWhiteSpace(kbContext))
                systemInstructionText += "\n\nKNOWLEDGE BASE CONTEXT:\n" + kbContext;


            // ── Build contents (history + current message) ───────────────────────
            var contents = new JsonArray();
            foreach (var msg in conversationHistory.TakeLast(20))
            {
                var role = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
                contents.Add(new JsonObject
                {
                    ["role"] = role,
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = msg.Content } }
                });
            }
            contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = userMessage } }
            });

            // ── Tools declaration ────────────────────────────────────────────────
            var tools = new JsonArray
            {
                new JsonObject
                {
                    ["functionDeclarations"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "createTicket",
                            ["description"] = "Creates a support ticket when the customer reports a genuine issue, complaint, or bug requiring human follow-up",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["issue"]         = new JsonObject { ["type"] = "STRING", ["description"] = "Summary of the customer's problem" },
                                    ["category"]      = new JsonObject { ["type"] = "STRING", ["description"] = "one of: billing, technical, account, other" },
                                    ["priority"]      = new JsonObject { ["type"] = "STRING", ["description"] = "high | medium | low" },
                                    ["customerName"]  = new JsonObject { ["type"] = "STRING", ["description"] = "Name or username of the customer" },
                                    ["customerEmail"] = new JsonObject { ["type"] = "STRING", ["description"] = "Email address of the customer" }
                                },
                                ["required"] = new JsonArray { "issue", "category", "priority" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "escalateToHuman",
                            ["description"] = "Immediately escalates the conversation to a human support agent when the user is visibly frustrated, angry, distressed, or explicitly asks for a human. Creates a HIGH priority ticket automatically.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["reason"]        = new JsonObject { ["type"] = "STRING", ["description"] = "Brief reason for escalation" },
                                    ["urgency"]       = new JsonObject { ["type"] = "STRING", ["description"] = "high | critical" },
                                    ["customerName"]  = new JsonObject { ["type"] = "STRING", ["description"] = "Customer name if known" },
                                    ["customerEmail"] = new JsonObject { ["type"] = "STRING", ["description"] = "Customer email if known" }
                                },
                                ["required"] = new JsonArray { "reason", "urgency" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "getTicketStatus",
                            ["description"] = "Retrieves the real-time status, priority, and details of an existing support ticket. Call this tool ONLY when the user's message explicitly names a specific ticket (e.g. '#12004', 'TICK-12004') or unambiguously asks 'what is the status of my ticket(s)?' with no other topic mixed into the same message. NEVER call this tool in response to billing, discount, refund, or 'you promised' language - those must go through searchChatHistory first per the prior-commitment rule. Do not infer a ticket ID from memory or prior context unless the user's current message itself references a ticket.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["ticketIdOrRef"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Ticket ID or reference (e.g. '12004', '#12004', 'TICK-12004'). Can be omitted if user asks for their latest ticket."
                                    }
                                }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "searchChatHistory",
                            ["description"] = "Searches earlier conversation messages from this chat session that may have scrolled off the immediate visible history. IMPORTANT: Do NOT call this tool if the exchange, question, or answer is already visible in the recent conversation messages provided in the prompt. Call this tool only when the user references something said, asked, suggested, or promised earlier in the conversation that is NOT visible in the immediate recent messages. This tool takes priority over getTicketStatus when a message contains both a prior-commitment claim and ticket-adjacent language.",
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "OBJECT",
                                ["properties"] = new JsonObject
                                {
                                    ["query"] = new JsonObject
                                    {
                                        ["type"] = "STRING",
                                        ["description"] = "Keywords, error messages, topics, or numbers to search for in earlier conversation turns."
                                    }
                                },
                                ["required"] = new JsonArray { "query" }
                            }
                        }
                    }
                }
            };

            var payload = new JsonObject
            {
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstructionText } }
                },
                ["contents"] = contents
            };
            if (!isOutOfScope)
            {
                payload["tools"] = tools;
            }


            var streamEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_modelName}:streamGenerateContent?alt=sse&key={apiKey}";
            var payloadJson = payload.ToJsonString();

            HttpResponseMessage httpResponse;
            bool httpInitFailed = false;
            string httpInitError = string.Empty;
            try
            {
                using var requestContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                httpResponse = await _httpClient.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, streamEndpoint) { Content = requestContent },
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP error initiating Gemini SSE stream.");
                httpInitFailed = true;
                httpInitError = "Connection error — please try again.";
                httpResponse = null!;
            }

            if (httpInitFailed)
            {
                yield return new StreamChunk(ErrorMessage: httpInitError);
                yield return new StreamChunk(Done: true);
                yield break;
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Gemini SSE endpoint returned {Status}: {Body}", (int)httpResponse.StatusCode, errBody);
                yield return new StreamChunk(ErrorMessage: "AI service temporarily unavailable. Please try again shortly.");
                yield return new StreamChunk(Done: true);
                yield break;
            }

            // ── Stream reading loop ──────────────────────────────────────────────
            var assembledText = new StringBuilder();
            bool ticketCreated = false;
            int? ticketId = null;

            using var responseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new System.IO.StreamReader(responseStream, Encoding.UTF8);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                // SSE lines start with "data: "; blank lines are keep-alive separators
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var jsonData = line.Substring(6).Trim(); // strip "data: "
                if (jsonData == "[DONE]") break;

                JsonObject? chunkObj;
                try { chunkObj = JsonNode.Parse(jsonData)?.AsObject(); }
                catch { continue; } // malformed line — skip

                if (chunkObj == null) continue;

                var parts = chunkObj["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                if (parts == null) continue;

                // ── Check for function call mid-stream ───────────────────────────
                var funcPart = parts.FirstOrDefault(p => p?["functionCall"] != null)?.AsObject();
                if (funcPart != null)
                {
                    var funcCall   = funcPart["functionCall"]?.AsObject();
                    var funcName   = funcCall?["name"]?.ToString();
                    var funcArgs   = funcCall?["args"]?.AsObject();

                    _logger.LogInformation("Gemini SSE stream: functionCall detected — {FuncName}", funcName);

                    // Signal the controller that a function call fired
                    yield return new StreamChunk(FunctionCallName: funcName, FunctionCallArgs: funcArgs);

                    // ── Execute the function server-side ─────────────────────────
                    string? pendingYieldText = null;
                    bool funcExecFailed = false;
                    try
                    {
                        if (string.Equals(funcName, "escalateToHuman", StringComparison.OrdinalIgnoreCase))
                        {
                            var reason        = funcArgs?["reason"]?.ToString() ?? userMessage;
                            var urgency       = funcArgs?["urgency"]?.ToString() ?? "high";
                            var escalateName  = funcArgs?["customerName"]?.ToString() ?? username;
                            var escalateEmail = funcArgs?["customerEmail"]?.ToString() ?? email;
                            var issue         = $"[URGENT ESCALATION - {urgency.ToUpperInvariant()}] {reason}";

                            var ticket = await _ticketService.CreateTicketAsync(
                                customerId, issue, "technical", "high",
                                escalateName, escalateEmail, authenticatedUserId);

                            ticketCreated = true;
                            ticketId      = ticket.Id;

                            var escalationMsg =
                                $"I can see you're having a really difficult time, and I sincerely apologize. " +
                                $"I've immediately escalated your case with **HIGH priority** — Ticket **#{ticket.Id}** has been created. " +
                                $"Our team will contact you as soon as possible.";

                            assembledText.Append(escalationMsg);
                            pendingYieldText = escalationMsg;
                        }
                        else if (string.Equals(funcName, "createTicket", StringComparison.OrdinalIgnoreCase))
                        {
                            var issue         = funcArgs?["issue"]?.ToString() ?? userMessage;
                            var category      = funcArgs?["category"]?.ToString() ?? "other";
                            var priority      = funcArgs?["priority"]?.ToString() ?? "medium";
                            var ticketName    = funcArgs?["customerName"]?.ToString() ?? username;
                            var ticketEmail   = funcArgs?["customerEmail"]?.ToString() ?? email;

                            // Code-level safety backstop: intercept complaints about AI itself
                            if (IsAiComplaint(issue) || IsAiComplaint(userMessage))
                            {
                                _logger.LogWarning("StreamChatAsync: createTicket code backstop triggered! Issue='{Issue}', UserMessage='{UserMessage}'. Ticket creation declined.", issue, userMessage);

                                var declineReply = "I apologize for misunderstanding your question earlier! I cannot create a support ticket for my own conversational mistake or AI performance, but I would like to help get this right. Could you please restate your original question? If you are experiencing a genuine issue with ChatApp (such as an application bug, billing problem, or account issue), please let me know and I will be happy to create a ticket for our team.";

                                assembledText.Append(declineReply);
                                pendingYieldText = declineReply;
                            }
                            else
                            {
                                var ticket = await _ticketService.CreateTicketAsync(
                                    customerId, issue, category, priority,
                                    ticketName, ticketEmail, authenticatedUserId);

                                ticketCreated = true;
                                ticketId      = ticket.Id;

                                // Do a non-streaming follow-up call so Gemini can phrase the confirmation naturally
                                var followUpContents = JsonNode.Parse(contents.ToJsonString())!.AsArray();
                                // Append the model's function-call turn
                                followUpContents.Add(JsonNode.Parse(chunkObj["candidates"]![0]!["content"]!.ToJsonString())!);
                                // Append our function response
                                followUpContents.Add(new JsonObject
                                {
                                    ["role"] = "user",
                                    ["parts"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["functionResponse"] = new JsonObject
                                            {
                                                ["name"] = "createTicket",
                                                ["response"] = new JsonObject
                                                {
                                                    ["ticketId"] = ticket.Id,
                                                    ["status"]   = "Created",
                                                    ["message"]  = $"Ticket #{ticket.Id} successfully created."
                                                }
                                            }
                                        }
                                    }
                                });

                                systemInstructionText +=
                                    "\n\nVERIFICATION BEFORE REPLYING: Compare the tool result you just received against the " +
                                    "user's actual question. If the tool result's subject does not match what the user asked " +
                                    "(for example, the user asked about a billing refund but the retrieved ticket is about an " +
                                    "unrelated feature request), do NOT present the tool result as the answer. Instead, tell the " +
                                    "user plainly that you did not find something matching their actual question, and address " +
                                    "their real question directly (referencing the KNOWLEDGE BASE CONTEXT or offering to create " +
                                    "a ticket, as appropriate).";

                                var followUpPayload = new JsonObject
                                {
                                    ["systemInstruction"] = new JsonObject
                                    {
                                        ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstructionText } }
                                    },
                                    ["contents"] = followUpContents
                                };

                                var followUpEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_modelName}:generateContent?key={apiKey}";
                                var (followUpResponse, _) = await SendSingleGeminiRequestAsync(followUpEndpoint, followUpPayload);
                                var fuParts = followUpResponse?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                                var fuReply = fuParts?.FirstOrDefault(p => p?["text"] != null && p?["thought"] == null)?["text"]?.ToString()
                                              ?? fuParts?.FirstOrDefault(p => p?["text"] != null)?["text"]?.ToString()
                                              ?? $"Support ticket #{ticket.Id} has been created. Our team will contact you within 24 hours.";

                                assembledText.Append(fuReply);
                                pendingYieldText = fuReply;
                            }
                        }
                        else if (string.Equals(funcName, "getTicketStatus", StringComparison.OrdinalIgnoreCase))
                        {
                            var ticketRef = funcArgs?["ticketIdOrRef"]?.ToString();
                            var statusResult = await _ticketService.GetTicketStatusAsync(ticketRef, authenticatedUserId, email);

                            _logger.LogInformation("Gemini SSE stream: getTicketStatus called for '{Ref}' - Found={Found}",
                                ticketRef ?? "latest", statusResult.Found);

                            var followUpContents = JsonNode.Parse(contents.ToJsonString())!.AsArray();
                            followUpContents.Add(JsonNode.Parse(chunkObj["candidates"]![0]!["content"]!.ToJsonString())!);
                            followUpContents.Add(new JsonObject
                            {
                                ["role"] = "user",
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["functionResponse"] = new JsonObject
                                        {
                                            ["name"] = "getTicketStatus",
                                            ["response"] = new JsonObject
                                            {
                                                ["found"] = statusResult.Found,
                                                ["referenceNumber"] = statusResult.ReferenceNumber,
                                                ["ticketId"] = statusResult.TicketId,
                                                ["status"] = statusResult.Status,
                                                ["priority"] = statusResult.Priority,
                                                ["category"] = statusResult.Category,
                                                ["subject"] = statusResult.Subject,
                                                ["createdAt"] = statusResult.CreatedAt?.ToString("o"),
                                                ["details"] = statusResult.Message
                                            }
                                        }
                                    }
                                }
                            });

                            systemInstructionText +=
                                "\n\nVERIFICATION BEFORE REPLYING: Compare the tool result you just received against the " +
                                "user's actual question. If the tool result's subject does not match what the user asked " +
                                "(for example, the user asked about a billing refund but the retrieved ticket is about an " +
                                "unrelated feature request), do NOT present the tool result as the answer. Instead, tell the " +
                                "user plainly that you did not find something matching their actual question, and address " +
                                "their real question directly (referencing the KNOWLEDGE BASE CONTEXT or offering to create " +
                                "a ticket, as appropriate).";

                            var followUpPayload = new JsonObject
                            {
                                ["systemInstruction"] = new JsonObject
                                {
                                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstructionText } }
                                },
                                ["contents"] = followUpContents
                            };

                            var followUpEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_modelName}:generateContent?key={apiKey}";
                            var (followUpResponse, _) = await SendSingleGeminiRequestAsync(followUpEndpoint, followUpPayload);
                            var fuParts = followUpResponse?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                            var fuReply = fuParts?.FirstOrDefault(p => p?["text"] != null && p?["thought"] == null)?["text"]?.ToString()
                                          ?? fuParts?.FirstOrDefault(p => p?["text"] != null)?["text"]?.ToString()
                                          ?? statusResult.Message;

                            assembledText.Append(fuReply);
                            pendingYieldText = fuReply;
                        }
                        else if (string.Equals(funcName, "searchChatHistory", StringComparison.OrdinalIgnoreCase))
                        {
                            var query = funcArgs?["query"]?.ToString() ?? userMessage;
                            var pastExcerpts = await _memoryService.RetrievePastMessagesAsync(sessionId, query);

                            _logger.LogInformation("Gemini SSE stream: searchChatHistory called for '{Query}' - Found={Count}",
                                query, pastExcerpts.Count);

                            var excerptsArray = new JsonArray();
                            foreach (var ex in pastExcerpts)
                            {
                                excerptsArray.Add(new JsonObject
                                {
                                    ["turn"] = ex.TurnIndex,
                                    ["speaker"] = ex.Role,
                                    ["message"] = ex.Content,
                                    ["timestamp"] = ex.Timestamp.ToString("o")
                                });
                            }

                            var followUpContents = JsonNode.Parse(contents.ToJsonString())!.AsArray();
                            followUpContents.Add(JsonNode.Parse(chunkObj["candidates"]![0]!["content"]!.ToJsonString())!);
                            followUpContents.Add(new JsonObject
                            {
                                ["role"] = "user",
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["functionResponse"] = new JsonObject
                                        {
                                            ["name"] = "searchChatHistory",
                                            ["response"] = new JsonObject
                                            {
                                                ["query"] = query,
                                                ["found"] = pastExcerpts.Count > 0,
                                                ["matchCount"] = pastExcerpts.Count,
                                                ["results"] = excerptsArray,
                                                ["notes"] = pastExcerpts.Count > 0 
                                                    ? "These are verbatim past messages matching the query." 
                                                    : "No older messages matching the query were found outside the visible recent history. Note: The recent turns are already provided directly in the conversation context."
                                            }
                                        }
                                    }
                                }
                            });

                            systemInstructionText +=
                                "\n\nVERIFICATION & CONSISTENCY RULES:\n" +
                                "1. Compare the tool result against the user's actual question. If the tool result's subject does not match what the user asked, do NOT present the tool result as the answer. Instead, tell the user plainly that you did not find something matching their actual question, and address their real question directly.\n" +
                                "2. INTERNAL CONSISTENCY GUARD: Never claim you have no record of an interaction if the information is visible in the conversation context or known from user status/memory. Never pair an apology about having no record with an answer that contains the very information requested. If you know the answer or it is in the context, state it directly and consistently.";

                            var followUpPayload = new JsonObject
                            {
                                ["systemInstruction"] = new JsonObject
                                {
                                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstructionText } }
                                },
                                ["contents"] = followUpContents
                            };

                            var followUpEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_modelName}:generateContent?key={apiKey}";
                            var (followUpResponse, _) = await SendSingleGeminiRequestAsync(followUpEndpoint, followUpPayload);
                            var fuParts = followUpResponse?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                            var fuReply = fuParts?.FirstOrDefault(p => p?["text"] != null && p?["thought"] == null)?["text"]?.ToString()
                                          ?? fuParts?.FirstOrDefault(p => p?["text"] != null)?["text"]?.ToString()
                                          ?? (pastExcerpts.Count > 0
                                              ? $"Earlier in our chat, you mentioned: \"{pastExcerpts.First().Content}\""
                                              : "I searched our earlier messages but couldn't find a matching reference.");

                            assembledText.Append(fuReply);
                            pendingYieldText = fuReply;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing function {FuncName} during SSE stream.", funcName);
                        funcExecFailed = true;
                    }

                    // Yield function result text OUTSIDE the try/catch (C# language rule for iterators)
                    if (!funcExecFailed && pendingYieldText != null)
                        yield return new StreamChunk(Text: pendingYieldText);

                    // Function call ends the stream for this turn
                    break;
                }

                // ── Regular text chunk ───────────────────────────────────────────
                foreach (var part in parts)
                {
                    var thought = part?["thought"];
                    if (thought != null) continue; // skip internal thought blocks

                    var text = part?["text"]?.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        assembledText.Append(text);
                        yield return new StreamChunk(Text: text);
                    }
                }
            }

            // ── Write audit log (non-fatal) ──────────────────────────────────────
            try
            {
                await WriteAuditLogAsync(sessionId, resolvedIntent,
                    intent?.Confidence ?? 0.5f, _modelName,
                    kbContext, 0,
                    ticketCreated ? $"TICK-{ticketId}" : null,
                    userMessage);
            }
            catch { /* non-critical */ }

            // ── Terminal sentinel ────────────────────────────────────────────────
            yield return new StreamChunk(
                Done: true,
                TicketCreated: ticketCreated,
                TicketId: ticketId);
        }
    }
}
