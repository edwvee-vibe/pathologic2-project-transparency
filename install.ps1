# Install: copies dist\Pathologic2ProjectTransparency.dll to <Game>\BepInEx\plugins\pathologic2-project-transparency\.
# Game files are not modified. -Uninstall removes the plugin folder (the config BepInEx\config\pathologic2.projecttransparency.cfg stays).
# Example: powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 [-GameDir ...] [-Uninstall]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Pathologic",
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $GameDir 'BepInEx\plugins\pathologic2-project-transparency'

if ($Uninstall) {
    if (Test-Path $target) {
        Remove-Item -Recurse -Force $target -Confirm:$false
        "Removed: $target"
    } else {
        "Not installed: $target"
    }
    return
}

if (-not (Test-Path "$GameDir\BepInEx\core\BepInEx.dll")) { throw "BepInEx 5 not found in the game folder: $GameDir" }
$dll = Join-Path $root 'dist\Pathologic2ProjectTransparency.dll'
if (-not (Test-Path $dll)) { throw "$dll not found; run build.ps1 first" }

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $dll $target -Force
"Installed: $target\Pathologic2ProjectTransparency.dll"
