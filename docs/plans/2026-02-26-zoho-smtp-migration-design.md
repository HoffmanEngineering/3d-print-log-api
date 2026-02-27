# Design: Migrate Email Provider from SendGrid to Zoho Mail (SMTP via MailKit)

**Date:** 2026-02-26
**Status:** Approved

## Context

SendGrid removed its free tier. The application currently uses the SendGrid SDK to send a single type of transactional email: a feedback notification sent to an internal address when a user submits feedback. Zoho Mail is already available and supports standard SMTP.

## Approach

Replace the SendGrid SDK with MailKit, the de-facto standard SMTP library for modern .NET. MailKit is actively maintained, properly async, and handles TLS correctly with all major providers including Zoho.

Alternatives considered:
- `System.Net.Mail.SmtpClient` — built-in but deprecated by Microsoft; known async issues
- Zoho ZeptoMail (HTTP API) — unnecessary complexity; Zoho Mail SMTP already works

## Files Changed

| Action | File |
|--------|------|
| Remove | `Services/SendGridEmailSender.cs` |
| Remove | `Models/SendGrid/SendGridEmailSenderOptions.cs` |
| Update | `Services/IEmailSender.cs` — remove leaked `Options` property |
| Add | `Services/SmtpEmailSender.cs` |
| Add | `Models/Smtp/SmtpEmailSenderOptions.cs` |
| Update | `Startup.cs` — swap DI registration and config binding |
| Update | `appsettings.json` — replace `SendGrid` section with `Smtp` |
| Update | `PrintLogApi.csproj` — remove SendGrid package, add MailKit |

## Interface

The `Options` property is removed from `IEmailSender` — it was an implementation detail that had leaked onto the interface. The interface becomes:

```csharp
public interface IEmailSender
{
    Task SendEmailAsync(string email, string subject, string message);
}
```

## Options Model

```csharp
// Models/Smtp/SmtpEmailSenderOptions.cs
public class SmtpEmailSenderOptions
{
    public string Host { get; set; }        // smtp.zoho.com
    public int Port { get; set; }           // 587
    public string Username { get; set; }    // Zoho email address
    public string Password { get; set; }    // Zoho app password
    public string SenderEmail { get; set; }
    public string SenderName { get; set; }
}
```

## Configuration Shape

`appsettings.json` (non-sensitive defaults only):

```json
"ExternalProviders": {
  "Smtp": {
    "Host": "smtp.zoho.com",
    "Port": 587,
    "Username": "",
    "Password": "",
    "SenderEmail": "hello@3dprintlog.com",
    "SenderName": "Hello from 3D Print Log"
  }
}
```

Sensitive values (`Username`, `Password`) remain empty in `appsettings.json` and are populated via Azure App Service environment variables or user secrets, consistent with the existing SendGrid `ApiKey` pattern.

## Implementation

`SmtpEmailSender` uses MailKit's `SmtpClient` to:
1. Connect to Zoho SMTP (`smtp.zoho.com:587`) with STARTTLS
2. Authenticate with `Username`/`Password`
3. Send the message (plain text + HTML, matching current behavior)
4. Disconnect

Each call to `SendEmailAsync` opens and closes its own connection, matching the current one-off send pattern. This is appropriate given the low frequency of feedback emails.

## Notes

- The `Models/SendGrid/` folder and `SendGrid` NuGet package are fully removed
- No changes to `FeedbacksController` — it only calls `SendEmailAsync`
- No database migrations required
