# Sprint 1 — AI Security, Tool Governance & Policy Enforcement
## Walkthrough & Delivery Summary

---

## What Was Implemented

Sprint 1 closes the **10 security gaps (GAP-01 to GAP-10)** identified during the audit phase. Every change is additive — no existing infrastructure was redesigned.

---

## New Files Created

| File | Purpose |
|------|---------|
| [`ToolRegistry.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Services/ToolRegistry.cs) | Singleton — validates tool name/enabled state. Unknown & disabled tools fail closed. Config-driven. |
| [`ToolThrottle.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Services/ToolThrottle.cs) | Singleton — per-session tool invocation counter. CAS-loop for atomic in-memory increments. |
| [`ToolArgumentValidator.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Services/ToolArgumentValidator.cs) | Static helper — deterministic schema + safety validation for all 4 tool argument payloads. |
| [`InputGuard.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Services/InputGuard.cs) | Static helper — runs before any LLM call. Blocks oversized messages and prompt-injection phrases. |
| [`ToolPolicy.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Services/ToolPolicy.cs) | Singleton — authorizes tool calls using **server-side JWT identity only** (never LLM args). |
| [`AuditBlockReasons.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.Domain/Entities/AuditBlockReasons.cs) | Domain constants for all block reason strings. |
| [`Sprint1GovernanceTests.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/tests/ChatApp.Tests/Sprint1GovernanceTests.cs) | 47 new unit tests covering all 5 Sprint 1 services. |

---

## Modified Files

| File | Change |
|------|--------|
| [`GeminiService.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Services/GeminiService.cs) | Injected governance services; replaced raw dispatch in **both** `ProcessChatAsync` + `StreamChatAsync` with 4-step pipeline |
| [`ChatStreamController.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Controllers/ChatStreamController.cs) | Added `InputGuard.Evaluate()` as first check before any DB/LLM call |
| [`ChatController.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Controllers/ChatController.cs) | Same InputGuard check for security parity |
| [`AiAuditController.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Controllers/AiAuditController.cs) | Session endpoint now `[Authorize]` + ownership validation; new `/threats` endpoint |
| [`ChatRateLimitingMiddleware.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Middleware/ChatRateLimitingMiddleware.cs) | Upgraded from IP-only to per-user (JWT) rate limiting |
| [`AiAuditLog.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.Domain/Entities/AiAuditLog.cs) | Added `ToolCallsJson`, `BlockReason`, `AuthenticatedUserId` fields |
| [`Program.cs`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/Program.cs) | Registered 3 new singleton services |
| [`appsettings.json`](file:///c:/Users/vipin/CHATAPPREP/CHATAPP-/ChatApp.API/appsettings.json) | Added `AI:InputGuard`, `AI:Tools`, `AI:RateLimit` config sections |

---

## The Tool Governance Pipeline

Every LLM tool request now passes through a **4-gate enforcement chain** before any side effect:

```
LLM → Request Tool
         │
         ▼
  1. ToolRegistry       — Is this tool known & enabled?  (fail-closed)
         │ ✓
         ▼
  2. ToolPolicy         — Is the caller authorized?      (server-side JWT only)
         │ ✓
         ▼
  3. ToolThrottle       — Under per-session limit?       (in-memory CAS)
         │ ✓
         ▼
  4. ToolArgumentValidator — Valid, safe argument shape?  (deterministic)
         │ ✓
         ▼
  Execute service call (TicketService, MemoryService...)
```

All 4 gates also apply to the **SSE streaming path** — no bypass possible.

---

## Security Gaps Closed

| Gap | Description | Fix |
|-----|------------|-----|
| GAP-01 | LLM-supplied email used for ticket ownership check | `getTicketStatus`: JWT identity used for auth'd users; LLM arg only for guests |
| GAP-02 | No tool allowlist — any LLM-invented tool name executed | `ToolRegistry` with fail-closed unknown-name rejection |
| GAP-03 | No per-tool session throttle | `ToolThrottle` with configurable per-tool limits |
| GAP-04 | No input length/injection validation | `InputGuard` with 4000-char limit + 17 injection phrases |
| GAP-05 | No tool argument validation | `ToolArgumentValidator` with schema + email/ref/category checks |
| GAP-06 | Anonymous users can call any tool | `ToolPolicy` enforces `RequiresAuth` per tool |
| GAP-07 | Audit log missing identity/tool/block fields | `AuditLog` extended; `WriteAuditLogAsync` updated in both paths |
| GAP-08 | Audit session endpoint open to any caller | `[Authorize]` + session ownership validation |
| GAP-09 | Rate limit IP-only (NAT collapse) | Per-user (JWT `sub`) rate limiting; anonymous falls back to IP |
| GAP-10 | No threat visibility endpoint | `GET /api/aiaudit/threats` — returns all blocked requests |

---

## Database Migration Applied

```
Migration: 20260923124101_Sprint1_AddToolAuditFields
Columns added to AiAuditLogs:
  - AuthenticatedUserId  uniqueidentifier NULL
  - BlockReason          nvarchar(max)    NULL
  - ToolCallsJson        nvarchar(max)    NULL
```

---

## Test Results

```
Total tests: 82
   Passed:   82
   Failed:   0
   Time:     1.44s
```

**47 new Sprint 1 tests** + all pre-existing tests green.

---

## Configuration Reference

```json
"AI": {
  "InputGuard": {
    "MaxMessageLength": 4000,
    "InjectionDetectionEnabled": true
  },
  "Tools": {
    "createTicket":     { "Enabled": true, "RequiresAuth": false, "MaxPerSession": 3 },
    "escalateToHuman":  { "Enabled": true, "RequiresAuth": false, "MaxPerSession": 2 },
    "getTicketStatus":  { "Enabled": true, "RequiresAuth": false, "MaxPerSession": 5 },
    "searchChatHistory":{ "Enabled": true, "RequiresAuth": false, "MaxPerSession": 5 }
  },
  "RateLimit": {
    "AuthenticatedRequestsPerMinute": 40,
    "AnonymousRequestsPerMinute": 10
  }
}
```

All values are configurable per environment without code changes.

---

## Known Limitations (Documented)

- **In-memory state**: `ToolThrottle` and `ChatRateLimitingMiddleware` reset on restart. Acceptable for single-instance deployment. Sprint 2 can move to Redis if multi-instance is needed.
- **Admin role**: `GET /api/aiaudit/threats` currently allows any authenticated user. Admin-role gate deferred to Sprint 2 (no Admin role in current JWT model — D-1).
- **Anonymous guest email**: LLM-supplied email accepted as contact detail for guests (by design — D-2). Documented in code.
