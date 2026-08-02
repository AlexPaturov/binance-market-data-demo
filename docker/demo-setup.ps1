#Requires -RunAsAdministrator
# One-shot bootstrap for a clean Windows machine: checks prerequisites, installs what is
# missing (WSL2, Docker Desktop), then launches the demo. Idempotent — if a reboot is
# required, it says so; just run the script again afterwards and it continues.
#
#   Right-click PowerShell -> Run as Administrator, then:
#     powershell -ExecutionPolicy Bypass -File .\docker\demo-setup.ps1
$ErrorActionPreference = 'Stop'

function Test-DockerReady {
    try { docker info *> $null; return ($LASTEXITCODE -eq 0) } catch { return $false }
}

Write-Host '== Demo setup =='

# --- 1. CPU virtualization (BIOS/UEFI). Cannot be toggled from the OS. ---
$vt = (Get-CimInstance Win32_Processor | Select-Object -First 1).VirtualizationFirmwareEnabled
if ($vt -eq $false) {
    Write-Warning 'CPU virtualization (VT-x / AMD-V) appears disabled in BIOS/UEFI.'
    Write-Warning 'Enable it in firmware, then re-run this script. Continuing anyway...'
}

$needReboot = $false

# --- 2. WSL2 (Docker Desktop backend) ---
$wslOk = $false
try { wsl --status *> $null; $wslOk = ($LASTEXITCODE -eq 0) } catch { $wslOk = $false }
if (-not $wslOk) {
    Write-Host 'Installing WSL2...'
    wsl --install --no-distribution
    $needReboot = $true
} else {
    Write-Host 'WSL2: already present.'
}

# --- 3. Docker Desktop ---
$dockerCli = Get-Command docker -ErrorAction SilentlyContinue
$dockerExe = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
if (-not $dockerCli -and -not (Test-Path $dockerExe)) {
    Write-Host 'Installing Docker Desktop...'
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        winget install -e --id Docker.DockerDesktop `
            --accept-source-agreements --accept-package-agreements
    } else {
        $installer = Join-Path $env:TEMP 'DockerDesktopInstaller.exe'
        Write-Host 'winget not found, downloading installer...'
        Invoke-WebRequest 'https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe' `
            -OutFile $installer
        Start-Process $installer -ArgumentList 'install', '--quiet', '--accept-license' -Wait
    }
    $needReboot = $true
} else {
    Write-Host 'Docker Desktop: already installed.'
}

# --- 4. Reboot gate: WSL2 / Docker install needs a restart before the engine works ---
if ($needReboot) {
    Write-Host ''
    Write-Host 'System components installed. Reboot the PC, then run this script again to continue.'
    exit 0
}

# --- 5. Make sure the Docker engine is running ---
if (-not (Test-DockerReady)) {
    if (Test-Path $dockerExe) {
        Write-Host 'Starting Docker Desktop, waiting for the engine...'
        Start-Process $dockerExe
    }
    $up = $false
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-DockerReady) { $up = $true; break }
        Start-Sleep -Seconds 5
    }
    if (-not $up) {
        throw 'Docker engine did not come up. Check virtualization in BIOS and that Docker Desktop started.'
    }
}
Write-Host 'Docker engine is running.'

# --- 6. Launch the demo ---
& (Join-Path $PSScriptRoot 'demo-start.ps1')
