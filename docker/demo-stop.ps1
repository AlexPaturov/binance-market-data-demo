# Stops the demo stack.
#   .\docker\demo-stop.ps1            # keep data
#   .\docker\demo-stop.ps1 -Volumes   # also remove volumes (seed reloads on next start)
#   .\docker\demo-stop.ps1 -Purge     # also remove volumes AND locally built demo images
param([switch]$Volumes, [switch]$Purge)
$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot 'compose')

$cmdArgs = @('-f', 'docker-compose.demo.yml', 'down', '--timeout', '30')
if ($Volumes -or $Purge) {
    $cmdArgs += '--volumes'
    Write-Host 'Removing volumes.'
}
docker compose @cmdArgs

if ($Purge) {
    Write-Host 'Removing built demo images.'
    docker image rm -f bdc/datamanager:demo bdc/worker:demo bdc/postgres-cron:16 2> $null
}
