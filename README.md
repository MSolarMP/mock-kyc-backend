# KYC Verification Platform: backend

The backend for a digital Know-Your-Customer verification platform. It orchestrates document validation, liveness and
face matching through (mocked) external vendors, supports cross-device continuation via QR code, and makes auditable
Approved / Rejected / ManualReview decisions.

- **Architecture document:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) (system context, components, workflow, data model, scaling)
- **Stack:** .NET 10 (LTS) · ASP.NET Core minimal APIs · EF Core + PostgreSQL 17 · S3-compatible object storage · OpenTelemetry

## Quick start

Prerequisites: Docker Desktop. For running from source: the .NET 10 SDK, and Visual Studio 2026 if you use VS (VS 2022 can't build `net10.0`).

```bash
docker compose up --build            # Postgres + S3 + API + worker
```

- Swagger UI: http://localhost:8080/swagger
- Metrics: http://localhost:8080/metrics · Health: http://localhost:8080/health/ready

Then run the end-to-end demo (Windows PowerShell 5.1+ or pwsh):

```powershell
.\scripts\demo.ps1                  # happy path: desktop -> QR -> phone -> uploads -> Approved
.\scripts\demo.ps1 -Scenario all    # every scenario below
```

The demo prints each step, the vendor check results, the decision and the full audit trail.

### Running from Visual Studio / `dotnet run`

```bash
docker compose up -d postgres s3     # infra only
```
Open `Kyc.sln`, set **Kyc.Api** as the startup project and press F5. It runs the API and worker in one process on
http://localhost:8080, applies migrations, and creates the bucket. `requests.http` has a clickable walkthrough.

### Tests

```bash
dotnet test tests/Kyc.UnitTests          # 59 tests: state machine, decision rules, crypto, routing
dotnet test tests/Kyc.IntegrationTests   # 14 tests: real Postgres + S3 via Testcontainers (needs Docker)
```

Integration tests cover the full journey, concurrent QR redemption (exactly one winner), tenant isolation,
idempotency, the manual review flow, durable retries, DB-enforced audit immutability, and checking that no reason
codes leak to customers.

## API overview

| Caller | Auth | Endpoints |
|---|---|---|
| Partner backend | `X-Api-Key: dev-partner-key` | `POST /v1/verification-sessions` (requires `Idempotency-Key`), `GET /v1/verification-sessions/{id}`, `GET …/{id}/events` |
| Customer device | `Authorization: Bearer <clientToken>` | `GET /v1/client/session`, `POST /v1/client/session/handoff`, `POST /v1/client/session/uploads`, `POST …/uploads/{id}/complete` |
| Second device | none (token in body, rate-limited) | `POST /v1/handoff/redeem` |
| Reviewer (partner back office) | `X-Api-Key: dev-partner-key` + `X-Reviewer-Id: <staff id>` | `GET /v1/reviews`, `GET /v1/reviews/{id}`, `POST /v1/reviews/{id}/decision` |

### Credentials for local testing

| Credential | Value | Where it comes from |
|---|---|---|
| Partner API key | `dev-partner-key` | Seeded on startup from `appsettings.json` (`Partners:Seed`, partner "Demo Bank"). Dev only. |
| Reviewer id | any staff id, e.g. `staff-1042` | Not a secret. The partner's back office says which of its staff is acting, and it is written to the audit trail. |
| Bearer token | the `clientToken` from a response | **Not fixed.** Issued per session: by `POST /v1/verification-sessions` (desktop) and by `POST /v1/handoff/redeem` (phone). It only grants access to that one session. |

### Testing in Swagger

Quick run-through (open Swagger and exercise the endpoints in order):

1. Open http://localhost:8080/swagger, click **Authorize**, enter `dev-partner-key` under **ApiKey** and leave the rest empty.
2. `POST /v1/verification-sessions` with header `Idempotency-Key: demo-0001` and body:
   ```json
   { "applicantReference": "customer-42", "channel": "Web",
     "customer": { "ipAddress": "198.51.100.7", "userAgent": "Mozilla/5.0", "deviceFingerprint": "fp-desktop" } }
   ```
3. Copy `sessionId` and `clientToken` from the response.
4. **Authorize** again and paste the `clientToken` under **Bearer** (without the word "Bearer"). The `/v1/client/session/...`
   endpoints now act as the customer's desktop.
5. To act as the phone, call `POST /v1/client/session/handoff`, take the value after `#token=` in `handoffUrl`, send it
   to `POST /v1/handoff/redeem`, and put the new `clientToken` in **Bearer**.
6. Partner views: `GET /v1/verification-sessions/{sessionId}` and `GET /v1/verification-sessions/{sessionId}/events` (audit trail).
7. Review endpoints: also enter a staff id under **ReviewerId**. The queue only lists sessions in `ManualReview`
   (see [Mock scenarios](#mock-scenarios) for how to trigger one).

Image uploads go to pre-signed S3 URLs, which Swagger can't do. For the full journey use `scripts/demo.ps1`, or
`requests.http` in Visual Studio, which chains the ids and tokens for you.

Errors are RFC 7807 `application/problem+json` with a stable `code` (e.g. `invalid_state_transition`,
`handoff_invalid`, `idempotency_key_reused`, `concurrent_modification`, `session_expired`).

**Upload flow:** declare the file (type, content type, size, sha256) → receive a pre-signed PUT URL → upload
directly to object storage → call `complete`, which seals the bytes under a key the client can't overwrite. The
API never streams image bytes. The worker re-hashes the file before each vendor step.

## Mock scenarios

The mock vendors read markers embedded in the uploaded file, the way vendor sandboxes use magic test images. A file
with no marker passes.

| Marker (in file content) | Effect | Demo scenario |
|---|---|---|
| `KYC_MOCK:DOC_TAMPERED` | Document Failed → **Rejected** | `doc-tampered` |
| `KYC_MOCK:DOC_BLURRY` | Document Unreadable → customer asked to recapture | `doc-blurry` |
| `KYC_MOCK:DOC_UNCERTAIN` | Document Inconclusive → **ManualReview** | |
| `KYC_MOCK:DOC_EXPIRED` | Expiry date in the past → **Rejected** | |
| `KYC_MOCK:DOC_NUMBER=XYZ` | Fixes the extracted document number (repeat-attempt detection) | `repeat-after-reject` |
| `KYC_MOCK:LIVENESS_SPOOF` / `_BLURRY` / `_UNCERTAIN` | Failed / Unreadable / Inconclusive liveness | `liveness-spoof` |
| `KYC_MOCK:FACE_MISMATCH` / `FACE_WEAK` | Face score 0.21 (**Rejected**) / 0.72 (**ManualReview**) | `face-weak` |
| `KYC_MOCK:PROVIDER_FLAKY` | Vendor fails the first attempt, then succeeds (durable retry) | `flaky-provider` |
| `KYC_MOCK:PROVIDER_DOWN` | Vendor always fails → retries run out → **ManualReview** (`PROVIDER_UNAVAILABLE`) | `provider-down` |

## Project layout

```
src/Kyc.Domain           Pure domain: aggregate, state machine, decision engine, workflow rules (no I/O)
src/Kyc.Infrastructure   EF Core/Postgres, migrations, S3 storage, provider adapters + mocks, job queue/processor,
                         workflow handlers, outbox relay, maintenance (expiry, retention), crypto
src/Kyc.Api              HTTP endpoints, auth handlers, correlation ids, problem+json, rate limits, OTel, Swagger
tests/                   Unit and integration tests
docs/ARCHITECTURE.md     Architecture document
scripts/demo.ps1         End-to-end scenario runner
```

## Assumptions

- **Backend only.** Front ends (web, mobile, QR rendering) are out of scope. The API returns the QR as a URL and a PNG.
- The **partner backend** (the onboarding system) authenticates the customer and creates the session server-to-server,
  passing the customer's IP, user agent and device fingerprint. It hands the returned `clientToken` to its front end.
- One identity document image (front) per session. Liveness is a single selfie image. Real liveness usually runs
  through a vendor SDK on the device; the adapter contract would then carry the vendor's session/result token
  instead of an image URL.
- Vendors are called synchronously from workers with a timeout. Many vendors offer async webhooks (see "Next steps").
- Session lifetime is 24 hours, handoff tokens last 5 minutes, and there are 3 document and 3 selfie attempts per session.
- "Hundreds per day" today, "tens of thousands" later: sizing is in the architecture doc's scaling section.

## Trade-offs

| Decision | Why | Cost |
|---|---|---|
| **Postgres as the job queue** (`SKIP LOCKED`, leases) instead of a broker | One transactional store means state + audit + job + event commit atomically; one fewer component to run; comfortably handles the target load | Polling latency (~0.5 s), and DB load if volume grows far beyond target. The `IJobHandler` seam allows a later swap to SQS/RabbitMQ |
| **Opaque session tokens** instead of JWTs | Instant revocation, session-bound, no key distribution | A DB lookup per customer request (indexed; cacheable) |
| **Polling** for status instead of push | Simplest, robust through proxies and mobile networks | Extra requests; SSE/WebSocket/push later |
| **Both devices stay active** after handoff | The desktop can keep showing progress; mid-journey device switching is normal | A stolen desktop token still works until expiry (mitigation: short TTL, revoke-on-handoff option) |
| **Fraud signals → ManualReview**, never auto-reject | Keeps false positives with a human; legally safer for adverse decisions | More review volume, monitored via the decisions metric |
| **Single deployable, two roles** | One codebase, shared model, no contract drift between API and worker | Both roles share dependencies and a release cadence |
| **SeaweedFS for local S3** | MinIO stopped publishing free container images (Docker pulls fail); the app only speaks the S3 API | Dev/prod parity is at the API level, not the product level |
| **.NET 10** rather than .NET 8 | .NET 8 reached end of support on 10 Nov 2026. Starting a regulated platform on an unsupported runtime would be a finding in its own right | Needs Visual Studio 2026 |

## Known limitations

- **Auth is a stand-in.** API keys live in config. Production would use OAuth2 client credentials or mTLS at a
  gateway, an SSO-backed reviewer identity with four-eyes approval for high-risk cases, and secrets/keys from a KMS.
- Rate limits are per-instance (in-memory); at scale they belong at the gateway or in a shared store.
- Only mock vendor adapters exist. Multi-vendor routing and fallback per capability is designed for (config per
  capability) but not implemented.
- No webhook delivery to partners: the outbox publisher logs events. A signed-webhook or SNS/Kafka publisher is the next adapter.
- No data-subject erasure endpoint. The approach (crypto-shred the identity field, purge images, keep AML records) is described in the architecture doc.
- The dead-letter queue has no operator UI or replay endpoint; dead-lettered jobs are visible in `workflow_jobs`.
- The selfie is matched against the document image, not against a chip/NFC or government database photo.
- Images are encrypted at rest by the store's own configuration. The local SeaweedFS setup isn't configured for SSE.

## Next steps (in order)

1. Real vendor adapter plus vendor webhooks (async results) with signature verification.
2. A partner webhook publisher (HMAC-signed, retried) behind `IEventPublisher`.
3. A dead-letter operator endpoint (inspect / replay) and alerts on queue lag, circuit-open and DLQ count.
4. Reviewer SSO, case assignment/claiming, four-eyes approval for high-risk outcomes.
5. Monthly partitioning of `audit_events`, plus export to WORM storage / a SIEM.
