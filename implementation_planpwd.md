# Implementation Plan: Forgot Password & Generic OTP Recovery System

We will implement a secure, loosely-coupled, production-grade **Password Recovery (Forgot Password)** flow for ChatApp, driven by a **generic, purpose-aware OTP engine** that can be reused for future features (e.g. Forgot Username, Email Verification, 2FA).

---

## 1. Existing Architecture & Reusability Audit

### Password Handling (Reused As-Is)
* **Hashing Algorithm**: `BCrypt.Net.BCrypt.HashPassword` (WorkFactor = 10/11) inside [`AuthService.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Services/AuthService.cs#L127-L135).
* **Verification Algorithm**: `BCrypt.Net.BCrypt.Verify(password, passwordHash)` in [`AuthService.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Services/AuthService.cs#L132-L135).
* **Password Validation**: Currently minimum 6 characters in [`UserDtos.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/DTOs/UserDtos.cs#L31) and [`Register.jsx`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/chat-app-ui/src/components/Auth/Register.jsx#L23). We will extract this into an [`IPasswordValidator`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Interfaces/IPasswordValidator.cs) so both Registration and Password Reset share the exact same validation rules.
* **Storage**: One-way hash stored in SQL Server `Users.PasswordHash`. Passwords are never decrypted or logged.

### Email Integration (Reused As-Is)
* The OTP service will **not** touch SMTP directly. It will queue messages via [`IEmailService`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Interfaces/IEmailService.cs), which writes to [`EmailOutboxes`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Domain/Entities/EmailOutbox.cs) and dispatches via [`EmailQueueWorker`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Email/Background/EmailQueueWorker.cs) using whichever provider is configured (`SmtpEmailProvider` or `DevConsoleEmailProvider`).

---

## 2. Proposed Architecture & Component Design

```
React (chat-app-ui)
  │  [Forgot Password Form: Step 1 Email → Step 2 OTP → Step 3 Old + New Password]
  ▼
AuthController (POST /api/auth/forgot-password, verify-otp, reset-password)
  │
  ▼
PasswordRecoveryService (ChatApp.Application)
  ├── IUserRepository (Get user, update password)
  ├── IPasswordValidator (Validate password rules)
  ├── IAuthService (Verify old password hash, hash new password)
  └── IOtpService (Generic OTP lifecycle engine)
         │
         ├── IOtpRepository (Store/retrieve hashed OTP, attempts, single-use tokens)
         └── IEmailService (Durable SQL Outbox → Worker → SMTP/DevConsole)
```

### Generic, Purpose-Aware OTP Engine
* **`OtpPurpose` Enum**: `PasswordReset`, `ForgotUsername`, `EmailVerification`, `TwoFactorAuthentication`.
* **Security & Hashing**:
  * Generated with `RandomNumberGenerator.GetInt32(100000, 1000000)`.
  * The raw OTP is **never stored in plaintext** in the database; only a SHA-256/HMAC hash (`OtpHash`) is stored.
  * **Brute-force protection**: Tracks `Attempts`. If failed attempts reach `MaxAttempts` (default: 5), the OTP is immediately invalidated.
  * **Expiration**: Configurable (default: 5 minutes).
  * **Reset Authorization Token**: Upon successful OTP verification in Step 2, the server generates a cryptographically secure, single-use `ResetToken` (valid for 15 minutes) associated with the verified record. Step 3 requires this token.

---

## 3. Proposed File Changes

### Domain Layer (`ChatApp.Domain`)
* [NEW] [`OtpPurpose.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Domain/Entities/OtpPurpose.cs): Enum defining OTP purposes (`PasswordReset`, `ForgotUsername`, `EmailVerification`, `TwoFactorAuthentication`).
* [NEW] [`OtpVerification.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Domain/Entities/OtpVerification.cs): Entity storing `Id`, `UserId`, `Recipient`, `Purpose`, `OtpHash`, `ResetToken`, `ResetTokenExpiresAt`, `ExpiresAt`, `Attempts`, `MaxAttempts`, `IsUsed`, `CreatedAt`, `VerifiedAt`.

### Application Layer (`ChatApp.Application`)
* [NEW] [`OtpDtos.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/DTOs/OtpDtos.cs): `ForgotPasswordRequestDto`, `VerifyOtpRequestDto`, `VerifyOtpResponseDto`, `ResetPasswordDto`, `OtpResultDto`.
* [NEW] [`IOtpRepository.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Interfaces/IOtpRepository.cs): Clean persistence abstraction for OTP entities (zero EF Core coupling).
* [NEW] [`IOtpService.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Interfaces/IOtpService.cs): Generic OTP lifecycle contract.
* [NEW] [`IPasswordValidator.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Interfaces/IPasswordValidator.cs): Shared password validation interface.
* [NEW] [`PasswordValidator.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Services/PasswordValidator.cs): Shared implementation validating min length (6), required characters.
* [NEW] [`IPasswordRecoveryService.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Interfaces/IPasswordRecoveryService.cs): Contract for the 3-step recovery flow.
* [NEW] [`PasswordRecoveryService.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Services/PasswordRecoveryService.cs): Coordinates user lookup, generic OTP service, old-password verification via `IAuthService`, new-password validation, and atomic password update.
* [MODIFY] [`EmailTemplateType.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Application/Enums/EmailTemplateType.cs): Ensure `PasswordResetOtp` or `PasswordReset` template type is mapped.

### Infrastructure Layer (`ChatApp.Infrastructure`)
* [NEW] [`OtpSettings.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Options/OtpSettings.cs): Strongly-typed options (`Length`, `ExpirationMinutes`, `MaxAttempts`, `ResetTokenExpirationMinutes`).
* [NEW] [`OtpRepository.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Repositories/OtpRepository.cs): EF Core implementation of `IOtpRepository`.
* [NEW] [`OtpService.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Services/OtpService.cs): Implements generic OTP generation, SHA-256 hashing, rate limiting, and email dispatch via `IEmailService`.
* [NEW] [`PasswordResetOtp.html`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Email/Templates/PasswordResetOtp.html): Dedicated responsive HTML template displaying the 6-digit OTP code, expiration warning, and security note.
* [MODIFY] [`ChatDbContext.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.Infrastructure/Data/ChatDbContext.cs): Add `DbSet<OtpVerification> OtpVerifications` and composite indexes on `(Recipient, Purpose, IsUsed)` and `(ResetToken)`.

### API Layer (`ChatApp.API`)
* [MODIFY] [`AuthController.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.API/Controllers/AuthController.cs): Add endpoints:
  * `POST /api/auth/forgot-password` (Anti-enumeration safe: returns 200 generic message whether email exists or not).
  * `POST /api/auth/forgot-password/verify-otp` (Validates OTP, returns `{ verified: true, resetToken: "..." }`).
  * `POST /api/auth/reset-password` (Validates `resetToken`, verifies `oldPassword`, checks `newPassword`, hashes, updates DB).
* [MODIFY] [`Program.cs`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.API/Program.cs): Register `IOtpRepository`, `IOtpService`, `IPasswordValidator`, `IPasswordRecoveryService`, and bind `OtpSettings`.
* [MODIFY] [`appsettings.json`] / [`appsettings.Development.json`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/ChatApp.API/appsettings.Development.json): Add `OtpSettings` section.
* [NEW] Database Migration: `AddOtpVerifications` via EF Core CLI.

### Frontend (`chat-app-ui`)
* [NEW] [`ForgotPassword.jsx`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/chat-app-ui/src/components/Auth/ForgotPassword.jsx): Interactive 3-step wizard matching existing `Auth.css` styles:
  * **Step 1**: Enter email → `Send OTP`.
  * **Step 2**: Enter 6-digit OTP with attempt feedback → `Verify OTP` + `Resend OTP`.
  * **Step 3**: Enter Old Password + New Password + Confirm New Password → `Reset Password`.
* [MODIFY] [`Login.jsx`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/chat-app-ui/src/components/Auth/Login.jsx): Add "Forgot Password?" link to navigate to `/forgot-password`.
* [MODIFY] [`App.jsx`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/chat-app-ui/src/App.jsx): Register `/forgot-password` public route.
* [MODIFY] [`api.js`](file:///C:/Users/Bhanu%20Dwivedi/source/repos/ChatApp/chat-app-ui/src/services/api.js): Add `forgotPassword`, `verifyOtp`, and `resetPassword` methods to `authAPI`.

---

## 4. Verification Plan

### Automated Build & Database Verification
1. **EF Core Migration**:
   ```powershell
   dotnet ef migrations add AddOtpVerifications --project ChatApp.Infrastructure --startup-project ChatApp.API
   dotnet ef database update --project ChatApp.Infrastructure --startup-project ChatApp.API
   ```
2. **Compile Backend**:
   ```powershell
   dotnet build ChatApp.sln
   ```
3. **Compile Frontend**:
   ```powershell
   npm run build --prefix chat-app-ui
   ```

### Functional Flow Testing
1. **Request OTP (`POST /api/auth/forgot-password`)**:
   - Send valid registered email (`bhanudwi96@gmail.com`).
   - Verify `OtpVerifications` table contains hashed OTP.
   - Verify `EmailOutboxes` table contains the email job, and `EmailQueueWorker` dispatches the OTP email.
2. **Verify OTP (`POST /api/auth/forgot-password/verify-otp`)**:
   - Submit invalid OTP → verify attempt count increments, returns descriptive error.
   - Submit correct OTP → verify response returns `{ verified: true, resetToken: "..." }`.
3. **Reset Password (`POST /api/auth/reset-password`)**:
   - Submit wrong old password → verify failure with *"Current password is incorrect"*.
   - Submit invalid new password (< 6 chars) → verify failure with *"Password must be at least 6 characters"*.
   - Submit correct old password + valid new password → verify success.
   - Try to reuse the same `resetToken` again → verify immediate rejection (single-use protection).
4. **Login with New Password**:
   - Log in using old password → verify login fails.
   - Log in using new password → verify login succeeds and enters chat room!
