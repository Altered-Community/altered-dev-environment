#!/usr/bin/env pwsh
# Launch the Altered local dev environment (Aspire AppHost).
# Checks prerequisites (.NET SDK, Aspire CLI, Docker) and offers to install the
# missing ones for you. Usage: ./run.ps1 [extra aspire args]
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Confirm-Yes([string]$question) {
    $ans = Read-Host "$question [Y/n]"
    return ($ans -eq '' -or $ans -match '^(y|yes|o|oui)$')
}

# --- .NET 10 SDK ---
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET SDK is not installed." -ForegroundColor Yellow
    if (Confirm-Yes "Install the .NET 10 SDK now (winget)?") {
        winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
        $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error "The .NET SDK is still not available. Close and reopen your terminal, then re-run ./run.ps1."
        exit 1
    }
}

# --- Aspire CLI ---
if (-not (Get-Command aspire -ErrorAction SilentlyContinue)) {
    Write-Host "The Aspire CLI is not installed." -ForegroundColor Yellow
    if (Confirm-Yes "Install it now (dotnet tool install -g aspire.cli)?") {
        dotnet tool install -g aspire.cli
        $env:PATH = "$HOME\.dotnet\tools;$env:PATH"
    }
    if (-not (Get-Command aspire -ErrorAction SilentlyContinue)) {
        Write-Error "The 'aspire' CLI is still not available. Close and reopen your terminal, then re-run ./run.ps1."
        exit 1
    }
}

# --- Docker ---
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "Docker is not installed." -ForegroundColor Yellow
    if (Confirm-Yes "Install Docker Desktop now (winget)?") {
        winget install --id Docker.DockerDesktop -e --accept-source-agreements --accept-package-agreements
        Write-Host "Docker Desktop installed. Launch it once (it may ask to finish setup / reboot), then re-run ./run.ps1." -ForegroundColor Cyan
        exit 0
    }
    Write-Error "Docker is required. Install Docker Desktop and re-run ./run.ps1."
    exit 1
}
try { docker info *> $null } catch { }
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker is installed but not running. Start Docker Desktop, then re-run ./run.ps1."
    exit 1
}

Write-Host "Starting Altered dev environment (Aspire). First run builds the auth + decks images and installs PHP deps; it can take a few minutes." -ForegroundColor Cyan

# Open the Aspire dashboard automatically once it's ready. The CLI logs the
# dashboard login URL (with its one-time token) to ~/.aspire/logs/cli_*.log;
# a background job watches for it and opens the browser, leaving the interactive
# AppHost in the foreground.
$logDir = Join-Path $HOME ".aspire/logs"
$startTime = Get-Date
$opener = Start-Job -ScriptBlock {
    param($logDir, $startTime)
    $deadline = (Get-Date).AddMinutes(10)
    while ((Get-Date) -lt $deadline) {
        $log = Get-ChildItem -Path $logDir -Filter 'cli_*.log' -ErrorAction SilentlyContinue |
               Where-Object { $_.LastWriteTime -ge $startTime } |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log) {
            $m = Select-String -Path $log.FullName -Pattern 'Login to the dashboard at (https?://\S+)' -ErrorAction SilentlyContinue |
                 Select-Object -Last 1
            if ($m) { Start-Process $m.Matches[0].Groups[1].Value; break }
        }
        Start-Sleep -Milliseconds 800
    }
} -ArgumentList $logDir, $startTime

try { aspire run @args }
finally { Remove-Job $opener -Force -ErrorAction SilentlyContinue }
