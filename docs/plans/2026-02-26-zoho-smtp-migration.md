# Zoho SMTP Migration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the SendGrid SDK with MailKit and Zoho SMTP for sending feedback notification emails.

**Architecture:** Swap `SendGridEmailSender` for a new `SmtpEmailSender` backed by MailKit. The `IEmailSender` interface is cleaned up by removing its leaked `Options` property. Config keys shift from `ExternalProviders:SendGrid` to `ExternalProviders:Smtp`. No controller or test changes are needed.

**Tech Stack:** ASP.NET Core 9.0, MailKit 4.x, Zoho Mail SMTP (`smtp.zoho.com:587` + STARTTLS)

---

### Task 1: Swap NuGet packages

**Files:**
- Modify: `PrintLogApi/PrintLogApi.csproj`

**Step 1: Remove SendGrid, add MailKit**

In `PrintLogApi.csproj`, replace:
```xml
<PackageReference Include="SendGrid" Version="9.29.3" />
```
with:
```xml
<PackageReference Include="MailKit" Version="4.9.0" />
```

**Step 2: Restore packages**

```bash
dotnet restore PrintLogApi/PrintLogApi.csproj
```
Expected: restore succeeds, no errors.

**Step 3: Verify build fails on SendGrid references (expected)**

```bash
dotnet build PrintLogApi/PrintLogApi.csproj
```
Expected: build errors referencing `SendGrid` namespace — that's correct, we'll fix them in subsequent tasks.

---

### Task 2: Add SmtpEmailSenderOptions

**Files:**
- Create: `PrintLogApi/Models/Smtp/SmtpEmailSenderOptions.cs`

**Step 1: Create the options model**

Create `PrintLogApi/Models/Smtp/SmtpEmailSenderOptions.cs`:

```csharp
namespace PrintLogApi.Models.Smtp
{
    public class SmtpEmailSenderOptions
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string SenderEmail { get; set; }
        public string SenderName { get; set; }
    }
}
```

---

### Task 3: Create SmtpEmailSender

**Files:**
- Create: `PrintLogApi/Services/SmtpEmailSender.cs`

**Step 1: Create the implementation**

Create `PrintLogApi/Services/SmtpEmailSender.cs`:

```csharp
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using PrintLogApi.Models.Smtp;

namespace PrintLogApi.Services
{
    public class SmtpEmailSender : IEmailSender
    {
        private readonly SmtpEmailSenderOptions _options;

        public SmtpEmailSender(IOptions<SmtpEmailSenderOptions> options)
        {
            _options = options.Value;
        }

        public async Task SendEmailAsync(string email, string subject, string message)
        {
            var mimeMessage = new MimeMessage();
            mimeMessage.From.Add(new MailboxAddress(_options.SenderName, _options.SenderEmail));
            mimeMessage.To.Add(new MailboxAddress(string.Empty, email));
            mimeMessage.Subject = subject;

            mimeMessage.Body = new BodyBuilder
            {
                HtmlBody = message,
                TextBody = message
            }.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_options.Username, _options.Password);
            await client.SendAsync(mimeMessage);
            await client.DisconnectAsync(true);
        }
    }
}
```

---

### Task 4: Update IEmailSender interface

**Files:**
- Modify: `PrintLogApi/Services/IEmailSender.cs`

**Step 1: Remove the leaked Options property**

Replace the entire file contents with:

```csharp
using System.Threading.Tasks;

namespace PrintLogApi.Services
{
    public interface IEmailSender
    {
        Task SendEmailAsync(string email, string subject, string message);
    }
}
```

The old interface imported `PrintLogApi.Models.SendGrid` and exposed `SendGridEmailSenderOptions Options { get; set; }` — both are removed.

---

### Task 5: Update Startup.cs

**Files:**
- Modify: `PrintLogApi/Startup.cs`

**Step 1: Remove the SendGrid using directive**

Remove this line near the top of `Startup.cs`:
```csharp
using PrintLogApi.Models.SendGrid;
```

Add in its place:
```csharp
using PrintLogApi.Models.Smtp;
```

**Step 2: Swap the DI registration**

Replace:
```csharp
services.AddTransient<IEmailSender, SendGridEmailSender>();
services.Configure<SendGridEmailSenderOptions>(options =>
{
    options.ApiKey = Configuration["ExternalProviders:SendGrid:ApiKey"];
    options.SenderEmail = Configuration["ExternalProviders:SendGrid:SenderEmail"];
    options.SenderName = Configuration["ExternalProviders:SendGrid:SenderName"];
});
```

With:
```csharp
services.AddTransient<IEmailSender, SmtpEmailSender>();
services.Configure<SmtpEmailSenderOptions>(options =>
{
    options.Host = Configuration["ExternalProviders:Smtp:Host"];
    options.Port = int.Parse(Configuration["ExternalProviders:Smtp:Port"] ?? "587");
    options.Username = Configuration["ExternalProviders:Smtp:Username"];
    options.Password = Configuration["ExternalProviders:Smtp:Password"];
    options.SenderEmail = Configuration["ExternalProviders:Smtp:SenderEmail"];
    options.SenderName = Configuration["ExternalProviders:Smtp:SenderName"];
});
```

**Step 3: Verify build succeeds**

```bash
dotnet build PrintLogApi/PrintLogApi.csproj
```
Expected: 0 errors. There may be warnings about the old files still existing — that's fine, the next task removes them.

---

### Task 6: Delete old SendGrid files

**Files:**
- Delete: `PrintLogApi/Services/SendGridEmailSender.cs`
- Delete: `PrintLogApi/Models/SendGrid/SendGridEmailSenderOptions.cs`
- Delete: `PrintLogApi/Models/SendGrid/` (directory, now empty)

**Step 1: Delete the files**

```bash
rm PrintLogApi/Services/SendGridEmailSender.cs
rm PrintLogApi/Models/SendGrid/SendGridEmailSenderOptions.cs
rmdir PrintLogApi/Models/SendGrid
```

**Step 2: Verify build still succeeds**

```bash
dotnet build PrintLogApi/PrintLogApi.csproj
```
Expected: 0 errors, 0 warnings about SendGrid.

---

### Task 7: Update configuration files

**Files:**
- Modify: `PrintLogApi/appsettings.json`
- Modify: `PrintLogApi/appsettings.Development.json`

**Step 1: Update appsettings.json**

Replace the `ExternalProviders` section:
```json
"ExternalProviders": {
  "SendGrid": {
    "ApiKey": "",
    "SenderEmail": "hello@3dprintlog.com",
    "SenderName": "Hello from 3D Print Log"
  }
}
```
With:
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

**Step 2: Update appsettings.Development.json**

Replace the `ExternalProviders` section:
```json
"ExternalProviders": {
  "SendGrid": {
    "ApiKey": "SG.u23...",
    "SenderEmail": "hello@3dprintlog.com",
    "SenderName": "Hello from 3D Print Log"
  }
}
```
With:
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

Leave `Username` and `Password` empty here — fill them in via user secrets for local development:
```bash
cd PrintLogApi
dotnet user-secrets set "ExternalProviders:Smtp:Username" "your-zoho-email@yourdomain.com"
dotnet user-secrets set "ExternalProviders:Smtp:Password" "your-zoho-app-password"
```

> **Note on Zoho app passwords:** If your Zoho account has two-factor authentication enabled, generate an app-specific password in Zoho Mail settings under Security > App Passwords. Use that instead of your account password.

For production, set these as Azure App Service environment variables:
- `ExternalProviders__Smtp__Username`
- `ExternalProviders__Smtp__Password`

(Azure uses `__` as the config key separator.)

---

### Task 8: Run tests and commit

**Step 1: Run all tests**

```bash
dotnet test --verbosity quiet
```
Expected: all existing tests pass. The feedback integration tests work fine because `FeedbackEmailAddress` is empty in the test appsettings, so `SendEmailAsync` is never called during tests.

**Step 2: Commit**

```bash
git add PrintLogApi/PrintLogApi.csproj \
        PrintLogApi/Models/Smtp/SmtpEmailSenderOptions.cs \
        PrintLogApi/Services/SmtpEmailSender.cs \
        PrintLogApi/Services/IEmailSender.cs \
        PrintLogApi/Startup.cs \
        PrintLogApi/appsettings.json \
        PrintLogApi/appsettings.Development.json
git rm PrintLogApi/Services/SendGridEmailSender.cs \
       PrintLogApi/Models/SendGrid/SendGridEmailSenderOptions.cs
git commit -m "feat: replace SendGrid with MailKit + Zoho SMTP for email sending"
```
