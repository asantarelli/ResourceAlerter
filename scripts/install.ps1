<#
.SYNOPSIS
    Installs ResourceAlerter as a Windows service (LocalSystem, Automatic startup).

.DESCRIPTION
    Run this from an elevated PowerShell prompt, from the folder produced by
    `dotnet publish` (i.e. the folder that contains ResourceAlerter.exe), or point
    -PublishDir at it explicitly.

.PARAMETER PublishDir
    Folder containing ResourceAlerter.exe. Defaults to the script's own directory.

.PARAMETER ServiceName
    Windows service name. Defaults to "ResourceAlerter".

.EXAMPLE
    .\install.ps1
    .\install.ps1 -PublishDir "C:\Apps\ResourceAlerter"
#>
[CmdletBinding()]
param(
    [string]$PublishDir = $PSScriptRoot,
    [string]$ServiceName = "ResourceAlerter",
    [string]$DisplayName = "ResourceAlerter - Server Health Monitor"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an elevated (Administrator) PowerShell prompt."
    exit 1
}

$exePath = Join-Path $PublishDir "ResourceAlerter.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "ResourceAlerter.exe not found at '$exePath'. Pass -PublishDir pointing at the dotnet publish output folder."
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists. Stopping and removing it first..."
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service '$ServiceName' (LocalSystem, Automatic) pointing at '$exePath'..."
New-Service -Name $ServiceName `
    -BinaryPathName "`"$exePath`"" `
    -DisplayName $DisplayName `
    -Description "Monitors CPU, memory, temperature, PSU voltages, disk space and network connectivity, and e-mails alerts. No external dependencies." `
    -StartupType Automatic | Out-Null

# Delayed auto-start reduces the odds of a mail-send race at boot before networking is up.
sc.exe config $ServiceName start= delayed-auto | Out-Null

# LocalSystem is required so LibreHardwareMonitor's kernel driver can access hardware sensors.
sc.exe config $ServiceName obj= "LocalSystem" | Out-Null

Write-Host "Starting service '$ServiceName'..."
Start-Service -Name $ServiceName

Start-Sleep -Seconds 2
Get-Service -Name $ServiceName | Format-Table -AutoSize

Write-Host ""
Write-Host "Done. Check the 'logs' folder next to ResourceAlerter.exe for diagnostics, and confirm the 'service started' e-mail arrived."
