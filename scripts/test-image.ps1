<#
.SYNOPSIS
  Helper for testing uploads by hand (Swagger / requests.http). See docs/TESTING.md.

.DESCRIPTION
  Step 1 (no -UploadUrl): writes a fake JPEG containing KYC_MOCK markers and prints the JSON body to paste into
          POST /v1/client/session/artifacts.
  Step 2 (with -UploadUrl): PUTs that same file to the pre-signed URL the API returned.

  Every file gets a random salt. The mock vendor derives the document number from the file hash, so reusing a
  file across sessions would trigger the repeat-document / reused-selfie fraud rules.

.EXAMPLE
  .\scripts\test-image.ps1 -Type IdentityDocument                         # clean document (passes)
  .\scripts\test-image.ps1 -Type IdentityDocument -Markers DOC_TAMPERED   # tampered document
  .\scripts\test-image.ps1 -Type Selfie -Markers FACE_WEAK
  .\scripts\test-image.ps1 -Type IdentityDocument -UploadUrl "<uploadUrl from the response>"
#>
param(
    [ValidateSet("IdentityDocument", "Selfie")]
    [string]$Type = "IdentityDocument",
    [string[]]$Markers = @(),
    [string]$UploadUrl
)

$ErrorActionPreference = "Stop"
$path = Join-Path ([System.IO.Path]::GetTempPath()) "kyc-test-$Type.jpg"

if ($UploadUrl) {
    if (-not (Test-Path $path)) { throw "No test file at $path. Run without -UploadUrl first." }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    Invoke-WebRequest -Method Put -Uri $UploadUrl -Body $bytes -ContentType "image/jpeg" -UseBasicParsing | Out-Null
    Write-Host "Uploaded $($bytes.Length) bytes. Now call POST /v1/client/session/artifacts/{artifactId}/complete" -ForegroundColor Green
    return
}

$salt = [guid]::NewGuid().ToString("N")
$text = "KYC test image $salt " + (($Markers | ForEach-Object { "KYC_MOCK:$_" }) -join " ")
$bytes = [byte[]](0xFF, 0xD8, 0xFF, 0xE0) + [System.Text.Encoding]::ASCII.GetBytes($text)
[System.IO.File]::WriteAllBytes($path, $bytes)

$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join "")

Write-Host "Wrote $path (markers: $(if ($Markers) { $Markers -join ', ' } else { 'none' }))" -ForegroundColor Green
Write-Host "Paste this body into POST /v1/client/session/artifacts:`n"
@{ type = $Type; contentType = "image/jpeg"; sizeBytes = $bytes.Length; sha256 = $hash } | ConvertTo-Json
