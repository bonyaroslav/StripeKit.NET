param(
    [string]$SchemaPath = "samples/StripeKit.SampleApi/SampleStorage/schema.sql"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SchemaPath))
{
    throw "Schema file not found: $SchemaPath"
}

$content = Get-Content $SchemaPath -Raw

$requiredSnippets = @(
    "create table if not exists customer_mappings",
    "create table if not exists webhook_events",
    "create table if not exists payment_records",
    "create table if not exists subscription_records",
    "create table if not exists refund_records",
    "promotion_outcome",
    "promotion_coupon_id",
    "promotion_code_id"
)

$missing = @()
foreach ($snippet in $requiredSnippets)
{
    if ($content.IndexOf($snippet, [System.StringComparison]::OrdinalIgnoreCase) -lt 0)
    {
        $missing += $snippet
    }
}

if ($missing.Count -gt 0)
{
    Write-Error "Schema verification failed. Missing definitions:`n - $($missing -join "`n - ")"
    exit 1
}

Write-Output "Schema verification passed."
