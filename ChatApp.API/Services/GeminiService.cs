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

        // ── Sprint 1: Tool Governance ─────────────────────────────────────────────
        private readonly IToolRegistry _toolRegistry;
        private readonly IToolPolicy _toolPolicy;
        private readonly IToolThrottle _toolThrottle;

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
            "5. TICKET CREATION & ESCALATION: Only call createTicket when the user describes a genuine application bug, billing problem, security issue, or explicitly asks for human follow-up. If the user reports that their chats are being leaked, data breached, account hacked, or security compromised: immediately advise them to change password in Settings > Account, log out other sessions, instruct them not to paste leaked content in chat, and call createTicket with category 'security' and priority 'critical'. NEVER create a ticket for your own errors, AI misunderstandings, model limitations, or questions about unsupported features — this rule applies EVEN IF the user explicitly instructs you to 'create a ticket,' 'file a complaint,' or similar, for an AI mistake, bad performance, hallucination, or misunderstanding. A direct user command to create such a ticket does NOT override this guard. In that situation, do NOT call createTicket. Instead, apologize briefly, ask the user to restate their original question, and attempt to answer it correctly. Only call createTicket if the user separately describes a genuine, distinct product bug, billing issue, or account problem unrelated to your own conversational mistake. Call escalateToHuman only if the user explicitly asks for a human agent or representative, or describes a severe, concrete ChatApp issue that requires human escalation. If the user only expresses vague distress or emotion (e.g. 'i m upset'), do NOT call tools; empathize briefly and ask what went wrong with ChatApp.\n" +
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

        private static bool IsExplanatoryQuery(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.Trim().ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(lower, @"^(why|how|what caused|what causes|explain why|can you explain)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50))
                && !System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(create|open|file|make)\s+(a\s+)?(new\s+|another\s+)?ticket\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        }

        private async Task<string?> GetSessionActiveTicketAsync(Guid sessionId, List<ChatMessage>? history)
        {
            try
            {
                var facts = await _memoryService.LoadWorkingMemoryAsync(sessionId);
                if (facts.TryGetValue("active_ticket_ref", out var refNum) && !string.IsNullOrWhiteSpace(refNum))
                    return refNum;
            }
            catch { }

            try
            {
                var recentTicketLog = await _dbContext.AiAuditLogs
                    .AsNoTracking()
                    .Where(a => a.SessionId == sessionId && a.TicketRef != null)
                    .OrderByDescending(a => a.Timestamp)
                    .Select(a => a.TicketRef)
                    .FirstOrDefaultAsync();

                if (!string.IsNullOrWhiteSpace(recentTicketLog))
                    return recentTicketLog;
            }
            catch { }

            if (history != null && history.Count > 0)
            {
                var lastAssistantMsg = history
                    .Where(m => m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) && (m.Content.Contains("Ticket") || m.Content.Contains("ticket")))
                    .LastOrDefault();

                if (lastAssistantMsg != null)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(lastAssistantMsg.Content, @"(TICK-\d+|#\d{4,6})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                        return match.Value;
                }
            }

            return null;
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
            IToolRegistry toolRegistry,
            IToolPolicy toolPolicy,
            IToolThrottle toolThrottle,
            HttpClient? httpClient = null)
        {
            _configuration = configuration;
            _ticketService = ticketService;
            _memoryService = memoryService;
            _dbContext = dbContext;
            _logger = logger;
            _toolRegistry = toolRegistry;
            _toolPolicy = toolPolicy;
            _toolThrottle = toolThrottle;
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
                                            ["description"] = "one of: billing, technical, account, security, other"
                                        },
                                        ["priority"] = new JsonObject
                                        {
                                            ["type"] = "STRING",
                                            ["description"] = "Evaluated gravity: 'critical' for security incidents, data/chat leaks, account compromises, or active vulnerabilities; 'high' for core blockers (e.g. unable to type in chat window, cannot send messages, crash, billing charges); 'medium' for non-blocking bugs; 'low' for cosmetic/inquiries"
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
                    var functionArgs = functionCall?["args"]?.AsObject();

                    // ══════════════════════════════════════════════════════════════
                    // SPRINT 1: Tool Governance Pipeline
                    // THE LLM MAY REQUEST AN ACTION — THE BACKEND DECIDES IF ALLOWED
                    // ══════════════════════════════════════════════════════════════

                    // Step 1: Registry — validate tool exists and is enabled
                    var registryResult = _toolRegistry.Resolve(functionName);
                    if (!registryResult.Success)
                    {
                        _logger.LogWarning(
                            "[Governance] Tool rejected by registry: '{Tool}', Reason={Reason}, Session={SessionId}",
                            functionName, registryResult.BlockReason, sessionId);

                        orchestrationSw.Stop();
                        await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: functionName,
                            toolAllowed: false,
                            blockReason: registryResult.BlockReason);

                        return new GeminiChatResult(
                            registryResult.BlockMessage ?? "I'm unable to process that request right now. Please try again or contact support.",
                            false, null, AuditLogId: null, IntentUsed: resolvedIntent);
                    }

                    var toolDef = registryResult.Tool!;

                    // Step 2: Policy — authorization using server-side identity only
                    var authContext = new ToolAuthContext(
                        AuthenticatedUserId: authenticatedUserId,
                        AuthenticatedEmail: authenticatedUserId.HasValue ? email : null,  // only trust email from JWT
                        SessionId: sessionId);

                    var policyResult = _toolPolicy.Authorize(toolDef, authContext);
                    if (!policyResult.IsAllowed)
                    {
                        _logger.LogWarning(
                            "[Governance] Tool '{Tool}' blocked by policy: {Reason}, Session={SessionId}",
                            functionName, policyResult.BlockReason, sessionId);

                        orchestrationSw.Stop();
                        await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: functionName,
                            toolAllowed: false,
                            blockReason: policyResult.BlockReason);

                        return new GeminiChatResult(
                            policyResult.BlockMessage ?? "You are not authorized to use this feature.",
                            false, null, AuditLogId: null, IntentUsed: resolvedIntent);
                    }

                    // Step 3: Throttle — per-session invocation limit
                    var throttleResult = _toolThrottle.TryConsume(sessionId, functionName!, toolDef.MaxPerSession);
                    if (!throttleResult.Allowed)
                    {
                        _logger.LogWarning(
                            "[Governance] Tool '{Tool}' throttled for session {SessionId}: {Count}/{Max}",
                            functionName, sessionId, throttleResult.CurrentCount, throttleResult.MaxAllowed);

                        orchestrationSw.Stop();
                        await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: functionName,
                            toolAllowed: false,
                            blockReason: ChatApp.Domain.Entities.AuditBlockReasons.ToolThrottled);

                        return new GeminiChatResult(
                            $"I've already performed this action {throttleResult.MaxAllowed} times in this session. Please start a new support session if you need further assistance.",
                            false, null, AuditLogId: null, IntentUsed: resolvedIntent);
                    }

                    // Step 4: Argument validation
                    var argValidation = ToolArgumentValidator.Validate(functionName!, functionArgs);
                    if (!argValidation.IsValid)
                    {
                        _logger.LogWarning(
                            "[Governance] Tool '{Tool}' argument validation failed: {Error}, Session={SessionId}",
                            functionName, argValidation.ErrorMessage, sessionId);

                        orchestrationSw.Stop();
                        await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: functionName,
                            toolAllowed: false,
                            blockReason: ChatApp.Domain.Entities.AuditBlockReasons.ArgumentInvalid);

                        return new GeminiChatResult(
                            "I wasn't able to process that request because of an issue with the information provided. Please try rephrasing your request.",
                            false, null, AuditLogId: null, IntentUsed: resolvedIntent);
                    }

                    // ── All governance checks passed — execute tool ────────────────

                    // ── escalateToHuman ──
                    if (string.Equals(functionName, "escalateToHuman", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = functionArgs;
                        var reason = args?["reason"]?.ToString() ?? userMessage;
                        var urgency = args?["urgency"]?.ToString() ?? "high";
                        // SECURITY: Name/email from LLM args are display-only metadata for the ticket.
                        // The actual user identity (authenticatedUserId) always comes from the JWT context.
                        var escalateName = args?["customerName"]?.ToString() ?? username;
                        var escalateEmail = authenticatedUserId.HasValue
                            ? email  // use JWT-verified email for authenticated users
                            : args?["customerEmail"]?.ToString() ?? email; // guests: LLM arg is OK as contact detail

                        var activeTicketRef = await GetSessionActiveTicketAsync(sessionId, conversationHistory);
                        var isExpl = IsExplanatoryQuery(userMessage) || IsExplanatoryQuery(reason);
                        var hasExplicitNewTicket = System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(another|new|second|separate)\s+ticket\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (activeTicketRef != null && !hasExplicitNewTicket)
                        {
                            _logger.LogInformation("escalateToHuman deduplicated: Active ticket {ActiveRef} already exists for session {SessionId}. Declining duplicate escalation.", activeTicketRef, sessionId);
                            orchestrationSw.Stop();
                            var auditIdDedup = await WriteAuditLogAsync(sessionId, resolvedIntent,
                                intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                                kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                                activeTicketRef, userMessage,
                                authenticatedUserId: authenticatedUserId,
                                toolName: "escalateToHuman",
                                toolAllowed: false,
                                blockReason: ChatApp.Domain.Entities.AuditBlockReasons.DuplicateSessionTicketBlocked);

                            var dedupReply = isExpl
                                ? $"Chats on our platform are encrypted in transit via TLS 1.3 and at rest with AES-256 encryption. Potential leaks or unauthorized access can occur if an account password was compromised elsewhere, or if active sessions remained open on a shared or unattended device.\n\nYour existing incident ticket (**{activeTicketRef}**) is already actively assigned to our security engineering team, and they are reviewing your account logs. Please ensure you change your password in **Settings > Account** and click **Log out from all devices**."
                                : $"Your case is already actively being tracked under ticket **{activeTicketRef}**. Our team has received your report and is investigating. We will follow up with you directly as soon as possible!";

                            return new GeminiChatResult(dedupReply, true, null, AuditLogId: auditIdDedup, IntentUsed: resolvedIntent);
                        }

                        if (isExpl && !System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(create|open|file|make|talk|speak|escalat)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            _logger.LogInformation("escalateToHuman declined for explanatory query '{UserMessage}'.", userMessage);
                            orchestrationSw.Stop();
                            var auditIdExpl = await WriteAuditLogAsync(sessionId, resolvedIntent,
                                intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                                kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                                null, userMessage,
                                authenticatedUserId: authenticatedUserId,
                                toolName: "escalateToHuman",
                                toolAllowed: false,
                                blockReason: ChatApp.Domain.Entities.AuditBlockReasons.ExplanatoryQueryNotTicket);

                            var explReply = "Chats on ChatApp are protected with TLS 1.3 encryption in transit and AES-256 encryption at rest. If messages appear exposed, common causes include:\n" +
                                "1. **Reused or Compromised Passwords**: If your password was compromised on another service.\n" +
                                "2. **Active Sessions on Other Devices**: If your account remained logged in on a shared computer or browser.\n" +
                                "3. **Phishing or Device Access**: If unauthorized individuals gained physical or remote access to your device.\n\n" +
                                "To protect your account right now, please go to **Settings > Account** to change your password and log out of all active sessions.";

                            return new GeminiChatResult(explReply, false, null, AuditLogId: auditIdExpl, IntentUsed: resolvedIntent);
                        }

                        _logger.LogWarning("Gemini triggered escalateToHuman: Reason='{Reason}', Urgency='{Urgency}', User='{User}'",
                            reason, urgency, escalateName ?? "Guest");

                        var escalationIssue = $"[URGENT ESCALATION - {urgency.ToUpperInvariant()}] {reason}";
                        var ticket = await _ticketService.CreateTicketAsync(
                            customerId, escalationIssue, "technical", "high",
                            escalateName, escalateEmail, authenticatedUserId);

                        try { await _memoryService.SaveFactAsync(sessionId, "active_ticket_ref", $"TICK-{ticket.Id}", "support_verified"); } catch {}

                        orchestrationSw.Stop();
                        var auditId1 = await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            $"TICK-{ticket.Id}", userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: "escalateToHuman",
                            toolAllowed: true);

                        return new GeminiChatResult(
                            $"I can see you're having a really difficult time, and I sincerely apologize for the frustration. I've immediately escalated your case to our senior support team with **HIGH priority** — Ticket **#{ticket.Id}** has been created and our team will contact you as soon as possible. You deserve faster help than I can provide right now.",
                            true, ticket.Id, AuditLogId: auditId1, IntentUsed: resolvedIntent);
                    }

                    // ── createTicket ──
                    if (string.Equals(functionName, "createTicket", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = functionArgs;
                        var issue = args?["issue"]?.ToString() ?? userMessage;
                        var category = args?["category"]?.ToString() ?? "other";
                        var priority = args?["priority"]?.ToString() ?? "medium";
                        var ticketCustomerName = args?["customerName"]?.ToString() ?? username;
                        // SECURITY: For authenticated users, always use JWT-verified email.
                        // For guests, the LLM-provided email is acceptable as a contact address.
                        var ticketCustomerEmail = authenticatedUserId.HasValue
                            ? email
                            : args?["customerEmail"]?.ToString() ?? email;

                        // Code-level safety backstop: intercept complaints about AI itself
                        if (IsAiComplaint(issue) || IsAiComplaint(userMessage))
                        {
                            _logger.LogWarning("ProcessChatAsync: createTicket code backstop triggered! Issue='{Issue}', UserMessage='{UserMessage}'. Ticket creation declined.", issue, userMessage);
                            orchestrationSw.Stop();
                            var auditIdDecline = await WriteAuditLogAsync(sessionId, resolvedIntent,
                                intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                                kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                                null, userMessage,
                                authenticatedUserId: authenticatedUserId,
                                toolName: "createTicket",
                                toolAllowed: false,
                                blockReason: "AiComplaintBackstop");

                            var declineReply = "I apologize for misunderstanding your question earlier! I cannot create a support ticket for my own conversational mistake or AI performance, but I would like to help get this right. Could you please restate your original question? If you are experiencing a genuine issue with ChatApp (such as an application bug, billing problem, or account issue), please let me know and I will be happy to create a ticket for our team.";
                            return new GeminiChatResult(declineReply, false, null, AuditLogId: auditIdDecline, IntentUsed: resolvedIntent);
                        }

                        var activeTicketRef = await GetSessionActiveTicketAsync(sessionId, conversationHistory);
                        var isExpl = IsExplanatoryQuery(userMessage) || IsExplanatoryQuery(issue);
                        var hasExplicitNewTicket = System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(another|new|second|separate)\s+ticket\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (activeTicketRef != null && !hasExplicitNewTicket)
                        {
                            _logger.LogInformation("createTicket deduplicated: Active ticket {ActiveRef} already exists for session {SessionId}. Declining duplicate ticket.", activeTicketRef, sessionId);
                            orchestrationSw.Stop();
                            var auditIdDedup = await WriteAuditLogAsync(sessionId, resolvedIntent,
                                intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                                kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                                activeTicketRef, userMessage,
                                authenticatedUserId: authenticatedUserId,
                                toolName: "createTicket",
                                toolAllowed: false,
                                blockReason: ChatApp.Domain.Entities.AuditBlockReasons.DuplicateSessionTicketBlocked);

                            var dedupReply = isExpl
                                ? $"Chats on our platform are encrypted in transit using TLS 1.3 and at rest with AES-256. Leaks or unauthorized access typically occur due to an exposed password, an active session left open on a shared device, or credential compromise elsewhere.\n\nYour existing incident ticket (**{activeTicketRef}**) is already actively assigned to our security engineering team, and they are reviewing your account logs. Please ensure you change your password in **Settings > Account** and click **Log out from all devices**."
                                : $"Your request is already being tracked under active ticket **{activeTicketRef}**. Our team has been notified and will follow up with you directly. If you have any additional details to add, please share them here and they will be noted!";

                            return new GeminiChatResult(dedupReply, true, null, AuditLogId: auditIdDedup, IntentUsed: resolvedIntent);
                        }

                        if (isExpl && !System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(create|open|file|make)\s+(a\s+)?ticket\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            _logger.LogInformation("createTicket declined for explanatory query '{UserMessage}'.", userMessage);
                            orchestrationSw.Stop();
                            var auditIdExpl = await WriteAuditLogAsync(sessionId, resolvedIntent,
                                intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                                kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                                null, userMessage,
                                authenticatedUserId: authenticatedUserId,
                                toolName: "createTicket",
                                toolAllowed: false,
                                blockReason: ChatApp.Domain.Entities.AuditBlockReasons.ExplanatoryQueryNotTicket);

                            var explReply = "Chats on ChatApp are protected with TLS 1.3 encryption in transit and AES-256 encryption at rest. If messages appear exposed, common causes include:\n" +
                                "1. **Reused or Compromised Passwords**: If your password was compromised on another service.\n" +
                                "2. **Active Sessions on Other Devices**: If your account remained logged in on a shared computer or browser.\n" +
                                "3. **Phishing or Device Access**: If unauthorized individuals gained physical or remote access to your device.\n\n" +
                                "To protect your account right now, please go to **Settings > Account** to change your password and log out of all active sessions. If you would like me to create a security support ticket for our engineering team to review your logs, please let me know!";

                            return new GeminiChatResult(explReply, false, null, AuditLogId: auditIdExpl, IntentUsed: resolvedIntent);
                        }

                        _logger.LogInformation("Gemini triggered createTicket: Issue='{Issue}', Category='{Category}', Priority='{Priority}', Requester='{Requester}', UserId='{UserId}'",
                            issue, category, priority, ticketCustomerName ?? "Guest", authenticatedUserId?.ToString() ?? "Guest");

                        var ticket = await _ticketService.CreateTicketAsync(customerId, issue, category, priority, ticketCustomerName, ticketCustomerEmail, authenticatedUserId);
                        try { await _memoryService.SaveFactAsync(sessionId, "active_ticket_ref", $"TICK-{ticket.Id}", "support_verified"); } catch {}

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
                                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstructionText } }
                            },
                            ["contents"] = JsonNode.Parse(contents.ToJsonString())
                        };

                        var followUpEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{workingModel ?? _modelName}:generateContent?key={apiKey}";
                        var (followUpResponse, _) = await SendSingleGeminiRequestAsync(followUpEndpoint, followUpRequestBody);
                        var followUpParts = followUpResponse?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
                        var followUpReply = followUpParts?.FirstOrDefault(p => p?["text"] != null && p?["thought"] == null)?["text"]?.ToString()
                                            ?? followUpParts?.FirstOrDefault(p => p?["text"] != null)?["text"]?.ToString();

                        if (string.IsNullOrWhiteSpace(followUpReply))
                            followUpReply = $"I have created support ticket #{ticket.Id} for you. Our team will contact you within 24 hours.";

                        orchestrationSw.Stop();
                        var auditId2 = await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, workingModel ?? _modelName,
                            kbContext, (int)orchestrationSw.ElapsedMilliseconds,
                            $"TICK-{ticket.Id}", userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: "createTicket",
                            toolAllowed: true);

                        return new GeminiChatResult(followUpReply, true, ticket.Id,
                            AuditLogId: auditId2, IntentUsed: resolvedIntent);
                    }

                    // ── getTicketStatus ──
                    if (string.Equals(functionName, "getTicketStatus", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = functionArgs;
                        var ticketRef = args?["ticketIdOrRef"]?.ToString();

                        // SECURITY (GAP-01 FIX): For authenticated users, pass only server-side identity.
                        // The LLM-supplied customerEmail is NEVER used when the user is authenticated —
                        // this prevents "User A → LLM args email=userB → reads UserB tickets".
                        // For anonymous users, email from args is used as a contact filter (by design).
                        string? statusEmail = authenticatedUserId.HasValue ? null : email;

                        var statusResult = await _ticketService.GetTicketStatusAsync(ticketRef, authenticatedUserId, statusEmail);

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
                            "user's actual question. If the tool result's subject does not match what the user asked, " +
                            "do NOT present the tool result as the answer. Instead, tell the user plainly that you did " +
                            "not find something matching their actual question, and address their real question directly.";

                        var followUpRequestBody = new JsonObject
                        {
                            ["systemInstruction"] = new JsonObject
                            {
                                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstructionText } }
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
                            statusResult.ReferenceNumber, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: "getTicketStatus",
                            toolAllowed: true);

                        return new GeminiChatResult(followUpReply, false, statusResult.TicketId,
                            AuditLogId: auditId, IntentUsed: resolvedIntent);
                    }

                    // ── searchChatHistory ──
                    if (string.Equals(functionName, "searchChatHistory", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = functionArgs;
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
                                                : "No older messages matching the query were found outside the visible recent history."
                                        }
                                    }
                                }
                            }
                        });

                        systemInstructionText +=
                            "\n\nVERIFICATION & CONSISTENCY RULES:\n" +
                            "1. Compare the tool result against the user's actual question. Answer directly if found.\n" +
                            "2. Never claim you have no record of an interaction if the information is visible in context.";

                        var followUpRequestBody = new JsonObject
                        {
                            ["systemInstruction"] = new JsonObject
                            {
                                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstructionText } }
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
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: "searchChatHistory",
                            toolAllowed: true);

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
            string userMessage,
            Guid? authenticatedUserId = null,
            string? toolName = null,
            bool? toolAllowed = null,
            string? blockReason = null)
        {
            try
            {
                // Extract KB topic names from the formatted context string
                var kbTopics = System.Text.RegularExpressions.Regex
                    .Matches(kbContext ?? string.Empty, @"\[Topic: ([^\]]+)\]")
                    .Select(m => m.Groups[1].Value)
                    .ToList();

                // Build ToolCallsJson if a tool was involved
                string? toolCallsJson = null;
                if (toolName != null)
                {
                    var toolEntry = new System.Text.Json.Nodes.JsonObject
                    {
                        ["tool"] = toolName,
                        ["allowed"] = toolAllowed ?? false,
                        ["blockReason"] = blockReason
                    };
                    toolCallsJson = new System.Text.Json.Nodes.JsonArray(toolEntry).ToJsonString();
                }

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
                    UserMessageSnippet = userMessage.Length > 500 ? userMessage[..497] + "..." : userMessage,
                    ToolCallsJson = toolCallsJson,
                    BlockReason = blockReason,
                    AuthenticatedUserId = authenticatedUserId
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

                    // ══════════════════════════════════════════════════════════════
                    // SPRINT 1: Tool Governance Pipeline (Streaming Path)
                    // Same rules as ProcessChatAsync — no bypass for SSE.
                    // ══════════════════════════════════════════════════════════════

                    // Step 1: Registry
                    var regResult = _toolRegistry.Resolve(funcName);
                    if (!regResult.Success)
                    {
                        _logger.LogWarning("[Governance/Stream] Tool rejected by registry: '{Tool}', Reason={Reason}", funcName, regResult.BlockReason);
                        await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, _modelName, kbContext, 0,
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: funcName, toolAllowed: false, blockReason: regResult.BlockReason);

                        yield return new StreamChunk(Text: regResult.BlockMessage ?? "I'm unable to process that request right now.");
                        yield return new StreamChunk(Done: true, TicketCreated: ticketCreated, TicketId: ticketId);
                        yield break;
                    }
                    var streamToolDef = regResult.Tool!;

                    // Step 2: Policy
                    var streamAuthCtx = new ToolAuthContext(
                        AuthenticatedUserId: authenticatedUserId,
                        AuthenticatedEmail: authenticatedUserId.HasValue ? email : null,
                        SessionId: sessionId);
                    var streamPolicyResult = _toolPolicy.Authorize(streamToolDef, streamAuthCtx);
                    if (!streamPolicyResult.IsAllowed)
                    {
                        _logger.LogWarning("[Governance/Stream] Tool '{Tool}' blocked by policy: {Reason}", funcName, streamPolicyResult.BlockReason);
                        await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, _modelName, kbContext, 0,
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: funcName, toolAllowed: false, blockReason: streamPolicyResult.BlockReason);

                        yield return new StreamChunk(Text: streamPolicyResult.BlockMessage ?? "You are not authorized to use this feature.");
                        yield return new StreamChunk(Done: true, TicketCreated: ticketCreated, TicketId: ticketId);
                        yield break;
                    }

                    // Step 3: Throttle
                    var streamThrottle = _toolThrottle.TryConsume(sessionId, funcName!, streamToolDef.MaxPerSession);
                    if (!streamThrottle.Allowed)
                    {
                        _logger.LogWarning("[Governance/Stream] Tool '{Tool}' throttled for session {SessionId}: {Count}/{Max}",
                            funcName, sessionId, streamThrottle.CurrentCount, streamThrottle.MaxAllowed);
                        await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, _modelName, kbContext, 0,
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: funcName, toolAllowed: false,
                            blockReason: ChatApp.Domain.Entities.AuditBlockReasons.ToolThrottled);

                        yield return new StreamChunk(Text: $"I've already performed this action {streamThrottle.MaxAllowed} times in this session. Please start a new support session if you need further assistance.");
                        yield return new StreamChunk(Done: true, TicketCreated: ticketCreated, TicketId: ticketId);
                        yield break;
                    }

                    // Step 4: Argument validation
                    var streamArgVal = ToolArgumentValidator.Validate(funcName!, funcArgs);
                    if (!streamArgVal.IsValid)
                    {
                        _logger.LogWarning("[Governance/Stream] Tool '{Tool}' argument validation failed: {Error}", funcName, streamArgVal.ErrorMessage);
                        await WriteAuditLogAsync(sessionId, resolvedIntent,
                            intent?.Confidence ?? 0.5f, _modelName, kbContext, 0,
                            null, userMessage,
                            authenticatedUserId: authenticatedUserId,
                            toolName: funcName, toolAllowed: false,
                            blockReason: ChatApp.Domain.Entities.AuditBlockReasons.ArgumentInvalid);

                        yield return new StreamChunk(Text: "I wasn't able to process that request because of an issue with the information provided. Please try rephrasing your request.");
                        yield return new StreamChunk(Done: true, TicketCreated: ticketCreated, TicketId: ticketId);
                        yield break;
                    }

                    // ── All governance checks passed — execute tool ────────────────

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
                            // SECURITY: JWT email for authenticated users; LLM arg only for guests
                            var escalateEmail = authenticatedUserId.HasValue ? email : (funcArgs?["customerEmail"]?.ToString() ?? email);

                            var activeTicketRef = await GetSessionActiveTicketAsync(sessionId, conversationHistory);
                            var isExpl = IsExplanatoryQuery(userMessage) || IsExplanatoryQuery(reason);
                            var hasExplicitNewTicket = System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(another|new|second|separate)\s+ticket\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                            if (activeTicketRef != null && !hasExplicitNewTicket)
                            {
                                _logger.LogInformation("StreamChatAsync: escalateToHuman deduplicated: Active ticket {ActiveRef} already exists for session {SessionId}.", activeTicketRef, sessionId);
                                var dedupReply = isExpl
                                    ? $"Chats on our platform are encrypted in transit via TLS 1.3 and at rest with AES-256 encryption. Potential leaks or unauthorized access can occur if an account password was compromised elsewhere, or if active sessions remained open on a shared or unattended device.\n\nYour existing incident ticket (**{activeTicketRef}**) is already actively assigned to our security engineering team, and they are reviewing your account logs. Please ensure you change your password in **Settings > Account** and click **Log out from all devices**."
                                    : $"Your case is already actively being tracked under ticket **{activeTicketRef}**. Our team has received your report and is investigating. We will follow up with you directly as soon as possible!";

                                assembledText.Append(dedupReply);
                                pendingYieldText = dedupReply;
                            }
                            else if (isExpl && !System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(create|open|file|make|talk|speak|escalat)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                _logger.LogInformation("StreamChatAsync: escalateToHuman declined for explanatory query '{UserMessage}'.", userMessage);
                                var explReply = "Chats on ChatApp are protected with TLS 1.3 encryption in transit and AES-256 encryption at rest. If messages appear exposed, common causes include:\n" +
                                    "1. **Reused or Compromised Passwords**: If your password was compromised on another service.\n" +
                                    "2. **Active Sessions on Other Devices**: If your account remained logged in on a shared computer or browser.\n" +
                                    "3. **Phishing or Device Access**: If unauthorized individuals gained physical or remote access to your device.\n\n" +
                                    "To protect your account right now, please go to **Settings > Account** to change your password and log out of all active sessions.";

                                assembledText.Append(explReply);
                                pendingYieldText = explReply;
                            }
                            else
                            {
                                var issue = $"[URGENT ESCALATION - {urgency.ToUpperInvariant()}] {reason}";
                                var ticket = await _ticketService.CreateTicketAsync(
                                    customerId, issue, "technical", "high",
                                    escalateName, escalateEmail, authenticatedUserId);

                                ticketCreated = true;
                                ticketId      = ticket.Id;
                                try { await _memoryService.SaveFactAsync(sessionId, "active_ticket_ref", $"TICK-{ticket.Id}", "support_verified"); } catch {}

                                var escalationMsg =
                                    $"I can see you're having a really difficult time, and I sincerely apologize. " +
                                    $"I've immediately escalated your case with **HIGH priority** — Ticket **#{ticket.Id}** has been created. " +
                                    $"Our team will contact you as soon as possible.";

                                assembledText.Append(escalationMsg);
                                pendingYieldText = escalationMsg;
                            }
                        }
                        else if (string.Equals(funcName, "createTicket", StringComparison.OrdinalIgnoreCase))
                        {
                            var issue         = funcArgs?["issue"]?.ToString() ?? userMessage;
                            var category      = funcArgs?["category"]?.ToString() ?? "other";
                            var priority      = funcArgs?["priority"]?.ToString() ?? "medium";
                            var ticketName    = funcArgs?["customerName"]?.ToString() ?? username;
                            // SECURITY: JWT email for authenticated users; LLM arg only for guests
                            var ticketEmail   = authenticatedUserId.HasValue ? email : (funcArgs?["customerEmail"]?.ToString() ?? email);

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
                                var activeTicketRef = await GetSessionActiveTicketAsync(sessionId, conversationHistory);
                                var isExpl = IsExplanatoryQuery(userMessage) || IsExplanatoryQuery(issue);
                                var hasExplicitNewTicket = System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(another|new|second|separate)\s+ticket\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                                if (activeTicketRef != null && !hasExplicitNewTicket)
                                {
                                    _logger.LogInformation("StreamChatAsync: createTicket deduplicated: Active ticket {ActiveRef} already exists for session {SessionId}.", activeTicketRef, sessionId);
                                    var dedupReply = isExpl
                                        ? $"Chats on our platform are encrypted in transit using TLS 1.3 and at rest with AES-256. Leaks or unauthorized access typically occur due to an exposed password, an active session left open on a shared device, or credential compromise elsewhere.\n\nYour existing incident ticket (**{activeTicketRef}**) is already actively assigned to our security engineering team, and they are reviewing your account logs. Please ensure you change your password in **Settings > Account** and click **Log out from all devices**."
                                        : $"Your request is already being tracked under active ticket **{activeTicketRef}**. Our team has been notified and will follow up with you directly. If you have any additional details to add, please share them here and they will be noted!";

                                    assembledText.Append(dedupReply);
                                    pendingYieldText = dedupReply;
                                }
                                else if (isExpl && !System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(create|open|file|make)\s+(a\s+)?ticket\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                {
                                    _logger.LogInformation("StreamChatAsync: createTicket declined for explanatory query '{UserMessage}'.", userMessage);
                                    var explReply = "Chats on ChatApp are protected with TLS 1.3 encryption in transit and AES-256 encryption at rest. If messages appear exposed, common causes include:\n" +
                                        "1. **Reused or Compromised Passwords**: If your password was compromised on another service.\n" +
                                        "2. **Active Sessions on Other Devices**: If your account remained logged in on a shared computer or browser.\n" +
                                        "3. **Phishing or Device Access**: If unauthorized individuals gained physical or remote access to your device.\n\n" +
                                        "To protect your account right now, please go to **Settings > Account** to change your password and log out of all active sessions. If you would like me to create a security support ticket for our engineering team to review your logs, please let me know!";

                                    assembledText.Append(explReply);
                                    pendingYieldText = explReply;
                                }
                                else
                                {
                                    var ticket = await _ticketService.CreateTicketAsync(
                                        customerId, issue, category, priority,
                                        ticketName, ticketEmail, authenticatedUserId);

                                    ticketCreated = true;
                                    ticketId      = ticket.Id;
                                    try { await _memoryService.SaveFactAsync(sessionId, "active_ticket_ref", $"TICK-{ticket.Id}", "support_verified"); } catch {}

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
                    }
                    else if (string.Equals(funcName, "getTicketStatus", StringComparison.OrdinalIgnoreCase))
                        {
                            var ticketRef = funcArgs?["ticketIdOrRef"]?.ToString();
                            // SECURITY (GAP-01 FIX): Authenticated users scoped to JWT identity only
                            string? statusEmail = authenticatedUserId.HasValue ? null : email;
                            var statusResult = await _ticketService.GetTicketStatusAsync(ticketRef, authenticatedUserId, statusEmail);

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
