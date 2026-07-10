<#
.SYNOPSIS
    Stops and removes the ResourceAlerter Windows service.

.EXAMPLE
    .\uninstall.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "ResourceAlerter"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an elevated (Administrator) PowerShell prompt."
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed. Nothing to do."
    exit 0
}

if ($existing.Status -ne 'Stopped') {
    Write-Host "Stopping service '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force
}

Write-Host "Removing service '$ServiceName'..."
sc.exe delete $ServiceName | Out-Null

Write-Host "Done. The application folder and its logs/appsettings.json were left in place."
