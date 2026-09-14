#Requires -Version 5.1
<#
.SYNOPSIS
    audio.cpp Native Studio - Windows uninstaller.

.PARAMETER Prefix
    Install directory to remove. Default: %LOCALAPPDATA%\Programs\audiocpp-studio

.PARAMETER Purge
    Also remove settings and the voice library (%APPDATA%\audiocpp-studio).

.NOTES
    Downloaded models are never touched: they live in the models root you chose,
    are large, and are not this uninstaller's to delete.

.EXAMPLE
    .\uninstall-windows.ps1
    .\uninstall-windows.ps1 -Purge
#>
[CmdletBinding()]
param(
    [string] $Prefix = "$env:LOCALAPPDATA\Programs\audiocpp-studio",
    [switch] $Purge
)

$ErrorActionPreference = 'Stop'

$Exe     = 'AudioCpp.Bindings.Gui.exe'
$Config  = Join-Path $env:APPDATA 'audiocpp-studio'
$Link    = 'audio.cpp Native Studio.lnk'
$removed = $false

if (Test-Path $Prefix) {
    # Refuse to delete something that is not ours. -Prefix takes an arbitrary
    # path and Remove-Item -Recurse does not ask twice, so check for the
    # executable the installer put there before removing the directory.
    if (Test-Path (Join-Path $Prefix $Exe)) {
        Write-Host "Removing $Prefix ..."
        Remove-Item $Prefix -Recurse -Force
        $removed = $true
    } else {
        Write-Error "$Prefix does not look like an install of this app (no $Exe in it); refusing to delete it."
    }
}

foreach ($dir in @((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
                   [Environment]::GetFolderPath('Desktop'))) {
    $path = Join-Path $dir $Link
    if (Test-Path $path) {
        Write-Host "Removing $path ..."
        Remove-Item $path -Force
        $removed = $true
    }
}

if ($Purge -and (Test-Path $Config)) {
    Write-Host "Purging settings at $Config ..."
    Remove-Item $Config -Recurse -Force
    $removed = $true
}

if ($removed) {
    Write-Host 'Done.'
    if (-not $Purge -and (Test-Path $Config)) {
        Write-Host "Settings kept at $Config (-Purge removes them)."
    }
} else {
    Write-Host "Nothing to uninstall at $Prefix"
}
