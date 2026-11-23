# Manual testing guide

A step-by-step walkthrough of every endpoint in Swagger, success paths first, then every other outcome and error.

If you only want to see it work, run `.\scripts\demo.ps1 -Scenario all`. It does all of this automatically.

---

## 0. Setup

```bash
docker compose up --build
```

Open **http://localhost:8080/swagger**.

### Credentials

| What | Value | Notes |
|---|---|---|
| Partner API key (`X-Api-Key`) | `dev-partner-key` | Seeded on startup for partner "Demo Bank" (`appsettings.json` → `Partners:Seed`). |
| Reviewer id (`X-Reviewer-Id`) | Any staff id, e.g. `staff-1042` | **Not a key, and nothing to look up.** Reviewers work in the partner's back office, which is already authenticated by the partner API key. The back office says *which* staff member is acting, and that id is written to the audit trail and the decision. Letters, digits and `. _ @ : -`, up to 64 characters. |
| Bearer token | The `clientToken` from a response | **Not fixed.** Issued per session by `POST /v1/verification-sessions` (desktop) or `POST /v1/handoff/redeem` (phone). It only works for that one session. |
| `Idempotency-Key` header | Any unique string, e.g. `test-001` | Use a **new value for every new session**. Reusing it returns the existing session. |

### Swagger Authorize box

Click **Authorize** (top right). It has three fields. Fill them as you go:

- **ApiKey**: `dev-partner-key`. Fill it now and leave it.
- **ReviewerId**: `staff-1042`. Fill it now as well. It's ignored by the endpoints that don't need it.
- **Bearer**: you paste a `clientToken` here later. To swap tokens, click **Logout** on just that field, then paste the new one.

### Uploading files

Uploads go **directly to object storage** through a pre-signed URL, not through the API, so Swagger can't do that one
step. The helper script covers it:

```powershell
# 1. Make a test image and print the JSON body for the "declare upload" call
.\scripts\test-image.ps1 -Type IdentityDocument
# 2. After the API returns an uploadUrl, PUT the file to it
.\scripts\test-image.ps1 -Type IdentityDocument -UploadUrl "<uploadUrl>"
```

The file can contain `KYC_MOCK:` markers that control what the mock vendor returns (`-Markers DOC_TAMPERED` etc.),
the way real vendor sandboxes use magic test images. With no markers, every check passes.

> Always generate a **fresh** image per upload (the script does this). The mock vendor derives the document number from
> the file contents, so re-uploading the same file in another session triggers the fraud rules.

### Recurring sub-flow: "upload a file"

Several tests below say *"upload a document"* or *"upload a selfie (markers X)"*. That always means these four steps (Bearer must be set):

1. `.\scripts\test-image.ps1 -Type <IdentityDocument|Selfie> [-Markers X]` and copy the JSON it prints.
2. **`POST /v1/client/session/artifacts`** with that JSON as the body. Returns **201** with `artifactId` and `uploadUrl`.
3. `.\scripts\test-image.ps1 -Type <same type> -UploadUrl "<uploadUrl>"` (the URL expires after 5 minutes).
4. **`POST /v1/client/session/artifacts/{artifactId}/complete`**. Returns **202** with `"status": "DocumentSubmitted"` (or `SelfieSubmitted`).

Then **poll `GET /v1/client/session`** until `nextAction` changes. The worker picks the job up in under a second, and
the mock vendor takes about 300 ms per check, so one or two refreshes is usually enough.

| `nextAction` | Meaning |
|---|---|
| `UPLOAD_DOCUMENT` | Waiting for the ID document (if `recaptureRequested` is true, the previous one was unreadable) |
| `UPLOAD_SELFIE` | Document accepted, waiting for the selfie |
| `WAIT` | Checks running, or a human reviewer has it |
| `COMPLETE` | Approved or Rejected |
| `EXPIRED` | Session lifetime (24 h) passed |

---

## Part A: Success paths

### A1. Happy path: create, document, selfie, Approved

| # | Endpoint | Input | Expected |
|---|---|---|---|
| 1 | `POST /v1/verification-sessions` | Header `Idempotency-Key: test-001`. Body below. | **201**. Copy `sessionId` and `clientToken`. `status: Created` |
| 2 | Authorize → **Bearer** | Paste `clientToken` | |
| 3 | `GET /v1/client/session` | none | **200**, `status: Created`, `nextAction: UPLOAD_DOCUMENT`, 3 attempts remaining for each |
| 4 | Upload a document (sub-flow above, no markers) | | `complete` returns **202** `DocumentSubmitted` |
| 5 | `GET /v1/client/session` (poll) | | `status: AwaitingSelfie`, `nextAction: UPLOAD_SELFIE` |
| 6 | Upload a selfie (no markers) | | `complete` returns **202** `SelfieSubmitted` |
| 7 | `GET /v1/client/session` (poll) | | `status: Approved`, `nextAction: COMPLETE` |
| 8 | `GET /v1/verification-sessions/{sessionId}` | `sessionId` from step 1 | **200**. Three checks (`DocumentValidation`, `Liveness`, `FaceMatch`) all `Passed`; one decision `Approved`, `decidedBy: System`, reason `ALL_CHECKS_PASSED` |
| 9 | `GET /v1/verification-sessions/{sessionId}/events` | | **200**. The full audit trail: `session.created`, `artifact.upload_requested`, `artifact.uploaded`, `check.completed` ×3, `session.status_changed` (several), `risk.assessed`, `decision.made` |

Body for step 1. **Change `ipAddress` every few sessions.** More than 10 sessions from one IP in 24 h triggers the
`HIGH_IP_VELOCITY` fraud rule, and everything goes to manual review.

```json
{
  "applicantReference": "customer-001",
  "channel": "Web",
  "customer": { "ipAddress": "198.51.100.7", "userAgent": "Mozilla/5.0 Desktop", "deviceFingerprint": "fp-desktop" }
}
```

Notice that the customer view (`/v1/client/session`) never shows reasons or scores, while the partner view does.
That's deliberate: a fraudster shouldn't learn what to change on the next attempt.

### A2. Idempotent retry

| # | Endpoint | Input | Expected |
|---|---|---|---|
| 1 | `POST /v1/verification-sessions` | **Same** `Idempotency-Key` and **same** body as A1 step 1 | **200** (not 201), **same `sessionId`**, a new `clientToken` |

### A3. Continue on a phone (QR handoff)

Start a new session (A1 steps 1–3, new `Idempotency-Key`), then:

| # | Endpoint | Input | Expected |
|---|---|---|---|
| 1 | `POST /v1/client/session/handoff` | Bearer = desktop token | **200**, `handoffUrl` like `http://localhost:3000/verify/continue#token=AbC...`. Copy the part after `#token=`. In production the front end shows this URL as a QR code. |
| 2 | `POST /v1/handoff/redeem` | No auth needed. Body `{ "token": "<from step 1>", "deviceFingerprint": "fp-phone" }` | **200**, a **new** `clientToken` (the phone's) and the session view |
| 3 | Authorize → Bearer | Log out, paste the **phone** token | |
| 4 | Upload a document and a selfie as in A1 | | Ends `Approved` |
| 5 | Switch Bearer back to the desktop token and `GET /v1/client/session` | | Also `Approved`. Both devices see the same progress. |

The audit trail now also has `handoff.issued` and `handoff.redeemed`.

### A4. Recapture, then approved

Shows the customer being asked to retake a blurry photo instead of being rejected.

| # | Step | Expected |
|---|---|---|
| 1 | New session, upload a document with `-Markers DOC_BLURRY` | |
| 2 | Poll `GET /v1/client/session` | `status: Created`, `nextAction: UPLOAD_DOCUMENT`, **`recaptureRequested: true`**, `documentAttemptsRemaining: 2` |
| 3 | Upload a clean document, then a clean selfie | Ends `Approved` |

Audit trail: `session.recapture_requested`. Partner view: two `DocumentValidation` checks (`Unreadable`, then `Passed`).
`-Markers LIVENESS_BLURRY` on the selfie does the same for the selfie step.

### A5. Vendor glitch, automatic retry, approved

| # | Step | Expected |
|---|---|---|
| 1 | New session, upload a document with `-Markers PROVIDER_FLAKY` | The vendor times out on the first call |
| 2 | Poll for ~3 seconds | `UPLOAD_SELFIE`. The job retried with backoff and succeeded |
| 3 | Upload a clean selfie | `Approved` |

`docker compose logs worker` shows `Job failed (attempt 1/5); retrying in ...`.

### A6. Manual review, reviewer approves

| # | Endpoint | Input | Expected |
|---|---|---|---|
| 1 | New session, clean document, selfie with `-Markers FACE_WEAK` | | Poll: `status: ManualReview`, `nextAction: WAIT` |
| 2 | `GET /v1/reviews` | ApiKey + ReviewerId set | **200**, your session is in the queue with `reasonCodes: ["FACE_MATCH_LOW_CONFIDENCE"]` and a `riskScore` |
| 3 | `GET /v1/reviews/{sessionId}` | | **200**. Checks, the extracted identity (name, date of birth, **masked** document number), short-lived image links (2 min), and related sessions with the same document |
| 4 | `POST /v1/reviews/{sessionId}/decision` | `{ "outcome": "Approved", "reasonCode": "VERIFIED_MANUALLY", "notes": "Checked against document" }` | **200**, `status: Approved`. A second decision appears with `decidedBy: Reviewer`, `reviewerId: staff-1042` |
| 5 | `GET /v1/verification-sessions/{sessionId}/events` | | Includes `artifact.viewed_by_reviewer` (opening the case is itself audited) and a `decision.made` with actor `reviewer:staff-1042 via partner:demo-bank` |

---

## Part B: Other outcomes

Each row is a new session (new `Idempotency-Key`). "doc" and "selfie" mean the upload sub-flow with those markers.
Check the result with `GET /v1/verification-sessions/{sessionId}` and look at `status` and `decisions[].reasonCodes`.

### Rejected automatically

| Scenario | doc markers | selfie markers | Final status | Reason codes | Note |
|---|---|---|---|---|---|
| Tampered document | `DOC_TAMPERED` | (no selfie needed) | **Rejected** | `DOCUMENT_FAILED` | Rejected right after the document, so the customer isn't asked for a selfie |
| Expired document | `DOC_EXPIRED` | none | **Rejected** | `DOCUMENT_EXPIRED` | The document check passes, but the extracted expiry date is in the past |
| Spoofed selfie | none | `LIVENESS_SPOOF` | **Rejected** | `LIVENESS_FAILED` | Face match is skipped |
| Different person | none | `FACE_MISMATCH` | **Rejected** | `FACE_MISMATCH` | Face score 0.21 (< 0.60) |

### Rejected by a reviewer

Same as A6, but decide with `{ "outcome": "Rejected", "reasonCode": "DOCUMENT_MISMATCH", "notes": "..." }`. Ends **Rejected**.

### Sent to manual review

| Scenario | How | Reason codes | Note |
|---|---|---|---|
| Uncertain document | doc `DOC_UNCERTAIN`, clean selfie | `DOCUMENT_INCONCLUSIVE` | |
| Uncertain liveness | clean doc, selfie `LIVENESS_UNCERTAIN` | `LIVENESS_INCONCLUSIVE` | |
| Weak face match | clean doc, selfie `FACE_WEAK` | `FACE_MATCH_LOW_CONFIDENCE` | Score 0.72 (between 0.60 and 0.85) |
| Vendor down | doc `PROVIDER_DOWN` | `PROVIDER_UNAVAILABLE` | Takes **~30–40 s** (5 attempts with backoff). We don't reject a customer for our vendor's outage |
| Vendor misconfigured | doc `PROVIDER_MISCONFIGURED` | `PROVIDER_UNAVAILABLE` | Almost immediate: a 401 from the vendor is permanent, so there are no retries |
| Recaptures used up | doc `DOC_BLURRY` three times (after the 1st and 2nd, `nextAction` returns to `UPLOAD_DOCUMENT`) | `DOCUMENT_UNREADABLE`, `LIVENESS_CHECK_MISSING`, `FACE_MATCH_MISSING` | After the 3rd, the session goes straight to a decision with no selfie step |
| Repeat after rejection | Session 1: doc `DOC_TAMPERED,DOC_NUMBER=RPT1` (Rejected). Session 2: doc `DOC_NUMBER=RPT1`, clean selfie | `REPEAT_ATTEMPT_AFTER_REJECTION` | `GET /v1/reviews/{id}` lists session 1 under `relatedSessions`. Use a **new number** (`RPT2`, ...) each time you rerun it |
| Same document, two open sessions | Session 1: doc `DOC_NUMBER=DUP1`, stop before the selfie. Session 2: doc `DOC_NUMBER=DUP1`, clean selfie | `CONCURRENT_SESSIONS_SAME_DOCUMENT` | |
| Reused selfie | Session 1: complete normally. Session 2: clean doc, then for the selfie **don't** rerun the script; reuse the same JSON body and file | `SELFIE_REUSED` | Same photo across applicants is a classic fraud signal |
| High IP velocity | Create 11+ sessions with the same `customer.ipAddress`, then complete one | `HIGH_IP_VELOCITY` | |
| File swapped after upload | Declare with `"sha256": "0000…0000"` (64 zeros) but the real `sizeBytes`, then PUT the real file | `ARTIFACT_INTEGRITY_MISMATCH` on the check, plus a recapture request | `complete` succeeds; the worker re-hashes the file and catches the mismatch before any vendor sees it. After `complete` the bytes are sealed, so re-PUTting the upload URL can't swap what a vendor reads |

Pass several markers like this: `-Markers DOC_TAMPERED,DOC_NUMBER=RPT1`.

Fraud signals only ever send a session to manual review, never to an automatic rejection.

---

## Part C: Error responses

All errors are `application/problem+json` with a stable `code`.

### Partner endpoints (`/v1/verification-sessions`)

| Test | How | Expected |
|---|---|---|
| No API key | Authorize → Logout on ApiKey, call any partner endpoint | **401** |
| Wrong API key | ApiKey = `wrong` | **401** |
| Missing idempotency key | Leave `Idempotency-Key` empty | **400** `validation_failed` |
| Key reused with a different body | Same `Idempotency-Key` as an earlier session, change `applicantReference` | **422** `idempotency_key_reused` |
| Missing applicant reference | `"applicantReference": ""` | **400** `validation_failed` |
| Unknown session | `GET /v1/verification-sessions/{random guid}` | **404**. Another partner's session also returns 404 (not 403), so ids can't be probed |

### Customer endpoints (`/v1/client/...`, `/v1/handoff/redeem`)

| Test | How | Expected |
|---|---|---|
| No / bad token | Logout on Bearer, or paste `abc` | **401** |
| Wrong content type | Declare with `"contentType": "image/gif"` | **400** `validation_failed` |
| Bad hash | `"sha256": "abc"` | **400** `validation_failed` |
| Too big | `"sizeBytes": 20000000` (limit 10 MB) | **400** `validation_failed` |
| Selfie before document | Declare `"type": "Selfie"` on a new session | **409** `unexpected_artifact` |
| Complete without uploading | Declare, skip the PUT, call `complete` | **422** `upload_not_found` |
| Size doesn't match | Declare `sizeBytes` 1 larger than the file, PUT, `complete` | **422** `upload_size_mismatch` |
| Complete twice | Call `complete` again on the same artifact | **202**, same answer (safe to retry) |
| Upload while checks are running | Declare a document again right after `complete` (status `DocumentSubmitted`) | **409** `unexpected_artifact` |
| Upload after the session finished | Declare a document on an Approved / Rejected / ManualReview session | **409** `unexpected_artifact` |
| QR token used twice | Redeem the same handoff token again | **422** `handoff_invalid`, and the audit trail gets `handoff.redeem_failed` (reason `already_redeemed`) |
| Old QR after a new one | Create a handoff twice, redeem the **first** token | **422** `handoff_invalid` (reason `revoked`). Only one live QR per session |
| Unknown QR token | `{ "token": "made-up" }` | **422** `handoff_invalid`, the same message, so valid tokens can't be probed |
| QR after the session finished | `POST /v1/client/session/handoff` on an Approved session | **409** `session_complete` |
| Redeem rate limit | Redeem 11+ times within a minute | **429** |

### Reviewer endpoints (`/v1/reviews`)

| Test | How | Expected |
|---|---|---|
| No reviewer id | Logout on ReviewerId | **400** `validation_failed` (`X-Reviewer-Id` required) |
| Bad reviewer id | ReviewerId = `has spaces` | **400** `validation_failed` |
| Session not in review | Decide on an Approved session | **409** `not_in_review` |
| Invalid outcome | `"outcome": "ManualReview"` | **400** `validation_failed` (must be Approved or Rejected) |
| Missing reason | `"reasonCode": ""` | **400** `validation_failed` |
| Decide twice | Decide again after a successful decision | **409** `not_in_review` |

---

## Other ways to run the same tests

- **`scripts/demo.ps1`** automates Parts A and B: `-Scenario happy | doc-tampered | doc-blurry | face-weak |
  liveness-spoof | flaky-provider | provider-down | provider-misconfigured | repeat-after-reject | all`.
- **`requests.http`**: click-through version for Visual Studio / Rider / VS Code. It chains ids and tokens automatically.
- **Integration tests**: `dotnet test tests/Kyc.IntegrationTests` covers the journey, concurrency (exactly one QR
  redeem wins), tenant isolation, idempotency and audit immutability against real Postgres and S3.
