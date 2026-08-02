# Brings up the demo stack and opens the browser at http://localhost:7002.
# PowerShell equivalent of demo-start.sh for Windows (Docker Desktop, no bash/WSL needed).
#   Run from a PowerShell prompt:  .\docker\demo-start.ps1
$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot 'compose')

if (-not (Test-Path '.env')) {
    Copy-Item '.env.example' '.env'
    Write-Host 'Created .env from .env.example.'
}

docker compose -f docker-compose.demo.yml up -d --build

$url = 'http://localhost:7002'
Write-Host "Waiting for DataManager at $url ..."
for ($i = 0; $i -lt 90; $i++) {
    try {
        if ((Invoke-WebRequest -Uri "$url/health/ready" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) {
            break
        }
    } catch { }
    Start-Sleep -Seconds 2
}

Start-Process $url
Write-Host "Demo is up: $url - pick a role on the login page."
