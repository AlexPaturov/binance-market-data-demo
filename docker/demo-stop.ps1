# Stops the demo stack. Use -Volumes to also remove volumes (seed reloads on next start).
#   .\docker\demo-stop.ps1        # keep data
#   .\docker\demo-stop.ps1 -Volumes
param([switch]$Volumes)
$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot 'compose')

$cmdArgs = @('-f', 'docker-compose.demo.yml', 'down', '--timeout', '30')
if ($Volumes) {
    $cmdArgs += '--volumes'
    Write-Host 'Removing volumes - seed will reload on next start.'
}

docker compose @cmdArgs
