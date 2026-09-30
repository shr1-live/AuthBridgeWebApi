# Runs the smoke client against a running API's /mcp endpoint.
# Local: gets a Development token for the seeded tenant A coordinator from /dev/token.
# Remote: set $env:AUTHBRIDGE_ACCESS_TOKEN to a real Supabase access token first and pass -Url.
param([string]$Url = 'http://localhost:5243/mcp')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (-not $env:AUTHBRIDGE_ACCESS_TOKEN) {
  $base = $Url -replace '/mcp$', ''
  $body = '{"subjectId":"11111111-1111-4111-8111-111111111111"}'
  $env:AUTHBRIDGE_ACCESS_TOKEN = (Invoke-RestMethod -Method Post -Uri "$base/dev/token" -ContentType 'application/json' -Body $body).accessToken
}
dotnet build tools/AuthBridge.SmokeClient -v q
dotnet run --project tools/AuthBridge.SmokeClient --no-build -- http --url $Url
