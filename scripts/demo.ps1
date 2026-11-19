<#
.SYNOPSIS
  End-to-end demo of the KYC journey: partner creates session -> desktop issues QR handoff -> mobile redeems ->
  mobile uploads document + selfie via pre-signed URLs -> worker runs checks -> decision -> audit trail.

.EXAMPLE
  .\scripts\demo.ps1                        # happy path
  .\scripts\demo.ps1 -Scenario face-weak    # ends in ManualReview, then a reviewer approves it
  .\scripts\demo.ps1 -Scenario all          # run every scenario

  Scenarios: happy, doc-tampered, doc-blurry, face-weak, liveness-spoof, flaky-provider, provider-down, repeat-after-reject
#>
param(
    [string]$BaseUrl = "http://localhost:8080",
    [string]$Scenario = "happy",
    [string]$PartnerKey = "dev-partner-key",
    [string]$ReviewerKey = "dev-reviewer-key"
)

$ErrorActionPreference = "Stop"
$scenarios = [ordered]@{
    # name                 = @(document markers,            selfie markers,          expected outcome, recapture doc?)
    "happy"               = @(@(),                          @(),                     "Approved",     $false)
    "doc-tampered"        = @(@("DOC_TAMPERED"),            @(),                     "Rejected",     $false)
    "doc-blurry"          = @(@("DOC_BLURRY"),              @(),                     "Approved",     $true)
    "face-weak"           = @(@(),                          @("FACE_WEAK"),          "ManualReview", $false)
    "liveness-spoof"      = @(@(),                          @("LIVENESS_SPOOF"),     "Rejected",     $false)
    "flaky-provider"      = @(@("PROVIDER_FLAKY"),          @(),                     "Approved",     $false)
    "provider-down"       = @(@("PROVIDER_DOWN"),           @(),                     "ManualReview", $false)
    "repeat-after-reject" = @(@("DOC_NUMBER=RPT900101"),    @(),                     "ManualReview", $false)
}

function Write-Step($text) { Write-Host "`n>> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "   $text" -ForegroundColor Green }

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
    $params = @{ Method = $Method; Uri = "$BaseUrl$Path"; Headers = $Headers; ContentType = "application/json" }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 10) }
    try { return Invoke-RestMethod @params }
    catch {
        $detail = $_.ErrorDetails.Message
        throw "$Method $Path failed: $($_.Exception.Message) $detail"
    }
}

# Fake image: a JPEG header plus optional mock markers (the way vendor sandboxes use magic test images).
function New-TestImage([string[]]$Markers, [string]$Salt) {
    $text = "KYC test image $Salt " + (($Markers | ForEach-Object { "KYC_MOCK:$_" }) -join " ")
    $header = [byte[]](0xFF, 0xD8, 0xFF, 0xE0)
    return $header + [System.Text.Encoding]::ASCII.GetBytes($text)
}

function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    return (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString("x2") }) -join "")
}

function Send-Artifact($Token, [string]$Type, [byte[]]$Bytes) {
    $auth = @{ Authorization = "Bearer $Token" }
    $slot = Invoke-Api POST "/v1/client/session/artifacts" @{
        type = $Type; contentType = "image/jpeg"; sizeBytes = $Bytes.Length; sha256 = (Get-Sha256Hex $Bytes)
    } $auth
    Write-Ok "pre-signed PUT issued for $Type (expires $($slot.expiresAt))"
    # Straight to object storage. The API never sees the bytes.
    Invoke-WebRequest -Method Put -Uri $slot.uploadUrl -Body $Bytes -ContentType "image/jpeg" -UseBasicParsing | Out-Null
    Write-Ok "uploaded $($Bytes.Length) bytes directly to object storage"
    $state = Invoke-Api POST "/v1/client/session/artifacts/$($slot.artifactId)/complete" $null $auth
    Write-Ok "upload confirmed -> status $($state.status)"
}

function Wait-ForNextAction($Token, [string[]]$Until, [int]$TimeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $s = Invoke-Api GET "/v1/client/session" $null @{ Authorization = "Bearer $Token" }
        if ($Until -contains $s.nextAction -or ($s.status -eq "ManualReview" -and $Until -contains "REVIEW")) { return $s }
        Start-Sleep -Milliseconds 1500  # a realistic client poll interval (rate limit is 60/min per session)
    }
    throw "Timed out waiting for $($Until -join '/')"
}

function Invoke-Scenario([string]$Name) {
    $def = $scenarios[$Name]
    if ($null -eq $def) { throw "Unknown scenario '$Name'. Options: $($scenarios.Keys -join ', ')" }
    $docMarkers, $selfieMarkers, $expected, $recapture = $def
    $salt = [guid]::NewGuid().ToString("N")
    # A distinct customer IP per run; reusing one would (correctly) trip the HIGH_IP_VELOCITY fraud rule.
    $customerIp = "198.51.100.$(Get-Random -Minimum 1 -Maximum 254)"
    $partner = @{ "X-Api-Key" = $PartnerKey; "Idempotency-Key" = "demo-$salt"; "X-Correlation-Id" = "demo-$salt" }

    Write-Host "`n================ Scenario: $Name (expect $expected) ================" -ForegroundColor Yellow

    if ($Name -eq "repeat-after-reject") {
        Write-Step "Setting the scene: an earlier session with the same document was rejected"
        $prior = Invoke-Api POST "/v1/verification-sessions" @{ applicantReference = "prior-$salt"; channel = "Web" } `
            @{ "X-Api-Key" = $PartnerKey; "Idempotency-Key" = "prior-$salt" }
        Send-Artifact $prior.clientToken "IdentityDocument" (New-TestImage @("DOC_NUMBER=RPT900101", "DOC_TAMPERED") "prior-$salt")
        $p = Wait-ForNextAction $prior.clientToken @("COMPLETE")
        Write-Ok "prior session finished: $($p.status)"
    }

    Write-Step "1. Partner backend creates a session (server-to-server, idempotent)"
    $created = Invoke-Api POST "/v1/verification-sessions" @{
        applicantReference = "applicant-$salt"; channel = "Web"
        customer = @{ ipAddress = $customerIp; userAgent = "Mozilla/5.0 (Windows NT 10.0) Desktop"; deviceFingerprint = "fp-desktop-$salt" }
    } $partner
    Write-Ok "session $($created.sessionId) status=$($created.status)"
    $replay = Invoke-Api POST "/v1/verification-sessions" @{
        applicantReference = "applicant-$salt"; channel = "Web"
        customer = @{ ipAddress = $customerIp; userAgent = "Mozilla/5.0 (Windows NT 10.0) Desktop"; deviceFingerprint = "fp-desktop-$salt" }
    } $partner
    Write-Ok "retry with same Idempotency-Key returned same session: $($replay.sessionId -eq $created.sessionId)"
    $desktop = $created.clientToken

    Write-Step "2. Desktop asks for a QR code to continue on the phone"
    $handoff = Invoke-Api POST "/v1/client/session/handoff" $null @{ Authorization = "Bearer $desktop" }
    Write-Ok "handoff URL: $($handoff.handoffUrl.Substring(0, 60))... (QR PNG: $($handoff.qrCodePngBase64.Length) base64 chars)"
    $handoffToken = ($handoff.handoffUrl -split "#token=")[1]

    Write-Step "3. Phone scans the QR and redeems the single-use token"
    $mobile = Invoke-Api POST "/v1/handoff/redeem" @{ token = $handoffToken; deviceFingerprint = "fp-phone-$salt" } `
        @{ "User-Agent" = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0) Mobile" }
    Write-Ok "phone has its own token; session nextAction=$($mobile.session.nextAction)"
    try {
        Invoke-Api POST "/v1/handoff/redeem" @{ token = $handoffToken } | Out-Null
        Write-Host "   !! replay unexpectedly succeeded" -ForegroundColor Red
    } catch { Write-Ok "replaying the same QR token is refused (single-use)" }
    $phone = $mobile.clientToken

    Write-Step "4. Phone uploads the identity document"
    Send-Artifact $phone "IdentityDocument" (New-TestImage $docMarkers "doc-$salt")
    if ($recapture) {
        $s = Wait-ForNextAction $phone @("UPLOAD_DOCUMENT", "UPLOAD_SELFIE")
        if ($s.recaptureRequested) {
            Write-Ok "provider said the image is unreadable -> recapture requested ($($s.documentAttemptsRemaining) attempts left)"
            Send-Artifact $phone "IdentityDocument" (New-TestImage @() "doc2-$salt")
        }
    }

    $s = Wait-ForNextAction $phone @("UPLOAD_SELFIE", "COMPLETE", "REVIEW") -TimeoutSeconds 120
    if ($s.nextAction -eq "UPLOAD_SELFIE") {
        Write-Step "5. Document accepted; phone uploads a selfie"
        Send-Artifact $phone "Selfie" (New-TestImage $selfieMarkers "selfie-$salt")
        $s = Wait-ForNextAction $phone @("COMPLETE", "REVIEW") -TimeoutSeconds 120
    }

    Write-Step "6. Outcome (customer view, deliberately without reasons)"
    Write-Ok "status=$($s.status) nextAction=$($s.nextAction)"
    Write-Ok "desktop sees the same progress: $((Invoke-Api GET '/v1/client/session' $null @{ Authorization = "Bearer $desktop" }).status)"

    $details = Invoke-Api GET "/v1/verification-sessions/$($created.sessionId)" $null @{ "X-Api-Key" = $PartnerKey }
    Write-Step "7. Partner view: checks and decision"
    foreach ($c in $details.checks) { Write-Ok ("{0,-20} {1,-12} score={2} provider={3} attempt={4} {5}" -f $c.type, $c.outcome, $c.score, $c.provider, $c.attempt, ($c.reasonCodes -join ",")) }
    foreach ($d in $details.decisions) { Write-Ok ("decision {0} by {1} reasons={2}" -f $d.outcome, $d.decidedBy, ($d.reasonCodes -join ",")) }

    if ($details.status -eq "ManualReview") {
        Write-Step "8. Reviewer opens the case and approves it"
        $reviewer = @{ "X-Api-Key" = $ReviewerKey }
        $case = Invoke-Api GET "/v1/reviews/$($created.sessionId)" $null $reviewer
        Write-Ok "case shows $($case.images.Count) short-lived image links, $($case.relatedSessions.Count) related sessions, doc# $($case.identity.documentNumberMasked)"
        $null = Invoke-Api POST "/v1/reviews/$($created.sessionId)/decision" @{ outcome = "Approved"; reasonCode = "VERIFIED_MANUALLY"; notes = "Demo: checked against document" } $reviewer
        $details = Invoke-Api GET "/v1/verification-sessions/$($created.sessionId)" $null @{ "X-Api-Key" = $PartnerKey }
        Write-Ok "final status: $($details.status)"
    }

    Write-Step "9. Audit trail (append-only)"
    $events = Invoke-Api GET "/v1/verification-sessions/$($created.sessionId)/events" $null @{ "X-Api-Key" = $PartnerKey }
    foreach ($e in $events) { Write-Host ("   #{0,-5} {1,-30} {2,-9} {3}" -f $e.id, $e.eventType, $e.actorType, $e.actorId) }

    $firstDecision = $details.decisions[0].outcome
    if ($firstDecision -eq $expected) { Write-Host "`n   PASS: automated outcome $firstDecision" -ForegroundColor Green }
    else { Write-Host "`n   UNEXPECTED: automated outcome $firstDecision (expected $expected)" -ForegroundColor Red; $script:failed = $true }
}

$script:failed = $false
if ($Scenario -eq "all") { foreach ($n in $scenarios.Keys) { Invoke-Scenario $n } } else { Invoke-Scenario $Scenario }
if ($script:failed) { exit 1 }
