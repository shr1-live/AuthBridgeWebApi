# Creates or resets the local LocalDB database with the synthetic fixtures.
# Windows only. Deletes AuthBridge rows in the LOCAL database only (the tool refuses remote resets).
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
dotnet build tools/AuthBridge.DbTool -v q
dotnet run --project tools/AuthBridge.DbTool --no-build -- migrate --provider SqlServer
dotnet run --project tools/AuthBridge.DbTool --no-build -- seed --provider SqlServer --reset
