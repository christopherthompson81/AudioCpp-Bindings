#Requires -Version 5.1
<#
.SYNOPSIS
    audio.cpp Native Studio - Windows installer.

.DESCRIPTION
    Publishes the app self-contained, copies the native engine and the audio
    shim beside it, and creates Start Menu and Desktop shortcuts.

    The app is a consumer of a native engine it does not build, so installing it
    is not just a publish: audiocpp.dll, the audioio shim, the model specs and
    the VAD weights all have to come along. They are laid out under the install
    directory exactly as they sit in a source checkout, which is why no code
    change is needed to find them: both native resolvers and both asset lookups
    walk up from the executable looking for external\audio.cpp\... and
    native\audioio\..., and find them there.

.PARAMETER Prefix
    Install directory. Default: %LOCALAPPDATA%\Programs\audiocpp-studio

.PARAMETER NoEngine
    Do not copy the engine. The app will then need AUDIOCPP_NATIVE_DIR set.

.PARAMETER NoShortcuts
    Do not create Start Menu or Desktop shortcuts.

.EXAMPLE
    .\install-windows.ps1
    .\install-windows.ps1 -Prefix D:\Apps\audiocpp-studio -NoShortcuts
#>
[CmdletBinding()]
param(
    [string] $Prefix = "$env:LOCALAPPDATA\Programs\audiocpp-studio",
    [switch] $NoEngine,
    [switch] $NoShortcuts
)

$ErrorActionPreference = 'Stop'

$Root      = $PSScriptRoot
$Engine    = Join-Path $Root 'external\audio.cpp'
$EngineBin = Join-Path $Engine 'build\bin'
$Exe       = 'AudioCpp.Bindings.Gui.exe'
$Shim      = Join-Path $Root 'native\audioio\build\audioio.dll'

# A multi-config CMake generator -- which is what Visual Studio gives you -- puts
# the output in a per-configuration subdirectory, so bin\ alone finds nothing on
# a tree where the library is sitting right there.
function Find-Native([string] $dir, [string] $name) {
    foreach ($config in @('', 'Release', 'RelWithDebInfo', 'MinSizeRel', 'Debug')) {
        $candidate = if ($config) { Join-Path $dir (Join-Path $config $name) }
                     else         { Join-Path $dir $name }
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

# --- Validate before writing anything -------------------------------------
# A publish takes long enough that discovering a missing engine afterwards
# wastes real time, and a half-written install directory is worse than none.
$engineDll = $null
if (-not $NoEngine) {
    $engineDll = Find-Native $EngineBin 'audiocpp.dll'
    if (-not $engineDll) {
        Write-Error @"
No engine build under $EngineBin.
Build it first, from a Developer PowerShell:
    cmake -S external\audio.cpp -B external\audio.cpp\build -DAUDIOCPP_BUILD_C_API=ON
    cmake --build external\audio.cpp\build --config Release --target audiocpp
or pass -NoEngine to install the app alone.
"@
    }
}

$shimDll = Find-Native (Split-Path -Parent $Shim) 'audioio.dll'
if (-not $shimDll) {
    Write-Error "No audioio shim under $(Split-Path -Parent $Shim). Build it with scripts\build-native.sh (or the equivalent cmake invocation)."
}

Write-Host 'Publishing the app...'
& dotnet publish (Join-Path $Root 'src\AudioCpp.Bindings.Gui\AudioCpp.Bindings.Gui.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $Prefix `
    --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed with exit code $LASTEXITCODE" }

# Symbols are for debugging a build tree, not for an install.
Get-ChildItem -Path $Prefix -Filter '*.pdb' -File | Remove-Item -Force

Write-Host 'Installing the audio shim...'
$shimDest = Join-Path $Prefix 'native\audioio\build'
New-Item -ItemType Directory -Force -Path $shimDest | Out-Null
Copy-Item $shimDll $shimDest -Force

if (-not $NoEngine) {
    Write-Host 'Installing the engine...'
    $engineDest = Join-Path $Prefix 'external\audio.cpp\build\bin'
    New-Item -ItemType Directory -Force -Path $engineDest | Out-Null
    # Whatever sits beside audiocpp.dll: a CUDA build brings its own runtime DLLs
    # and the app fails to start without them.
    Copy-Item (Join-Path (Split-Path -Parent $engineDll) '*.dll') $engineDest -Force

    # Without the specs the catalogue is empty and any safetensors package fails
    # to load with "model spec not found for family"; without the VAD weights the
    # app silently falls back to fixed chunking, which is worse than an error.
    Write-Host 'Installing model specs and VAD weights...'
    $specsDest = Join-Path $Prefix 'external\audio.cpp'
    New-Item -ItemType Directory -Force -Path (Join-Path $specsDest 'assets\framework\models') | Out-Null
    Copy-Item (Join-Path $Engine 'model_specs') $specsDest -Recurse -Force
    Copy-Item (Join-Path $Engine 'assets\framework\models\silero_vad') `
              (Join-Path $specsDest 'assets\framework\models') -Recurse -Force
}

if (-not $NoShortcuts) {
    Write-Host 'Creating shortcuts...'
    # The icon comes from the .exe itself: ApplicationIcon puts it in the PE
    # resource, which is what Explorer, the Start Menu and the taskbar all read.
    $target   = Join-Path $Prefix $Exe
    $shell    = New-Object -ComObject WScript.Shell
    $startDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    foreach ($dir in @($startDir, [Environment]::GetFolderPath('Desktop'))) {
        $link = $shell.CreateShortcut((Join-Path $dir 'audio.cpp Native Studio.lnk'))
        $link.TargetPath       = $target
        $link.WorkingDirectory = $Prefix
        $link.IconLocation     = $target
        $link.Description      = 'Local audio model studio: transcription, diarization, separation and speech'
        $link.Save()
    }
}

Write-Host ''
Write-Host "Installed to $Prefix"
Write-Host "Launch it from the Start Menu, or run: $(Join-Path $Prefix $Exe)"
if ($NoEngine) {
    Write-Host ''
    Write-Host '  Installed without the engine: set AUDIOCPP_NATIVE_DIR to a build''s bin'
    Write-Host '  before launching, or the app will not find audiocpp.dll.'
}
