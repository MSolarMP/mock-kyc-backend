# CLAUDE.md — KYC Verification Platform (take-home assessment)

Context for Claude Code (and humans) picking this repo up mid-flight. Read this first.

> **2026-11-19: design walkthrough in progress.** The repo was too code-heavy for a 3–5h assessment. We are going through
> the design step by step with the user and recording each decision in **`docs/DECISIONS.md`** (status table, reasoning,
> change checklist). **Where this file and DECISIONS.md conflict, DECISIONS.md wins** (e.g. outbox removed, partners table,
> reviewer via partner key + reviewerId, QR PNG dropped, mocks restructured as "Vendor A"). Don't trim or rewrite code until
> step 11 (keep or cut) is agreed. Commit the current full version to a branch before trimming.

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
  The architecture doc explains the move to SQS/RabbitMQ/Kafka as volume grows.
- **Uploads use pre-signed URLs**: the client PUTs straight to object storage and the API never streams image bytes.
  Flow: `POST /v1/client/session/artifacts` (declare type/contentType/size/sha256) → PUT to `uploadUrl` →
  `POST .../artifacts/{id}/complete` (API checks size with HEAD; worker re-hashes before vendors see it).
- **QR handoff**: desktop calls `POST /v1/client/session/handoff`, which returns a single-use token with a 5-minute TTL, carried in the URL **fragment** (`#token=`) so it isn't sent to servers or logs, plus a QR PNG (base64).
  Mobile calls `POST /v1/handoff/redeem`, which consumes the token atomically and issues a **new** session access token for that device. Both devices stay valid.
- **Auth**: partner/back-office systems use `X-Api-Key` (roles `partner`, `reviewer`). Customers use an opaque Bearer session token
  (hash stored, revocable). The session id is **derived from the token**, so customer routes have no `{id}` (no IDOR).
- **Vendors are mocked** behind `IDocumentVerificationProvider`, `ILivenessProvider`, `IFaceMatchProvider`. Scenarios are driven by ASCII
  markers inside the uploaded file (like vendor sandbox test images), e.g. `KYC_MOCK:DOC_TAMPERED`, `KYC_MOCK:FACE_WEAK`,
  `KYC_MOCK:PROVIDER_FLAKY`, `KYC_MOCK:PROVIDER_DOWN`. No marker means a pass.
- **Outcomes**: Approved / Rejected / ManualReview. Reviewers decide through `POST /v1/reviews/{sessionId}/decision`.
  Provider outage after all retries goes to **ManualReview** (never auto-reject because a vendor is down).
- **Audit**: domain events on the session aggregate become `audit_events` rows in the **same transaction** as the state change
  (done in `KycDbContext.SaveChangesAsync`). A DB trigger makes `audit_events` append-only. Events flagged `publish` also go to `outbox_messages`.
- **PII**: the document number is stored only as an HMAC (`document_number_hash`, for repeat-attempt detection); extracted identity is
  AES-GCM encrypted (`extracted_identity_encrypted`). No PII in logs or audit payloads. A retention job purges images N days after a decision.
- One deployable, two roles: env `Kyc__Roles` = `api`, `worker`, or `api,worker` (default). docker-compose runs them as separate containers.

## Solution layout
```
src/Kyc.Domain          Pure domain: entities, SessionStateMachine, DecisionEngine, WorkflowRules (no infra deps)
src/Kyc.Infrastructure  EF Core (Postgres, snake_case), migrations, S3 storage, mock providers, job queue,
                        workflow job handlers, outbox publisher, maintenance (expiry/retention), crypto helpers
src/Kyc.Api             ASP.NET Core minimal APIs, auth handlers, endpoints, correlation id, problem+json, OTel, Swagger
tests/Kyc.UnitTests     Domain rules, decision engine, crypto, mock provider marker parsing
tests/Kyc.IntegrationTests  WebApplicationFactory + Testcontainers (Postgres, SeaweedFS), full journeys (needs Docker running)
docs/ARCHITECTURE.md    The ≤5-page architecture doc
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
- **No `EnableRetryOnFailure`** on Npgsql: it forbids the explicit transaction the outbox relay uses.
- **Authentication runs before rate limiting.** A default policy scheme picks ApiKey or Bearer from the headers, so rate-limit
  partitions see the session/client id. The other order silently falls back to per-IP buckets.
- The mock document vendor returns extracted identity even for `DOC_TAMPERED`; otherwise repeat attempts after a rejection can't be linked.
- The demo uses a random customer IP per scenario; a fixed IP (correctly) trips the `HIGH_IP_VELOCITY` rule after 10 sessions.
- Regenerating the initial migration drops the hand-written append-only trigger SQL on `audit_events`. Re-add it (see the end of `Up`).

## Progress
- [x] Solution scaffold, net10.0, pinned packages, dotnet-ef local tool (`dotnet-tools.json`)
- [x] Domain: enums, state machine, aggregate + entities, decision engine, workflow rules
- [x] Infrastructure: DbContext, crypto, S3 storage, mock providers + Polly, job queue/processor, 4 handlers, outbox, maintenance
- [x] EF migration `InitialSchema` (+ append-only trigger on audit_events)
- [x] API: auth handlers, partner/client/handoff/review endpoints, middleware, OTel/Prometheus, Swagger, rate limiting
- [x] docker-compose (postgres, s3, api, worker) + Dockerfile; verified with `--scale worker=2`
- [x] Unit tests: 47 passing
- [x] Integration tests: 12 passing (Testcontainers)
- [x] scripts/demo.ps1: all 8 scenarios pass against the compose stack
- [x] requests.http, README.md, docs/ARCHITECTURE.md
- [ ] Optional polish: tiny HTML page to show the QR visually; partner webhook publisher; DLQ replay endpoint
- [x] Initial commit on `main`; full original version kept on branch `full-implementation`
- [x] Build phase of the walkthrough (2026-11-19): outbox removed, QR PNG removed, tracing wiring removed, `partners` +
      `partner_api_keys` tables (replace `ApiClients` config; dev seed via `Partners:Seed`), reviewer = partner key +
      `X-Reviewer-Id`, mocks → `Providers/VendorA/` (sandbox client + adapter + per-vendor thresholds), permanent
      vendor errors dead-letter immediately, per-partner image retention, migration regenerated (+ trigger).
      Unit 59 ✓, integration 13 ✓, demo 9/9 ✓.
- [ ] Next: cleanup + docs (rewrite ARCHITECTURE.md from docs/DECISIONS.md, README updates incl. "How to replace Vendor A")
