# Loosely-Coupled Email & Customer Support System

## Goal Description
Integrate a robust, modular, and future-proof **Email System** with an initial **Customer Support** capability into the ChatApp project.
The architecture is designed using the **Ports & Adapters (Hexagonal / Clean Architecture) Pattern** so that:
1. Any module across the app (Customer Support, Auth/Password Reset, Notifications, System Alerts) can dispatch emails through a unified abstraction without knowing *how* they are delivered.
2. The delivery mechanism is interchangeable via configuration (e.g., standard SMTP/Gmail, MailKit, AWS SES, SendGrid, or a mock console logger in development).
3. Email sending is completely non-blocking (asynchronous in-memory background worker with `System.Threading.Channels.Channel<T>`), ensuring zero UI/API latency.
4. If in the future you wish to extract the email engine into a standalone microservice (e.g., Azure Function, RabbitMQ worker, or Docker container), **zero business logic in the application or domain layers will need to change**.

---

## User Review Required
> [!IMPORTANT]
> **Email Provider Selection**:
> For local development and testing, we will include a **DevConsole/Mock Email Provider** (prints full email contents, recipient, and formatted HTML preview to console/logs without needing real credentials) alongside a standard **SMTP Provider** (supporting Gmail, Outlook, AWS SES, or any custom SMTP server).
> 
> Please confirm if you would like to start with:
> - **Option A (Recommended for Dev)**: DevConsole Provider for instant testing + SMTP configured via `appsettings.Development.json` (you can plug in your Gmail or SMTP credentials whenever ready).
> - **Option B**: SendGrid or AWS SES API key integration directly.

> [!NOTE]
> **Package Addition**:
> We will add `MailKit` and `MimeKit` (the standard, high-performance .NET email libraries replacing the deprecated `System.Net.Mail.SmtpClient`) to `ChatApp.Infrastructure`.

---

## Open Questions
> [!WARNING]
> 1. **Support Ticket Persistence**: Should customer support inquiries also be stored in the database (`SupportTickets` table) with an assigned Ticket ID for tracking/audit, in addition to dispatching the notification email to your support inbox? *(Recommended: Yes, gives full history and audit trail).*
> 2. **Support Recipient Email**: What default support email address should inquiries be delivered to (e.g., `support@chatapp.local` or a specific admin address)? This can be specified in `appsettings.json`.

---

## Proposed Architecture: Loosely-Coupled Email Engine

```
                                    +-----------------------------------------+
                                    |         ChatApp.Application             |
                                    |                                         |
[ Customer Support Feature ] ---->  |   IEmailService                         |  <---- [ Future: Password Reset ]
[ Chat Alerts / Offline Digest ] -> |   EmailMessage / EmailTemplates         |  <---- [ Future: Security Alert ]
                                    +-----------------------------------------+
                                                         |
                                     (Dependency Inversion Boundary)
                                                         |
                                    +-----------------------------------------+
                                    |        ChatApp.Infrastructure           |
                                    |                                         |
                                    |  +-----------------------------------+  |
                                    |  | Background Channel Queue Worker   |  | (Zero API latency)
                                    |  +-----------------------------------+  |
                                    |                   |                     |
                                    |                   v                     |
                                    |        IEmailProvider (Strategy)        |
                                    |       /          |           \          |
                                    |  SmtpProvider DevProvider (Future SES)  |
                                    +-----------------------------------------+
```

---

## Proposed Changes

### ChatApp.Domain
#### [NEW] [SupportTicket.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Domain/Entities/SupportTicket.cs)
- Represents a customer support inquiry.
- Fields: `Id`, `UserId` (optional for guests), `UserEmail`, `Subject`, `Message`, `Category` (e.g., Bug, Account, Billing, Feature Request), `Status` (Open, InProgress, Resolved), `CreatedAt`, `ResolvedAt`.

---

### ChatApp.Application
#### [NEW] [IEmailService.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Interfaces/IEmailService.cs)
- Core application contract:
  - `Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);`
  - `Task SendTemplateAsync<TModel>(string to, string subject, string templateName, TModel model);`

#### [NEW] [EmailMessage.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/DTOs/EmailDtos.cs)
- DTOs: `EmailMessage` (To, Cc, Bcc, Subject, HtmlBody, PlainTextBody, Attachments), `SupportTicketDto`, `CreateSupportTicketDto`.

#### [NEW] [ISupportTicketService.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Interfaces/ISupportTicketService.cs)
- Business logic interface for creating tickets, confirming receipt to the user, and forwarding the alert to the support team.

#### [NEW] [SupportTicketService.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Services/SupportTicketService.cs)
- Implements ticket creation and triggers `IEmailService.SendAsync(...)` for:
  1. Auto-reply confirmation to the customer with Ticket Reference ID.
  2. Escalation email to the support team admin inbox with customer info and message.

---

### ChatApp.Infrastructure
#### [NEW] [EmailSettings.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Options/EmailSettings.cs)
- Strongly typed configuration:
  - `Provider` ("Smtp", "Console", "SendGrid")
  - `SmtpHost`, `SmtpPort`, `UseSsl`, `SenderName`, `SenderEmail`, `Username`, `Password`
  - `SupportInboxEmail` (where customer messages land)

#### [NEW] [IEmailProvider.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Email/IEmailProvider.cs)
- Low-level provider contract for interchangeable mail delivery strategies.

#### [NEW] [SmtpEmailProvider.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Email/SmtpEmailProvider.cs)
- MailKit/MimeKit implementation with secure TLS/SSL, HTML email rendering, and fallback error logging.

#### [NEW] [DevConsoleEmailProvider.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Email/DevConsoleEmailProvider.cs)
- Dev-friendly provider that logs email details to the console/logger without requiring third-party credentials.

#### [NEW] [BackgroundEmailQueue.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Email/BackgroundEmailQueue.cs)
- In-memory bounded channel (`Channel<EmailMessage>`) preventing thread starvation and guaranteeing that sending an email never adds latency to HTTP requests.

#### [NEW] [EmailQueueWorker.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Email/EmailQueueWorker.cs)
- `BackgroundService` consuming queued emails and dispatching them through the configured `IEmailProvider` with auto-retry.

#### [MODIFY] [ChatDbContext.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Data/ChatDbContext.cs)
- Add `DbSet<SupportTicket> SupportTickets` and index configuration.

---

### ChatApp.API
#### [NEW] [SupportController.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.API/Controllers/SupportController.cs)
- Endpoints:
  - `POST /api/support/ticket` (Submit support request with validation, authenticated or guest)
  - `GET /api/support/my-tickets` (View ticket history for logged-in user)

#### [MODIFY] [Program.cs](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.API/Program.cs)
- Register `EmailSettings`, `IEmailService`, `IEmailProvider`, `BackgroundEmailQueue`, and `EmailQueueWorker`.

#### [MODIFY] [appsettings.Development.json](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.API/appsettings.Development.json)
- Add `EmailSettings` section.

---

### chat-app-ui (Frontend)
#### [NEW] [SupportModal.jsx](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/chat-app-ui/src/components/Support/SupportModal.jsx)
- Clean, accessible modal accessible from the chat sidebar or user profile dropdown.
- Category dropdown (Bug, Account Help, Feedback, General).
- Subject & Message textarea with auto-fill of current user's email.
- Real-time submission state, ticket confirmation ID display, and auto-dismiss.

---

## Verification Plan

### Automated / Build Verification
- Compile the solution:
  ```powershell
  dotnet build ChatApp.sln
  ```
- Run EF Core migration to verify database schema update:
  ```powershell
  dotnet ef migrations add AddSupportTickets --startup-project ChatApp.API --project ChatApp.API
  dotnet ef database update --startup-project ChatApp.API --project ChatApp.API
  ```

### Manual Verification
1. **API Test**:
   - Send `POST /api/support/ticket` with test payload `{ "subject": "Test Bug", "message": "Chat window scroll issue", "category": "Bug" }`.
   - Verify 200 OK returned in < 20ms (due to async queue).
   - Check API logs to see formatted email dispatch preview (or real SMTP delivery if configured).
2. **Database Verification**:
   - Query `SELECT TOP 1 * FROM SupportTickets ORDER BY CreatedAt DESC` to ensure ticket record saved with unique ID.
3. **Frontend UI Test**:
   - Open Support Modal from Chat UI.
   - Submit a ticket.
   - Check confirmation banner with Ticket ID.
