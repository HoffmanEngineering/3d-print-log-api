# Print Log API - Developer Notes

## EF Migrations

```bash
dotnet ef migrations add <MigrationName> --project=PrintLogApi
dotnet ef database update
```

To get user ID in the controllers:

```csharp
var userId = this.User.FindFirst(ClaimTypes.NameIdentifier).Value
```

## Stripe Local Testing

### 1. Configure appsettings.Development.json

```json
"Stripe": {
  "SecretKey": "sk_test_...",
  "WebhookSecret": "whsec_...",
  "ProMonthlyPriceId": "price_...",
  "ProAnnualPriceId": "price_..."
}
```

Get the `SecretKey` and price IDs from [dashboard.stripe.com](https://dashboard.stripe.com) → Developers → API keys / Product catalog.

### 2. Install and Run Stripe CLI

```bash
# Install (Windows via Scoop)
scoop install stripe

# Authenticate
stripe login

# Forward webhooks to local API (run this while testing)
stripe listen --forward-to https://localhost:5001/api/Subscription/webhook
```

The CLI will print a `whsec_...` signing secret — paste that into `WebhookSecret` above.

### 3. Test Cards

| Card Number          | Scenario              |
| -------------------- | --------------------- |
| `4242 4242 4242 4242` | Successful payment   |
| `4000 0000 0000 9995` | Payment declined     |
| `4000 0025 0000 3155` | Requires 3D Secure   |

Use any future expiry date, any 3-digit CVC, any ZIP.

### 4. Webhook Events Handled

| Event                           | Action                              |
| ------------------------------- | ----------------------------------- |
| `checkout.session.completed`    | Create/activate subscription record |
| `customer.subscription.updated` | Update status, plan, period dates   |
| `customer.subscription.deleted` | Mark as canceled                    |
| `invoice.payment_failed`        | Mark as past_due                    |

### 5. Production Go-Live Checklist

- Switch Stripe Dashboard to **live mode** and repeat setup with live keys (`sk_live_...`)
- Add a webhook endpoint in Dashboard → Developers → Webhooks → Add endpoint:
  - URL: `https://3dprintlog.com/api/Subscription/webhook`
  - Events: the 4 listed above
- Copy the live `whsec_...` signing secret into production config (Azure Key Vault)