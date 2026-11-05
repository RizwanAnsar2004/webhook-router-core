# Webhook Router

The router is registered as the **only** webhook URL in the HyperPay account. For each delivery it:

1. **Answers 200 straight away.** The request is checked for a body and the IV and tag headers, then queued as a Hangfire job in Redis.
2. **Decrypts the body in the job.** It uses AES-256-GCM with the account's webhook secret, then reads `payload.customParameters.SHOPPER_service`.
3. **Picks a target.** It looks the identifier up in `Routing:Services`, matching case-insensitively. When nothing matches, or the identifier is missing, it uses `Routing:DefaultUrl`.
4. **Forwards the original delivery.** The encrypted body and headers are POSTed unchanged, so each service still decrypts and verifies it with its own code.

```
HyperPay ──► POST /v1/api/webhooks/hyperpay ──► 200
                         │ (Hangfire job, Redis)
                         ▼
          decrypt → SHOPPER_service = "menna-qa"? ──yes──► menna-qa URL
                                                  └─no──► DefaultUrl
```

## Configuration (`WebhookRouter/appsettings.json`)

| Key | Meaning |
|---|---|
| `ConnectionStrings:Redis` | Redis used by Hangfire |
| `HyperPay:WebhookSecretEnabled` | `true`: a valid secret is required at startup and webhooks are routed by identifier. `false`: the secret is ignored and every webhook goes unread to `DefaultUrl`. |
| `HyperPay:WebhookSecret` | 64-character hex secret from the HyperPay webhook settings (required when enabled) |
| `HyperPay:IdentifierParameter` | Custom parameter that holds the service name (`SHOPPER_service`) |
| `Routing:Services[]` | `{ "Identifier": "menna-qa", "Url": "https://…/v1/Payments/HyperPay/Webhook" }`. Add one entry per service. |
| `Routing:DefaultUrl` | Where everything else goes (required) |
| `Routing:TimeoutSeconds` | Timeout for each forward attempt |
| `Hangfire:RedisPrefix` | `webhookrouter:hangfire:` |
| `Hangfire:RetryAttempts` / `RetryDelaysInSeconds` | Retries when a target fails (non-2xx, timeout or unreachable) |
| `Hangfire:DashboardEnabled` | Serves `/hangfire`. Off by default. |

Retry behaviour:

- **Target failures are retried.** HyperPay always receives 200, so it never redelivers, and Hangfire is the only retry.
- **Unreadable deliveries are logged and dropped, not retried.** That covers a wrong secret, a tampered body and a plaintext that is not JSON.

Every forward is logged with its identifier and target URL.

## Requirements on the services behind it

- **Every target service needs the same `HyperPay:WebhookSecret`.** They all receive the original encrypted body.
- **Each service tags its HyperPay calls.** In roa-core, set `WebhookRouter:ServiceIdentifier`. When it is set, roa-core sends `customParameters[SHOPPER_service]=<value>` with checkouts and refunds:
  - QA: `WebhookRouter__ServiceIdentifier=menna-qa`, set in the service's environment or its server appsettings.
  - Production: leave it empty, so production traffic falls through to `DefaultUrl`.

## Acceptance check

The tests cover the routing logic. The one thing they cannot prove is that HyperPay echoes `customParameters` back in the webhook payload. After deploying:

1. Make one sandbox **payment** from QA.
2. Make one sandbox **refund** from QA.
3. For each, find the line in `journalctl -u webhookrouter` and confirm `identifier menna-qa routing to <QA url> (default: False)`.

If either says `identifier (none)`, HyperPay is not returning the parameter for that event type, and those QA webhooks would fall through to `DefaultUrl`. The identifier would then have to move somewhere else, for example a prefix on `merchantTransactionId`.

## Run locally

```bash
cd WebhookRouter
HyperPay__WebhookSecret=<hex> Routing__DefaultUrl=http://localhost:5000/hook dotnet run
# POST http://localhost:5180/v1/api/webhooks/hyperpay
```

Tests are in `Tests/README.md` (`Tests/.venv/bin/python Tests/api_tests.py`). Deployment is in `deploy/README.md`.
