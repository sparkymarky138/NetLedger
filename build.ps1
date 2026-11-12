# Builds NetLedger.exe: one self-contained file that runs on Windows 10 (1607+) / 11 without .NET installed.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'publish'

dotnet publish (Join-Path $root 'src\NetLedger.csproj') -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $out 'NetLedger.exe'
Get-ChildItem $out | Where-Object { $_.Name -ne 'NetLedger.exe' } | Remove-Item -Force -Recurse
"Built {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
