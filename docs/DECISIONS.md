# Design Decision Log

This file records the design walkthrough of the KYC platform: for each decision, the requirement behind it, the options we looked at, what we chose and why, and what it means for the code and docs.

**Why this exists:** the brief says *"This assessment is intended to take approximately 3–5 hours. We are evaluating design decisions and technical reasoning rather than volume of code."* The first version of the repo was far too big for that budget (~3,800 lines of production code, plus a long list of extra features). This walkthrough decides what to **build** and what to **only design and document**.

The brief also allows: *"A working implementation or a detailed architecture with partial implementation is acceptable."*

Grading weights, for reference: System design 25%, Reliability/distributed systems 20%, Workflow 15%, API 10%, Security 10%, Data modelling 10%, Observability 5%, Documentation 5%.

| Step | Topic | Status |
|---|---|---|
| 1 | Product and actors | Agreed |
| 2 | System shape and queue | Agreed |
| 3 | Workflow orchestration | Agreed |
| 4 | Vendor failures and replaceability | Agreed |
| 5 | Data model and audit trail | Agreed |
| 6 | Authentication and QR handoff | Agreed |
| 7 | Document and selfie uploads | Agreed |
| 8 | PII and compliance | Agreed |
| 9 | Scaling | Agreed |
| 10 | API design and observability | Agreed |
| 11 | Keep or cut (final build list) | Agreed |

---

## KYC background (short version)

- **KYC (Know Your Customer):** the legal requirement for a bank or fintech to verify a customer's identity before opening an account. South Africa: **FICA**. Personal data handling: **POPIA** (GDPR in Europe).
- A digital check has three parts, each done by a specialist **vendor** (Onfido, Sumsub, Smile ID, Jumio, …):
  1. **Document check:** is the ID genuine and unaltered? The vendor also extracts name, date of birth and ID number.
  2. **Liveness:** is the selfie of a live person (not a photo of a photo, a screen replay or a mask)?
  3. **Face match:** does the selfie match the photo on the document?
- **Our platform does none of these checks itself.** It orchestrates: it tracks where each customer is, calls the vendors in order, survives their failures, combines results into a decision, and records everything for auditors.
- **Why manual review exists:** vendors return *scores*, not yes/no. High score: approve. Low score: reject. In between, or a vendor outage: a human decides. So there are three outcomes: **Approved / Rejected / ManualReview**.

---

## Step 1: Product and actors

**Requirement:** "Design the backend services…"; customers start on web or mobile; "remain independent of any single KYC vendor"; "determine… whether manual intervention took place."

### Decision 1.1: The platform is a backend that partner (bank) systems call
- **Options:** (A) a backend that the bank's systems call; (B) a standalone customer app with its own UI.
- **Chosen: A.** The brief says *backend* and grades backend topics only. It's how real KYC vendors work: the bank creates a session server-side, the customer completes it, the bank gets the result. No frontend to build.
- **Multi-tenant:** each bank is a *partner*. Every session carries a `partner_id`; each partner has its own API key and webhook URL and can only see its own sessions. Adding a bank is a database row, not a code change.

### Decision 1.2: Full audit trail and a reviewer API
- The brief wants a full trail: when it started, what happened, the status before and after, who did it, where it came from, who reviewed and who approved.
- **Reviewers are the bank's compliance staff, not the vendor.** Vendors return scores; *our rules* decide Approved / Rejected / ManualReview; humans at the bank decide the ManualReview cases. The bank remains legally responsible under FICA.
- **Chosen:** build a minimal reviewer API (list cases, post a decision). A reviewer UI is out of scope.

### Decision 1.3: The flow is asynchronous; results by webhook, with polling as the fallback
- **The bank does not wait on one HTTP call.** The process takes minutes (customer captures photos), then seconds (vendor calls), then possibly hours (manual review). Every call returns quickly with an id, and the result comes later.
- **Options:** polling only; webhooks only; both.
- **Chosen:** **webhook (built, minimal) with a polling GET as the fallback.** The webhook is a job on our existing queue: retried with backoff, signed (HMAC) so the bank can verify it came from us, and carrying an event id so the bank can ignore duplicates. Polling covers the case where the bank's server was down.
- **Revised in step 11:** webhooks are **designed only** (contract in 10.6); polling is the built mechanism. This keeps the codebase from growing.

**Changes:** add `partners` table; reviewer API stays. (Webhook delivery job dropped in step 11: design only.)

---

## Step 2: System shape and queue

**Requirement:** growth from hundreds to tens of thousands of verifications a day, seasonal spikes; "evolve without requiring a complete redesign"; "technology choices should be justified."

### Back-of-envelope sizing
50,000 verifications a day, with 20% landing in one peak hour, comes to about 10,000 sessions an hour: **~3 new sessions a second, ~30 API requests a second.** One small service and one Postgres database handle that easily. The hard part isn't request volume. It's that **vendor calls are slow and unreliable**, and a spike creates a backlog of vendor work. So the design separates *accepting requests* from *doing slow work*.

### Decision 2.1: Modular monolith, one image, two roles (API + Worker)
- **Options:**
  - (A) **Microservices** + Kafka: six deployables, distributed transactions; overkill for ~30 requests a second, and it breaks the "audit in the same transaction" guarantee.
  - (B) **Simple monolith** that calls vendors inside the HTTP request: slow requests, vendor errors surface to customers, spikes exhaust threads, crashes lose work.
  - (C) **Modular monolith with API and Worker roles joined by a queue.**
- **Chosen: C.** The API validates, saves state, enqueues a job and returns immediately. Workers take jobs and call vendors. Same codebase, started with `Kyc__Roles=api` or `worker`.
  - API is stateless, so any number of instances can run behind a load balancer.
  - Spikes make the queue longer instead of taking servers down; add workers to drain it.
  - Clean module boundaries (Domain / Infrastructure / Api) mean a part can be extracted later if it earns it.

### Decision 2.2: Queue = a Postgres table (option 1)
- **Options:**
  1. **Postgres job table** (`workflow_jobs`, claimed with `SELECT … FOR UPDATE SKIP LOCKED`).
  2. **Message broker** (RabbitMQ/SQS/Kafka) for everything, with an outbox.
  3. **Hybrid:** Postgres for internal workflow jobs, outbox → RabbitMQ for outward events.
- **Chosen: 1.**
  - Status change, audit row and the next job are written in **one transaction**, so all happen or none do.
  - No extra infrastructure; easily handles hundreds of jobs a second.
- **Why not a broker now:**
  - A broker needs the **outbox pattern** to avoid the dual-write problem (save to DB, crash, never publish). The outbox *is* a Postgres table polled by a relay, so you'd run both systems anyway.
  - Brokers deliver at least once, so consumers must be idempotent either way.
  - More code than the time budget allows.
- **What fan-out means:** one event delivered to several independent consumers (e.g. `SessionApproved` → webhook sender, fraud analytics, reporting, email). This is a broker's real strength, and the reason the **hybrid (option 3)** is the documented growth path: *"a queue for commands, a broker for events."*
- The queue sits behind `IJobQueue`, so moving to SQS/RabbitMQ later is an infrastructure change, not a redesign.

### Growth path (for ARCHITECTURE.md)
| Stage | Volume | What changes |
|---|---|---|
| Today | Hundreds a day | 1 API + 1 worker, Postgres, S3 |
| Growth | Tens of thousands a day, spikes | N API instances behind a load balancer; workers autoscale on queue depth; per-vendor rate limits; read replica for reporting |
| Scale | Hundreds of thousands+ | Hybrid broker (outbox → RabbitMQ/SQS) for events; partition audit table by month; split out webhook delivery or reviewer back office only if load or team structure justifies it |

**Changes:** none to the shape. Document the broker/hybrid path and fan-out in ARCHITECTURE.md.

---

## Step 3: Workflow orchestration

**Requirement:** Session Creation → Document Upload → Document Validation → Selfie Upload → Liveness → Face Match → Risk Assessment → Final Decision; "session state must be maintained"; "start on desktop, continue on mobile… without losing progress"; the platform "orchestrates the workflow."

### Decision 3.1: A state machine is the only way to change status
A fixed set of statuses and allowed moves (`src/Kyc.Domain/SessionStateMachine.cs`). Benefits: no illegal jumps (bug or attacker), every move is an audit event, and state lives in the DB so any device can continue.

```
 Created ──upload doc──► DocumentSubmitted ──doc OK──► AwaitingSelfie ──upload selfie──► SelfieSubmitted
    ▲                        │                             ▲                                │
    └──── blurry: retake ────┘                             └────── blurry: retake ──────────┘
                             │ doc failed                                                   │ checks done
                             ▼                                                              ▼
                       PendingDecision ◄────────────────────────────────────────────────────┘
          ┌──────────────────┼──────────────────┐
          ▼                  ▼                  ▼
      Approved           Rejected         ManualReview ──reviewer──► Approved / Rejected
 Created / AwaitingSelfie ──timeout──► Expired
```

### Decision 3.2: Orchestration, not choreography
- **Orchestration:** one coordinator decides the next step (a conductor). **Choreography:** each step reacts to the previous step's events (dancers following each other).
- **Chosen: orchestration.** The whole flow is visible in one place, and audit and debugging are easy. Regulated processes need one place that answers "what happened and why." The brief uses the word "orchestrates." Choreography suits outward events (webhooks, analytics).

### Decision 3.3: Our own orchestrator, not a workflow engine
- **Temporal** (open source; workflows written as normal code that survive crashes by replaying recorded step results) and **AWS Step Functions** (managed; workflows defined as JSON/diagrams) give retries, timers and history out of the box.
- **Chosen:** our own (state machine + job queue). Only four automated steps; an engine is a big new dependency; and graders want to see *us* design retries and idempotency.
- **Documented upgrade path:** move to Temporal (or Step Functions if standardised on AWS) when the workflow grows (per-country steps, sanctions screening, address checks, long waits).

### Decision 3.4: Smaller workflow rules
- **Document first, then selfie.** Face match needs the document photo; no point asking for a selfie if the ID is fake.
- **Fail fast:** a definitive document failure skips the remaining checks and goes to the decision. Saves vendor fees and customer time. (Collecting full evidence for fraud investigation could be a future setting.)
- **Retakes only for customer-fixable problems** (blur, glare, cropping), with an attempt limit. Fake, spoofed or a different person is final. Stops fraudsters retrying until something slips through.
- **Only customer-waiting statuses expire** (`Created`, `AwaitingSelfie`). If *we* are slow, the customer never loses progress.
- **Idempotent handlers:** a job can run twice after a crash; each handler checks "already done?" first.

**Changes:** none; this matches the current code.

---

## Step 4: Vendor failures and replaceability

**Requirement:** "remain independent of any single KYC vendor"; Reliability is 20% of the grade.

### Decision 4.1: Classify vendor responses before reacting
| What happened | Type | Action |
|---|---|---|
| Vendor says "fake ID" | Successful call, negative result | Record it, go to decision. **Never retry.** |
| Vendor says "too blurry" | Successful call, customer can fix it | Ask the customer to retake |
| Timeout, 5xx, connection refused, 429 | Transient failure | Retry later |
| 400 / 401 | Permanent failure (our bug or config) | Don't retry; dead letter and alert |

### Decision 4.2: Layers of defence
1. **Timeout on every vendor call** (e.g. 15s). Stops a hung vendor tying up all workers.
2. **Durable job-level retries with exponential backoff and jitter.** A failed job goes back into the table with `run_after = now + delay` (2s, 4s, 8s… plus randomness so retries don't all fire at once). It survives crashes and holds no threads. Retries are *not* done in memory.
3. **Circuit breaker per vendor** (Polly). If a vendor is failing heavily, stop calling it for a short time, then test with one call. Protects the vendor and makes our jobs fail fast.
4. **Give up gracefully:** after N attempts the job is **DeadLettered** (kept for inspection or replay) and the session goes to **ManualReview** with `PROVIDER_UNAVAILABLE`. **Never auto-reject because a vendor was down.**
- Polly stays (an earlier suggestion was to cut it): ~70 lines, directly earns Reliability marks.

### Decision 4.3: Idempotency with vendors
If a worker crashes after the vendor answered but before saving, the job reruns and the vendor is called twice (charged twice). Mitigations: handlers check for an existing result first; requests carry an **idempotency key** (already in `ProviderRequest`) that real vendors use to return the cached answer. Remaining risk (a rare double call) is accepted and documented.

### Decision 4.4: Vendors are replaceable through adapters (no runtime failover)
- The requirement is **replaceability**: we use Vendor A (mocked) today; replacing it with Vendor B later must be easy. **No automatic switching between vendors at runtime** (one line in the doc as a possible future feature).
- **Adapter pattern (ports and adapters):** our code speaks our own contract (`CheckOutcome`, score 0–1, our reason codes, vendor reference). Each vendor gets an adapter that translates its API into that contract. Nothing outside the adapter sees vendor field names or score scales.
- **Swapping vendors = write a new adapter + change config** (`Providers:Document`, API key/URL, **that vendor's thresholds**). Workflow, DB schema, decision logic, partner API and audit format don't change.
- Thresholds are per vendor and live in config (one vendor's 0.8 is not another's 0.8).
- Every check records the vendor name and reference, so old decisions stay explainable after a swap.
- Caveat for the doc: some vendors require their own camera SDK in the customer's app (especially liveness), so a swap might also touch the bank's frontend.

### Decision 4.5: Model "Vendor A" realistically (option B)
- The current mock implements our interface directly and returns our types, so the translation layer is only described in a comment, not visible.
- **Chosen:** restructure into `Providers/VendorA/`:
  - `VendorAClient`: fake Vendor A API returning *its own* format (e.g. `{"result":"consider","score":72,"breakdown":["image_quality"]}`); scenarios still driven by `KYC_MOCK:*` markers.
  - `VendorAAdapter`: maps that to our contract (`clear` → Passed, `consider` → Inconclusive, 72 → 0.72, `image_quality` → our reason code).
  - `VendorAOptions`: base URL, API key, thresholds.
- Replaces the current mock (small net change). README gets a "How to replace Vendor A" section.

**Changes:** restructure mocks into `VendorA/`; move thresholds into per-vendor config; README section.

---

## Step 5: Data model and audit trail

**Requirement:** every significant event traceable; future fraud detection, investigation and reporting possible; POPIA/GDPR.

### Decision 5.1: Postgres (relational), not NoSQL
- The critical write touches several tables at once (session status, provider check, audit row, job completion) and must be **one ACID transaction**. Native in Postgres. (MongoDB and DynamoDB do support transactions now, but as the exception path with limits.)
- The **queue lives in the same database**, which is what makes decision 2.2 work. A NoSQL store would need a separate queue and bring back dual writes.
- The data is relational (partner → sessions → artifacts/checks/decisions/audit); foreign keys and unique constraints let the DB enforce rules (e.g. `UNIQUE (partner_id, idempotency_key)`).
- **Investigations are ad-hoc SQL** ("has this ID number been rejected anywhere in the last 90 days?"). DynamoDB needs access patterns designed in advance, and fraud questions are exactly the ones you can't predict.
- NoSQL wins at millions of simple key-value writes a second. We're at ~30 requests a second.

### Decision 5.2: Current state + audit log, not event sourcing
- **Ours:** the `verification_sessions` row is the truth for *now*; `audit_events` is the history.
- **Event sourcing:** only events are stored; current state is computed by replaying them.
- **Why not event sourcing:**
  1. Everyday queries ("all ManualReview sessions for Bank A") need projections, which end up being our sessions table plus sync code.
  2. Event formats are forever: versioning and upcasters.
  3. **Conflicts with the right to erasure** (POPIA/GDPR): events are immutable; workarounds (crypto-shredding) add complexity. Our audit log contains no personal data, so personal data in normal columns can be purged and the log stays intact.
  4. The brief needs traceability, not replay.
  5. Roughly double the persistence code.
- Event sourcing fits when history *is* the business (ledgers, accounting).
- **Honest weakness:** state and log are two writes that could in theory drift. Prevented by one code path (`TransitionTo`), the same transaction, and an append-only trigger.

### Decision 5.3: Tables
| Table | Purpose / key point |
|---|---|
| `partners` | **New.** One row per bank: API key hash(es), webhook URL, webhook secret. Onboarding a bank needs no deploy. |
| `verification_sessions` | Current state. `UNIQUE (partner_id, idempotency_key)` prevents duplicate sessions on retried POSTs. |
| `artifacts` | Image metadata only (type, size, SHA-256, S3 key). Bytes live in S3. |
| `provider_checks` | Each vendor result: vendor, outcome, score, reasons, vendor reference. |
| `decisions` | Every decision, not just the last (automated ManualReview, then reviewer Approved), with rules version and reviewer. |
| `audit_events` | The append-only trail. |
| `workflow_jobs` | The queue; webhook deliveries become jobs here too. |
| `session_access_tokens`, `handoff_tokens` | Step 6. |

### Decision 5.4: The audit trail
- **Where:** only the `audit_events` table in Postgres. It's separate from application logs and traces (those are operational, best-effort, short retention); they're linked by `correlation_id`.
- **How it's written:** `VerificationSession.TransitionTo(...)` / `Record(...)` add pending events in memory; `KycDbContext.SaveChangesAsync` converts them into `audit_events` rows, filling in actor, IP and correlation id centrally, in the **same transaction**. Developers can't forget to audit.
- **Append-only, enforced by the DB:** a trigger blocks `UPDATE`/`DELETE`/`TRUNCATE` (kept; an earlier suggestion was to cut it). In production the app's DB user gets only `INSERT, SELECT`.
- **Columns:** `id, session_id, event_type, actor_type, actor_id, correlation_id, ip_address, data (JSON), occurred_at`.
- **No personal data** in audit rows: ids, codes and statuses only. Reviewer notes live in `decisions`.
- **Device details:** a customer's `actor_id` is their token id; the token row holds channel, user agent and whether it came via handoff.
- Reviewer **views** of images are audited too (`artifact.viewed_by_reviewer`).
- **Documented, not built:** a hash chain (each row stores the previous row's hash) for tamper evidence.

Example: one session that went to manual review:

| # | time | event_type | actor | key data |
|---|---|---|---|---|
| 1 | 10:40:01 | `session.created` | Partner `bank-a` | applicantRef, channel `Web` |
| 3 | 10:40:45 | `handoff.issued` | Customer (desktop token) | expires in 5 min |
| 4 | 10:41:30 | `handoff.redeemed` | Customer (new mobile token) | phone IP |
| 6 | 10:42:05 | `artifact.uploaded` | Customer (mobile) | IdentityDocument, sha256 |
| 8 | 10:42:13 | `check.completed` | System | DocumentValidation, vendor-a, Passed 0.93, ref |
| 13 | 10:43:19 | `check.completed` | System | FaceMatch, vendor-a, Inconclusive 0.71, `FACE_MATCH_WEAK` |
| 16 | 10:43:20 | `decision.made` | System | ManualReview |
| 18 | 13:58:02 | `artifact.viewed_by_reviewer` | Reviewer `jane.d@bank-a` | viewed images |
| 19 | 14:05:31 | `decision.made` | Reviewer `jane.d@bank-a` | Approved, `IDENTITY_CONFIRMED_MANUALLY` |

(Status-change rows between these are omitted here.)

### Decision 5.5: Drop `outbox_messages`
A webhook is a `workflow_jobs` row written in the same transaction as the decision, so the job table already acts as the outbox. A dedicated outbox and relay are only needed once a broker is added.

### Decision 5.6: Keep the fraud indexes
`document_number_hash` (repeat attempts after rejection), `artifacts.sha256` (same image reused across sessions), `(created_ip, created_at)` (automated abuse). Cheap now; this is what makes "future detection possible".

**Changes:** add `partners`; remove `outbox_messages` + `OutboxPublisher` + the outbox branch in `SaveChangesAsync`; keep trigger and indexes; possibly add `GET /v1/sessions/{id}/audit` for partners (confirm at step 11).

---

## Step 6: Authentication and QR handoff

**Requirement:** "seamless" and "secure" mobile handoff via QR; fraud via "automated abuse" and "impersonation"; Security is 10%.

Three caller types, three credentials: **partner** (server) → API key; **reviewer** (named person) → see 6.2; **customer** (anonymous, no account with us) → short-lived session token.

### Decision 6.1: Partners use hashed API keys
- Options: API key; OAuth2 client credentials; mTLS.
- **Chosen:** API key in `X-Api-Key`, stored only as a hash in `partners`, more than one per partner so keys can be rotated without downtime. Documented: OAuth2 client credentials or mTLS for production, depending on the partner.

### Decision 6.2: Reviewers act through the partner's back office (fixes a gap)
- **Gap in the current code:** reviewers use a shared API key with a `reviewer` role, so decisions are recorded against *a key*, not *a person*.
- Options: (A) the bank's back-office tool (which already logs in its staff) calls us with its partner key and a **required `reviewerId`**; (B) reviewers log in to us directly (own accounts or OIDC with every bank).
- **Chosen: A.** Matches our framing (a backend for bank systems). Recorded as `reviewer:jane.d@bank-a via partner:bank-a`. A partner can only review its own sessions. Documented: OIDC/SSO tokens carrying reviewer identity for production.

### Decision 6.3: Customers use opaque, hashed, short-lived tokens
- Flow: partner creates a session and gets back `customerToken`, then passes it to its own web page; the browser sends `Authorization: Bearer <token>`.
- Options: opaque random token (revocable, one DB lookup) vs JWT (no lookup but can't be revoked early, contents readable).
- **Chosen: opaque**, 32 random bytes, stored as a hash, scoped to one session.
- **Session id is derived from the token**, so customer routes have no `{id}` in the URL. This prevents **IDOR** (changing an id in the URL to reach someone else's data).

### Decision 6.4: QR handoff
- Desktop asks for a handoff, and gets back a URL with a one-time token in the **fragment** (`https://…/m#token=…`). The desktop shows it as a QR code. The phone scans it and POSTs the token to `/v1/handoff/redeem`, which atomically marks it used and issues a **new** token for the phone.

| Threat | Defence |
|---|---|
| Shoulder-surfed or photographed QR | 5-minute expiry + single use |
| Two redeems at once | Atomic `UPDATE … WHERE redeemed_at IS NULL`; only one succeeds |
| Token in logs, proxies, Referer | URL fragment: browsers never send the part after `#` to servers |
| Guessing | 256-bit random (rate limiting as a second layer; step 11) |
| Which device did what | Phone gets its own token; audit shows desktop vs mobile; each revocable separately |

- **Both devices stay valid** after handoff (desktop polls "waiting for your phone…"). Downgrading the desktop to read-only is documented as an option.
- **Drop server-side QR PNG generation:** return the URL only. Any frontend can render a QR code; less code, one less dependency.

**Changes:** reviewer flow via partner key + `reviewerId`; remove the reviewer API-key role; move API keys into `partners`; remove QR PNG generation and its package.

---

## Step 7: Document and selfie uploads

**Requirement:** document and selfie uploads from mobile cameras (2–8 MB photos on patchy networks); traffic spikes; POPIA/GDPR (ID images and selfies are highly sensitive).

### Decision 7.1: Images in S3-compatible object storage
- Options: Postgres blobs (bloats DB, backups, replication); API server disk (breaks with multiple instances, lost on restart); **object storage**.
- **Chosen:** S3-compatible storage; Postgres keeps only metadata. SeaweedFS locally (MinIO no longer publishes free images), AWS S3 in production. The code only speaks the S3 API, so the swap is config only.

### Decision 7.2: Pre-signed URL direct upload
- Options: (A) proxy bytes through our API (ties up API servers during slow mobile uploads, double bandwidth, raw images on our servers); (B) **pre-signed URLs**.
- A **pre-signed URL** is an S3 URL signed with our credentials that allows one PUT, to one path, with one content type, for a few minutes. The phone never sees our credentials.
- **Chosen: B.** Flow: `POST /v1/client/session/artifacts` (declare type, content type, size, sha256) → client PUTs to `uploadUrl` → `POST …/artifacts/{id}/complete`.

### Decision 7.3: Three integrity checks
| When | Check | Catches |
|---|---|---|
| Declare | Content type allowlist (`image/jpeg`, `image/png`), size limit, client-supplied SHA-256 | Wrong types, oversized files |
| Complete | API `HEAD`s the object: exists, size and type match | "Done" without uploading, wrong size |
| Worker, before vendors | Re-computes SHA-256 against the declared hash | File swapped after upload, corruption; audited as `artifact.integrity_failed` |

- The explicit `complete` call is used because S3 doesn't notify our API. Documented alternative: S3 event notifications (S3 → SQS).
- Documented, not built: malware scanning (GuardDuty Malware Protection for S3 / ClamAV), magic-byte file type check.

### Decision 7.4: Vendors fetch images via pre-signed GET URLs
Short-lived; bytes never flow through our worker (`ProviderRequest.ImageUrl`).

### Decision 7.5: Bucket security
Private bucket; encryption at rest (KMS in production); storage keys contain no personal data (`sessions/{sessionId}/{artifactId}`); retakes create a new artifact and mark the old one `Superseded` (kept for fraud review); signed URLs are never logged.

**Changes:** none expected; matches current code.

---

## Step 8: PII and compliance

**Requirement:** "regulated markets… broadly comparable to POPIA, GDPR, FICA and AML"; Security is 10%.

### Decision 8.1: Legal roles
- **Bank = responsible party** (POPIA) / controller (GDPR), and the FICA **accountable institution**. It collects consent and owns the customer relationship.
- **We = operator** (POPIA) / processor (GDPR). We need an operator agreement with each bank, appropriate security safeguards (POPIA s19), and breach notification to the bank.
- **Vendors = our sub-processors.**

### Decision 8.2: Classify data and minimise it
| Data | Sensitivity | Where | Protection |
|---|---|---|---|
| Selfie + ID photo | Highest: faces are **biometric** = *special personal information* under POPIA | S3 | Private bucket, KMS, short signed URLs, purged after retention |
| Extracted identity (name, DOB, ID number) | High | `extracted_identity_encrypted` | App-level AES-GCM |
| ID number for matching | High | `document_number_hash` | HMAC (keyed hash) |
| IP address, user agent | Personal data | Audit rows, token rows | Kept for fraud/security (8.6) |
| Tokens, API keys | Secrets | Token/partner tables | Hashes only |
| Status, decisions, reason codes | Not personal on their own | Normal columns, audit | Normal DB protection |

### Decision 8.3: App-level field encryption (AES-GCM)
- Options: (A) DB/disk encryption only (protects against a stolen disk, not a leaked backup, replica, SQL injection or over-privileged access); (B) A + app-level field encryption; (C) B + per-customer keys (envelope encryption, erasure by deleting the key, i.e. crypto-shredding).
- **Chosen: B.** Keep `FieldProtector` (`src/Kyc.Infrastructure/Security/Crypto.cs`, ~40 lines). AES-GCM is authenticated: tampering makes decryption fail. A key id is stored with each value so keys can be rotated. Production key from KMS / Key Vault, never config files. C documented as the next step.

### Decision 8.4: Reviewer access is masked and audited
ID number shown masked (`*********1234`), images via short-lived URLs, every view written as `artifact.viewed_by_reviewer`. Already built.

### Decision 8.5: Document number stored as an HMAC
- Needed to detect "repeated verification attempts after rejection".
- A plain SHA-256 isn't enough: a South African ID number is 13 digits (the first 6 are a date of birth), so the space is small enough to brute-force from a stolen table in minutes. **HMAC uses a secret key** kept apart from the DB, so hashes can't be reversed without it, and the same number still produces the same hash for matching.
- **Chosen:** keep.

### Decision 8.6: Retention configurable per partner
- **Tension:** POPIA/GDPR data minimisation (delete when no longer needed) vs FICA (accountable institution keeps identification records for at least 5 years after the relationship ends).
- Options: (A) we keep everything 5+ years (a big, long-lived biometric store, the worst thing to have breached); (B) purge images quickly, the bank keeps the FICA record; (C) retention configurable per partner, default as B.
- **Chosen: C.** The existing maintenance job purges image bytes N days after the decision (metadata kept, `artifact.purged` audited); make N per partner.
- **Stated assumption (README):** the bank, as the FICA accountable institution, holds the long-term identity record; retention periods to be confirmed with its compliance team.
- Kept long-term: audit trail, decisions, provider check results (no images, no readable identity).
- Documented, not built: **legal hold** flag; S3 lifecycle rule as a backstop.
- **Maintenance job kept** (it also expires abandoned sessions, step 3).

### Decision 8.7: IP addresses in the audit trail (stated exception)
An IP address is personal data under GDPR/POPIA, but it's essential fraud and security evidence. **Chosen:** keep it and state the exception openly, with a possible policy to truncate or remove IPs after N months.

### Decision 8.8: Data residency and cross-border transfer
POPIA s72 restricts transfers outside South Africa. Many KYC vendors process in the EU/US, and we send them biometric data. **Documented:** vendor selection must consider where processing happens (in-country or adequate jurisdictions, contractual safeguards); our storage and DB run in-region (e.g. AWS af-south-1, Cape Town).

### Decision 8.9: Customer rights (documented, not built)
- **Erasure:** purge images, null encrypted identity, keep audit and decision. FICA retention can override erasure, so it's a request to the bank, which decides. Possible API: `DELETE /v1/sessions/{id}/personal-data`.
- **Access requests:** answered from session, decisions and audit by session or applicant reference.
- **Consent:** collected by the bank; optionally passed as a consent reference at session creation.

### Decision 8.10: No personal data in logs
No tokens, names, ID numbers or signed URLs in logs (existing convention). Logs carry ids and the correlation id.

**Changes:** make image retention configurable per partner (column on `partners`); otherwise keep current crypto, HMAC, masking and maintenance job.

---

## Step 9: Scaling

**Requirement:** growth to tens of thousands a day with significant spikes; "evolve without requiring a complete redesign." Mostly documentation: the step 2 shape is what makes scaling possible.

### Decision 9.1: Capacity reasoning
- Peak ~3 new sessions a second, ~30 API requests a second; ~5 jobs per session (document, liveness, face match, decision, webhook), so **~15 jobs a second**.
- **Little's Law:** in-flight work = arrival rate × duration. 15 jobs/s × ~3s per vendor call = **~45 vendor calls in flight**, e.g. 5 worker instances × 10 concurrent jobs.

### Decision 9.2: Bottlenecks, in the order they appear
| # | Bottleneck | Lever |
|---|---|---|
| 1 | **Vendor rate limits and cost** | Per-vendor concurrency limit (excess waits in the queue); warn vendors before campaigns; fail fast to save fees |
| 2 | Worker capacity | Autoscale workers on queue depth / oldest-job age (KEDA, ECS target tracking) |
| 3 | DB connections | PgBouncer / RDS Proxy; small pools |
| 4 | DB size | Partition `audit_events` by month; delete completed jobs after N days; read replica for reporting and reviewer lists |
| 5 | One partner swamping others | Per-partner rate limits; per-partner queue fairness (doc) |
| 6 | API servers | Stateless; autoscale on CPU/RPS behind a load balancer |
| - | S3 | Scales on its own |

The API is the last thing to break, not the first.

### Decision 9.3: Graceful degradation under spikes
API calls stay fast; the queue grows; decisions are delayed, not lost; workers scale out and drain it; webhooks fire when ready. **SLO statement:** p95 time from selfie upload to decision under 60s normally; during spikes decisions may be delayed but no work is lost.

### Decision 9.4: When to move from the Postgres queue to a broker
~15 jobs/s is far below a `SKIP LOCKED` queue's limits (hundreds to low thousands a second). Signals to move to the step 2 hybrid: sustained thousands of jobs a second, queue contention in DB metrics, or other teams needing our events (fan-out). Cheap because of `IJobQueue`.

### Decision 9.5: Availability
App instances across multiple availability zones; Postgres Multi-AZ with automatic failover and point-in-time recovery; S3 already multi-AZ. Targets: RPO ≈ seconds, RTO ≈ minutes. Multi-region out of scope (and complicated by data residency, 8.8).

### Decision 9.6: Load testing
Documented as a validation plan (k6 against docker-compose with latency-injecting mock vendors), not built.

**Changes:** documentation only. Two possible small code items deferred to step 11: completed-job cleanup in the maintenance job; per-vendor concurrency limit (Polly).

---

## Step 10: API design and observability

**Requirement:** API design 10%, observability 5%.

### Decision 10.1: Routes grouped by caller
- Partner (`X-Api-Key`): `POST /v1/verification-sessions`, `GET /{id}`, `GET /{id}/events` (audit trail; already built).
- Reviewer (partner key + `reviewerId`): `GET /v1/reviews`, `GET /v1/reviews/{id}` (view audited), `POST /v1/reviews/{id}/decision`.
- Customer (Bearer token, no ids): `GET /v1/client/session`, `POST /handoff`, `POST /artifacts`, `POST /artifacts/{artifactId}/complete`.
- Public: `POST /v1/handoff/redeem`.
- One auth scheme and one rate-limit partition per group; the security boundary is obvious.

### Decision 10.2: URL versioning
`/v1/...`. Additive changes stay in v1 (partners must ignore unknown fields); breaking changes create `/v2` with a deprecation period. Header versioning rejected: invisible in logs and links.

### Decision 10.3: `nextAction` in status responses
Customer status returns `nextAction` (`UploadDocument`, `UploadSelfie`, `Retake`, `Wait`, `Done`). Workflow knowledge stays in the backend; frontends just render the screen for it, so workflow changes don't require frontend changes.

### Decision 10.4: Idempotency-Key on session creation (built)
`UNIQUE (partner_id, idempotency_key)`: retry with same key + body returns the same session; same key + different body returns 409. Other POSTs are idempotent via the state machine.

### Decision 10.5: RFC 7807 problem+json with a stable `code`
Every error has `type`, `title`, `status`, `code` (machine-readable, stable) and `correlationId`. 400 validation; 401/403; **404 also for another partner's session** (don't confirm existence); **409** for wrong state, idempotency mismatch, concurrent reviewer decisions (optimistic concurrency); 429 with `Retry-After`.

### Decision 10.6: Webhook contract
Thin event: `eventId`, `type`, `sessionId`, `status`, `occurredAt`; no personal data; partner calls `GET /{id}` for current details. Header `X-Kyc-Signature: t=<timestamp>,v1=<HMAC-SHA256(secret, timestamp + body)>`; partner rejects events older than ~5 minutes (replay protection) and deduplicates on `eventId`.

### Decision 10.7: Rate limiting (recommend keep; final in step 11)
Built-in ASP.NET Core middleware, ~30 lines. Partitions: per partner (fairness, 9.2), per customer session, tight per-IP limit on `/handoff/redeem`. Answers "automated abuse of onboarding channels."

### Decision 10.8: API documentation
Swagger at `/swagger`, `requests.http`, `scripts/demo.ps1`, all updated for the changes.

### Decision 10.9: Observability
- Three pillars (logs, metrics, traces) joined by a correlation id, carried across the queue on each job.
- **Technical metrics:** queue depth and oldest-job age (also the autoscaling signal), vendor latency/error rate per vendor, circuit state, dead letters, webhook failures per partner.
- **Business metrics:** sessions created, decisions by outcome, manual review rate, approval rate over time (a sudden shift signals a vendor problem or a fraud attack), drop-off by step (customer abandonment).
- **Alerts:** oldest job > 5 min; any dead letter; vendor error rate > 20% or circuit open; approval rate outside its band; webhook failures to a partner.
- Health checks `/health/live` and `/health/ready` (built).
- **Build level (recommend option B, lean; final in step 11):** structured logs + correlation id + health checks + the small metrics class with `/metrics`; OpenTelemetry tracing documented, not wired up.

**Changes:** reviewer routes move to partner auth (6.2); otherwise confirm rate limiting and observability level in step 11.

---

## Step 11: Keep, cut, change: the build plan

**Observation:** the walkthrough mostly confirmed the existing design, so the agreed changes don't shrink the codebase much (~3,800 lines today; roughly the same after the changes). The levers are scope (don't add what can be designed) and transparency about how the code was produced.

### Final answers to the open questions
- **Webhook delivery: design only.** Polling (`GET /v1/verification-sessions/{id}`) is the built mechanism; the webhook contract, signing and retry design go in ARCHITECTURE.md (10.6).
- **`partners` table: build it.** Hashed API keys in the DB (replaces `ApiClients` config), webhook URL/secret columns (for the designed webhook), per-partner image retention (8.6). Onboarding a bank is a row insert.
- **README note on how the code was produced: decide later**, after checking the assessment's rules. Leave a placeholder.

### Cut (remove from code, explain in docs)
| Item | Reference |
|---|---|
| `outbox_messages`, `OutboxPublisher`, outbox branch in `SaveChangesAsync` | 5.5 |
| QR PNG generation + QRCoder package (return the URL only) | 6.4 |
| Reviewer API-key role | 6.2 |
| OpenTelemetry tracing wiring (keep metrics) | 10.9 |

### Change
| Item | Reference |
|---|---|
| Mock providers → `Providers/VendorA/` (vendor-shaped client, adapter, options with per-vendor thresholds) | 4.5 |
| Reviewer endpoints via partner auth with required `reviewerId` | 6.2 |
| `partners` table replaces `ApiClients` config; per-partner retention used by the purge | 5.3, 6.1, 8.6 |
| Regenerate the migration; re-add the append-only trigger SQL | 5.4 |
| Update tests, `requests.http`, `scripts/demo.ps1` | — |

### Keep (decided, already built)
State machine; Postgres job queue (SKIP LOCKED, leases, backoff, dead letter); idempotent handlers; Polly timeout + circuit breaker; dead letter → ManualReview; audit in same transaction + append-only trigger; partner audit endpoint; fraud indexes; pre-signed uploads + integrity checks; opaque customer tokens; QR handoff token design; Idempotency-Key; problem+json; nextAction; AES-GCM + HMAC; masked/audited reviewer views; maintenance job; health checks + metrics (lean observability, option B); **rate limiting (kept)**.

### Document only
Webhook delivery; per-vendor concurrency limit; completed-job cleanup; tracing; broker/hybrid path; Temporal/Step Functions; OAuth2/mTLS/OIDC; hash chain; S3 events; malware scanning; legal hold; per-customer keys; erasure API; load test; multi-region.

### Order of work
1. Commit the current full version to a `full-implementation` branch; continue on `main`.
2. Cuts → build + tests green → commit.
3. Changes (Vendor A, reviewer flow, partners table) → tests green → commit.
4. Migration regenerated + trigger re-added; integration tests and demo pass against docker-compose → commit.
5. Docs: rewrite ARCHITECTURE.md (≤5 pages, from this log); update README (setup, assumptions, trade-offs, limitations, how to replace Vendor A, disclosure placeholder); tidy this file for submission.

---

## Change checklist

Internal working list; remove before submission if preferred.

### Before any trimming
- [ ] Commit the current full implementation to a branch (e.g. `full-implementation`); nothing is committed yet.

### Code changes agreed so far
- [ ] Add `partners` table (API key hashes, webhook URL + secret columns, image retention days); replaces `ApiClients` config. (5.3, 6.1, 8.6, 11)
- [ ] Remove `outbox_messages`, `OutboxPublisher`, and the outbox branch in `KycDbContext.SaveChangesAsync`. (5.5)
- [ ] Restructure mock providers into `Providers/VendorA/` (client with vendor-shaped responses, adapter, options); thresholds per vendor in config. (4.5)
- [ ] Reviewer actions via partner API key with a required `reviewerId`; actor recorded as `reviewer:<id> via partner:<id>`; remove the reviewer API-key role. (6.2)
- [ ] Return the handoff URL only; remove QR PNG generation and its package. (6.4)
- [ ] Regenerate the migration and **re-add the append-only trigger SQL** (see CLAUDE.md gotchas). (5.4)
- [ ] Update tests, `requests.http` and `scripts/demo.ps1` for the above.
- [ ] Maintenance purge reads retention per partner. (8.6)
- [ ] Remove OpenTelemetry tracing wiring; keep metrics + `/metrics`. (10.9)
- [x] Partner audit endpoint: already exists as `GET /v1/verification-sessions/{id}/events`; keep. (10.1)

### Kept as is (decided)
State machine; Postgres job queue with SKIP LOCKED, leases, retries, dead letter; idempotent handlers; Polly timeout + circuit breaker; append-only trigger; fraud indexes; pre-signed uploads with integrity checks; opaque customer tokens; QR handoff token design; AES-GCM field encryption; HMAC document number; masked + audited reviewer views; maintenance job (expiry + purge); rate limiting; health checks + metrics.

### Documentation to write (ARCHITECTURE.md / README.md)
- [ ] Back-of-envelope sizing and the growth path table. (2)
- [ ] Broker/hybrid growth path and fan-out; "the job table is our outbox". (2.2, 5.5)
- [ ] Temporal / Step Functions as the orchestration upgrade path. (3.3)
- [ ] Failure classification, retry/backoff/circuit breaker, dead letter → ManualReview, vendor idempotency keys. (4)
- [ ] Vendor replaceability, the camera SDK caveat, runtime failover as a future option; README "How to replace Vendor A". (4.4, 4.5)
- [ ] Why Postgres and why not event sourcing. (5.1, 5.2)
- [ ] Audit design, example trail, hash chain as future work. (5.4)
- [ ] OAuth2/mTLS for partners, OIDC for reviewers, desktop read-only downgrade option. (6)
- [ ] S3 event notifications, malware scanning, magic-byte check. (7)
- [ ] Webhook design (designed, not built): thin event, HMAC signature with timestamp, event id dedup, retries via job queue. (1.3, 10.6, 11)
- [ ] README placeholder: how the implementation was produced (decide after checking assessment rules). (11)
- [ ] API section: route groups, versioning policy, nextAction, idempotency, error codes, webhook contract + signature verification; observability section: metrics, alerts, tracing as next step. (10)
- [ ] Scaling section: capacity maths + Little's Law, bottleneck table, graceful degradation + SLO, broker signals, availability (multi-AZ, RPO/RTO), load test plan. (9)
- [ ] Compliance section: legal roles, data classification, retention assumption (bank holds the FICA record), legal hold, IP exception, data residency / cross-border, erasure/access/consent process, per-customer keys as next step. (8)

---

## Glossary

- **Adapter (ports and adapters):** a class that translates an external system's API into our own interface, so the rest of the code never depends on that system.
- **Aggregate root:** the entity through which all changes to a group of related data go (here, `VerificationSession`).
- **At-least-once delivery:** a message may arrive more than once, never zero times; consumers must handle duplicates.
- **Circuit breaker:** stops calling a failing dependency for a short time, then tests it again.
- **Correlation id:** an id attached to a request and everything it triggers, linking audit rows, logs and traces.
- **Dead letter:** a job that failed all retries, set aside for inspection or replay.
- **Dual-write problem:** writing to two systems (DB and broker) without a shared transaction; a crash between them leaves them inconsistent.
- **Event sourcing:** storing only events and computing state by replaying them.
- **Exponential backoff with jitter:** retry delays that double each time, plus randomness so many retries don't fire at once.
- **Fan-out:** one event delivered to several independent consumers.
- **IDOR:** Insecure Direct Object Reference; accessing someone else's data by changing an id in the request.
- **Idempotent:** safe to run more than once with the same effect as running it once.
- **Liveness:** checking that a selfie is of a live person, not a photo, replay or mask.
- **Orchestration vs choreography:** a central coordinator directs the steps vs each step reacting to the previous one's events.
- **Outbox pattern:** write outgoing messages to a DB table in the same transaction as the state change, then relay them to a broker.
- **Pre-signed URL:** a time-limited URL signed with our credentials that grants one specific storage operation.
- **SKIP LOCKED:** a Postgres option that lets workers claim rows other workers aren't already holding; the basis of our job queue.
- **URL fragment:** the part after `#`; browsers never send it to servers.
