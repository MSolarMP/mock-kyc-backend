# CLAUDE.md — KYC Verification Platform (take-home assessment)

Context for Claude Code (and humans) picking this repo up mid-flight. Read this first.

> **Design decisions live in `docs/DECISIONS.md`** (agreed with the user in an 11-step walkthrough on 2026-11-19: options,
> reasoning, what's built vs only designed). Where this file and DECISIONS.md disagree, DECISIONS.md wins. The user is new
> to the KYC domain and wants to understand and defend every decision: explain options and trade-offs, then let them decide.

## What this is
Senior Backend Engineer take-home: design and build the backend for a digital KYC verification platform.
The brief is `Senior_Backend_KYC_Assessment_Detailed.pdf` (repo root). The work should take 3–5 hours, and **design reasoning counts for more than the amount of code**.

Grading weights: System design 25%, Reliability/distributed systems 20%, Workflow design 15%, API design 10%,
Security 10%, Data modelling 10%, Observability 5%, Documentation 5%.

Deliverables: (1) source repo, (2) README (setup, assumptions, trade-offs, known limitations),
(3) `docs/ARCHITECTURE.md` (max 5 pages: system context, components, workflow design, scaling).

## Decisions already made (don't re-open these without a reason)
- **Backend only.** No frontend. The customer journey is shown with Swagger, `scripts/demo.ps1`, and `requests.http`.
- **.NET 10 (LTS)**, not .NET 8: .NET 8 went out of support on 2026-11-10. **Needs Visual Studio 2026 (v18+)**; VS 2022 can't build net10.0.
- **Postgres** for everything durable. **SeaweedFS** (S3-compatible) for images locally. MinIO was dropped because it no longer
  publishes free Docker images (pulls fail). The app only speaks the S3 API (AWSSDK.S3), so prod = AWS S3. **No separate message broker** for now:
  the workflow queue is a Postgres `workflow_jobs` table claimed with `FOR UPDATE SKIP LOCKED` (leases + retries + dead letter).
  The job table doubles as the transactional outbox. The architecture doc explains the move to a broker (outbox -> RabbitMQ/SQS
  for outward events: "a queue for commands, a broker for events") as volume grows.
- **Uploads use pre-signed URLs**: the client PUTs straight to object storage and the API never streams image bytes.
  Flow: `POST /v1/client/session/artifacts` (declare type/contentType/size/sha256) → PUT to `uploadUrl` →
  `POST .../artifacts/{id}/complete` (API checks size with HEAD; worker re-hashes before vendors see it).
- **QR handoff**: desktop calls `POST /v1/client/session/handoff`, which returns a single-use token with a 5-minute TTL, carried in the URL **fragment** (`#token=`) so it isn't sent to servers or logs. The front end renders the URL as a QR code (no server-side PNG).
  Mobile calls `POST /v1/handoff/redeem`, which consumes the token atomically and issues a **new** session access token for that device. Both devices stay valid.
- **Multi-tenant**: each bank is a row in `partners` (keys hashed in `partner_api_keys`, several per partner for rotation,
  per-partner `image_retention_days`). Dev/test partners are seeded from config `Partners:Seed` (`dev-partner-key`).
- **Auth**: partners use `X-Api-Key`. **Reviewers act through their partner's back office**: partner key + `X-Reviewer-Id`
  (a staff id, not an email); audited as `reviewer:<id> via partner:<id>`; partners only see/review their own sessions (404 otherwise).
  Customers use an opaque Bearer session token (hash stored, revocable). The session id is **derived from the token**, so customer
  routes have no `{id}` (no IDOR).
- **Vendor A (mocked) behind an adapter**: `Providers/VendorA/` has `SandboxVendorAClient` (answers in Vendor A's own format:
  `clear/consider/unidentified/rejected`, scores 0-100, its sub-check names, HTTP errors) and `VendorAAdapter`, which maps that onto
  our contracts (`IDocumentVerificationProvider`, `ILivenessProvider`, `IFaceMatchProvider`). Decision thresholds live in
  `Providers:VendorA:Thresholds` (per vendor). Replacing the vendor = new adapter folder + config. Sandbox scenarios are ASCII markers in
  the uploaded file, e.g. `KYC_MOCK:DOC_TAMPERED`, `FACE_WEAK`, `PROVIDER_FLAKY` (504 once), `PROVIDER_DOWN` (503),
  `PROVIDER_MISCONFIGURED` (401, permanent). No marker means a pass.
- **Vendor failures**: 408/429/5xx -> `TransientProviderException` -> durable job retry with backoff; other 4xx ->
  `PermanentProviderException` -> dead-lettered immediately. Polly adds a timeout + circuit breaker per capability.
- **Outcomes**: Approved / Rejected / ManualReview. Reviewers decide through `POST /v1/reviews/{sessionId}/decision`.
  Provider outage after all retries goes to **ManualReview** (never auto-reject because a vendor is down).
- **Audit**: domain events on the session aggregate become `audit_events` rows in the **same transaction** as the state change
  (done in `KycDbContext.SaveChangesAsync`). A DB trigger makes `audit_events` append-only. Partners read it at `GET /v1/verification-sessions/{id}/events`.
- **PII**: the document number is stored only as an HMAC (`document_number_hash`, for repeat-attempt detection); extracted identity is
  AES-GCM encrypted (`extracted_identity_encrypted`). No PII in logs or audit payloads (IP addresses are a stated exception, kept for
  fraud). The maintenance job purges images `partners.image_retention_days` after a decision; the bank holds the FICA record.
- **Designed, not built** (documented only): webhooks (polling is the built mechanism), tracing wiring, broker, Temporal, OAuth2/mTLS/OIDC,
  hash-chained audit, malware scanning, legal hold, erasure API, load test. See DECISIONS.md step 11.
- One deployable, two roles: env `Kyc__Roles` = `api`, `worker`, or `api,worker` (default). docker-compose runs them as separate containers.

## Solution layout
```
src/Kyc.Domain          Pure domain: entities, SessionStateMachine, DecisionEngine, WorkflowRules (no infra deps)
src/Kyc.Infrastructure  EF Core (Postgres, snake_case), migrations, partner seeder, S3 storage, Vendor A adapter + sandbox,
                        Polly resilience, job queue/processor + handlers, maintenance (expiry/retention), crypto helpers
src/Kyc.Api             ASP.NET Core minimal APIs, auth handlers, endpoints, correlation id, problem+json, metrics, Swagger
tests/Kyc.UnitTests     Domain rules, decision engine, crypto, Vendor A adapter mapping + sandbox markers
tests/Kyc.IntegrationTests  WebApplicationFactory + Testcontainers (Postgres, SeaweedFS), full journeys (needs Docker running)
docs/ARCHITECTURE.md    The ≤5-page architecture doc
docs/DECISIONS.md       Decision log from the design walkthrough (source of truth for the why)
scripts/demo.ps1        End-to-end demo: desktop → QR handoff → mobile uploads → decision
```

## Conventions
- Status changes only through `VerificationSession.TransitionTo` (enforces the state machine, emits the audit event).
- Job handlers must be **idempotent**: check the session status first and do nothing if the work is already done. The handler's changes and
  `job.Status = Completed` are saved in **one** `SaveChanges`.
- Enums are stored as strings. Tables and columns are snake_case (EFCore.NamingConventions). Ids are UUIDv7 (`Guid.CreateVersion7`).
- Errors are RFC 7807 problem+json with a stable `code` extension (from `DomainException.Code`).
- Never log tokens, document numbers, names, or image URLs.

## Common commands
```bash
docker compose up -d postgres s3             # infra only (then run the API from VS / dotnet run)
docker compose up --build                    # everything: api + worker + infra
dotnet build
dotnet test tests/Kyc.UnitTests
dotnet test tests/Kyc.IntegrationTests       # needs Docker Desktop running
dotnet tool restore && dotnet ef migrations add <Name> -p src/Kyc.Infrastructure -s src/Kyc.Api -o Persistence/Migrations
pwsh scripts/demo.ps1                        # end-to-end demo against http://localhost:8080
```
Visual Studio: open `Kyc.sln`, set `Kyc.Api` as the startup project, then use the `Kyc.Api` launch profile (Swagger at `/swagger`).
Start the infra containers first (`docker compose up -d postgres s3`).

## Gotchas already hit (don't reintroduce)
- **Guid keys must be `ValueGenerated.Never`** (set for all entities in `KycDbContext.OnModelCreating`). Otherwise EF treats a
  new child added through a navigation (`session.Artifacts.Add`) as an existing row, UPDATEs it, and throws a concurrency exception.
- **No `EnableRetryOnFailure`** on Npgsql: it forbids explicit transactions; transient DB errors already surface as job retries or a retryable 503.
- **Authentication runs before rate limiting.** A default policy scheme picks ApiKey or Bearer from the headers, so rate-limit
  partitions see the session/partner id. The other order silently falls back to per-IP buckets.
- The Vendor A sandbox returns extracted identity even for `DOC_TAMPERED`; otherwise repeat attempts after a rejection can't be linked.
- The demo uses a random customer IP per scenario; a fixed IP (correctly) trips the `HIGH_IP_VELOCITY` rule after 10 sessions.
- Regenerating the initial migration drops the hand-written append-only trigger SQL on `audit_events`. Re-add it (see the end of `Up`).
- Only the api container seeds partners (`Partners__SeedOnStartup: "false"` on the worker), to avoid a first-start race on the PK.
- `Microsoft.EntityFrameworkCore.Relational` is pinned in Infrastructure to match the Design package; without it the unit tests
  see two EF versions (MSB3277).
- In this environment, bash heredocs containing apostrophes break the Bash tool; write scripts to a file instead.

## Progress
- [x] Original full build (kept on branch `full-implementation`), then the design walkthrough (docs/DECISIONS.md)
- [x] Build phase: partners tables, reviewer identity, Vendor A adapter, permanent vendor errors, per-partner retention;
      removed outbox, QR PNG, tracing wiring; migration regenerated (+ trigger)
- [x] Cleanup: stale comments, unused audit type, EF version warning (0 warnings), `dotnet format`, `.gitattributes`
- [x] Unit tests 59 passing, integration tests 13 passing, demo 9/9 scenarios passing (compose stack)
- [ ] Docs: rewrite docs/ARCHITECTURE.md (max 5 pages) from DECISIONS.md; README (setup, assumptions, trade-offs, limitations,
      "How to replace Vendor A", reviewer id guidance, AI-disclosure placeholder); tidy DECISIONS.md for submission
