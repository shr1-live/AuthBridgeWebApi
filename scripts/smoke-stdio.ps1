# Runs the official-SDK smoke client against the local stdio MCP host (LocalDB, Development).
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
dotnet build src/AuthBridge.Mcp -v q
dotnet build tools/AuthBridge.SmokeClient -v q
dotnet run --project tools/AuthBridge.SmokeClient --no-build -- stdio
