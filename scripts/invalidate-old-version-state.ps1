# ═══════════════════════════════════════════════════════════════════════
# OPC UA KB — One-off helper: invalidate older-version crawl state
#
# Why this exists:
#   The crawler used to omit the .html suffix for HTML responses on
#   extensionless URLs (e.g., /DI/v102/docs/1), and older versions like
#   /DI/v101/docs/ aren't reachable from the main page. This script
#   removes entries for /v10x/ URLs from `_crawl-state.json` so the next
#   pipeline run re-fetches them, and deletes the matching legacy
#   extensionless blobs so the new ones (with .html suffix) won't have
#   orphans alongside them.
#
# Behaviour:
#   - Removes every state entry whose URL matches /v10\d(?:[a-z])?/
#   - Deletes every blob whose name matches ^[^/]+/v10\d[a-z]?/
#   - Idempotent — re-running after a clean state is a no-op.
#   - -DryRun lists counts without modifying anything.
#
# Usage:
#   .\scripts\invalidate-old-version-state.ps1 -DryRun
#   .\scripts\invalidate-old-version-state.ps1
# ═══════════════════════════════════════════════════════════════════════

[CmdletBinding()]
param(
    [string]$StorageAccount = "opcuakbstorage",
    [string]$ResourceGroup  = "rg-opcua-kb",
    [string]$Container      = "opcua-content",
    [string]$Subscription   = "d355e77f-e625-4931-b133-445d3dcf12cb",
    [string]$StateBlob      = "_crawl-state.json",
    [string]$AccountKey     = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Write-Info { param([string]$Msg) Write-Host "[INFO]  $Msg" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Msg) Write-Host "[OK]    $Msg" -ForegroundColor Green }
function Write-Warn { param([string]$Msg) Write-Host "[WARN]  $Msg" -ForegroundColor Yellow }
function Write-Fail { param([string]$Msg) Write-Host "[FAIL]  $Msg" -ForegroundColor Red; throw $Msg }

# Resolve az CLI Python (same pattern as scripts/install-agent.ps1) to
# avoid PowerShell mangling args containing $, !, +, =, & in account keys.
$PyPathCandidates = @(
    "C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe",
    "C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\python.exe",
    "$env:LOCALAPPDATA\Programs\Microsoft SDKs\Azure\CLI2\python.exe"
)
$PyPath = $PyPathCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

function Invoke-Az {
    if ($script:PyPath) { & $script:PyPath -IBm azure.cli @args }
    else                { & az @args }
    if ($LASTEXITCODE -ne 0) { throw "az CLI failed (exit $LASTEXITCODE): az $($args -join ' ')" }
}

function Invoke-AzText {
    $out = if ($script:PyPath) { & $script:PyPath -IBm azure.cli @args 2>$null }
           else                { & az @args 2>$null }
    if ($LASTEXITCODE -ne 0 -or $null -eq $out) { return $null }
    $joined = ($out -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($joined)) { return $null }
    return $joined
}

# ── Resolve subscription + storage account key ──────────────────────────
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Fail "Azure CLI ('az') not found."
}
if ($PyPath) { Write-Ok "Using az python: $PyPath" }
else         { Write-Warn "az python not found — falling back to 'az'. Special chars in keys may misbehave." }

if ($Subscription) {
    Write-Info "Setting subscription: $Subscription"
    Invoke-Az account set --subscription $Subscription -o none
}

if (-not $AccountKey) {
    Write-Info "Fetching storage account key for $StorageAccount..."
    $AccountKey = Invoke-AzText storage account keys list `
        --account-name $StorageAccount -g $ResourceGroup `
        --query "[0].value" -o tsv
    if (-not $AccountKey) { Write-Fail "Could not fetch storage account key." }
}
Write-Ok "Storage key resolved (length=$($AccountKey.Length))."

# ── Download _crawl-state.json to a script-local temp file ──────────────
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$WorkDir     = Join-Path $ScriptDir ".invalidate-state-tmp"
if (-not (Test-Path $WorkDir)) { New-Item -ItemType Directory -Path $WorkDir | Out-Null }
$LocalState  = Join-Path $WorkDir "_crawl-state.json"
$CleanedFile = Join-Path $WorkDir "_crawl-state.cleaned.json"
if (Test-Path $LocalState)  { Remove-Item $LocalState  -Force }
if (Test-Path $CleanedFile) { Remove-Item $CleanedFile -Force }

Write-Info "Downloading $StateBlob from $StorageAccount/$Container..."
Invoke-Az storage blob download `
    --account-name $StorageAccount --account-key $AccountKey `
    --container-name $Container --name $StateBlob `
    --file $LocalState --no-progress -o none

$stateRaw = Get-Content -Raw -LiteralPath $LocalState
$stateObj = $stateRaw | ConvertFrom-Json

# Support both shapes used in the codebase:
#   - Pipeline format: flat { "https://...": "2024-01-01T..." }
#   - Crawler format:  { "CrawledUrls": { "https://...": "..." } }
$wrapped  = $false
$entries  = $stateObj
if ($stateObj.PSObject.Properties.Name -contains "CrawledUrls") {
    $wrapped = $true
    $entries = $stateObj.CrawledUrls
}

$allUrls    = @($entries.PSObject.Properties.Name)
$urlRegex   = [regex]'/v10\d(?:[a-z])?/'
$matchedUrls = @($allUrls | Where-Object { $urlRegex.IsMatch($_) })
Write-Info "State entries total: $($allUrls.Count); matching /v10\d[a-z]?/: $($matchedUrls.Count)"

# ── List blobs matching ^[^/]+/v10\d[a-z]?/ ─────────────────────────────
# Use --num-results '*' to enumerate all blobs (default is capped at 5000).
Write-Info "Listing blobs in $Container (full enumeration, may take a moment)..."
$blobListJson = Invoke-AzText storage blob list `
    --account-name $StorageAccount --account-key $AccountKey `
    --container-name $Container --num-results '*' --query "[].name" -o json
if (-not $blobListJson) { $blobListJson = "[]" }
$allBlobs   = @($blobListJson | ConvertFrom-Json)
$blobRegex  = [regex]'^[^/]+/v10\d[a-z]?/'
$matchedBlobs = @($allBlobs | Where-Object { $blobRegex.IsMatch($_) })
Write-Info "Blobs total: $($allBlobs.Count); matching '^[^/]+/v10\d[a-z]?/': $($matchedBlobs.Count)"

# Show a small sample so dry-run output is informative.
if ($matchedUrls.Count -gt 0) {
    Write-Info "Sample matching URLs (first 5):"
    $matchedUrls | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
}
if ($matchedBlobs.Count -gt 0) {
    Write-Info "Sample matching blobs (first 5):"
    $matchedBlobs | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
}

if ($DryRun) {
    Write-Ok "[DRY-RUN] Would remove $($matchedUrls.Count) state entries and delete $($matchedBlobs.Count) blobs. No changes made."
    exit 0
}

# ── Apply changes ───────────────────────────────────────────────────────
foreach ($u in $matchedUrls) { $entries.PSObject.Properties.Remove($u) | Out-Null }

# Re-serialise. Pipeline writes indented JSON, so we do too (Depth=10 plenty).
$outJson = if ($wrapped) { $stateObj | ConvertTo-Json -Depth 10 }
           else          { $entries  | ConvertTo-Json -Depth 10 }
[System.IO.File]::WriteAllText($CleanedFile, $outJson, [System.Text.UTF8Encoding]::new($false))
Write-Ok "Wrote cleaned state to $CleanedFile (removed $($matchedUrls.Count) entries)."

# Upload cleaned state (overwrite).
Write-Info "Uploading cleaned $StateBlob..."
Invoke-Az storage blob upload `
    --account-name $StorageAccount --account-key $AccountKey `
    --container-name $Container --name $StateBlob `
    --file $CleanedFile --content-type "application/json" `
    --overwrite --no-progress -o none
Write-Ok "Uploaded cleaned crawl state."

# Delete matching blobs in batches (az caps --name-or-uri-list ~256).
if ($matchedBlobs.Count -gt 0) {
    $batchSize = 200
    $deleted   = 0
    for ($i = 0; $i -lt $matchedBlobs.Count; $i += $batchSize) {
        $batch = $matchedBlobs[$i..([Math]::Min($i + $batchSize - 1, $matchedBlobs.Count - 1))]
        Write-Info ("Deleting blobs {0}..{1} of {2}..." -f ($i + 1), ($i + $batch.Count), $matchedBlobs.Count)
        # Per-blob deletes — avoid delete-batch --pattern which is too broad
        # and would risk wiping unintended blobs in this container.
        foreach ($name in $batch) {
            Invoke-Az storage blob delete `
                --account-name $StorageAccount --account-key $AccountKey `
                --container-name $Container --name $name -o none
            $deleted++
        }
    }
    Write-Ok "Deleted $deleted blobs."
} else {
    Write-Ok "No matching blobs to delete."
}

Write-Ok "Done. Removed $($matchedUrls.Count) state entries; deleted $($matchedBlobs.Count) blobs."
