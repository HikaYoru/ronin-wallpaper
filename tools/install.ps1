<#
.SYNOPSIS
  Install (or update) Ronin from a release made by publish.ps1.
  Stops the running copy, keeps the old app as app.previous, copies the new one, adds a Start menu shortcut,
  points the app's update check at the releases folder, and starts it again. Your settings
  (%APPDATA%\RoninHost) are never touched.

.EXAMPLE
  .\tools\install.ps1                                            # newest release, into %LOCALAPPDATA%\Programs\Ronin
  .\tools\install.ps1 -Target "C:\Users\me\Downloads\Ronin Wallpaper"
  .\tools\install.ps1 -Zip .\releases\Ronin-0.6.0.zip -StartWithWindows
#>
param(
    [string]$Zip = "",
    [string]$Target = (Join-Path $env:LOCALAPPDATA "Programs\Ronin"),
    [switch]$StartWithWindows,
    [switch]$NoShortcut,
    [switch]$NoStart
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$releases = Join-Path $root "releases"

if (-not $Zip) {
    $vj = Join-Path $releases "version.json"
    if (-not (Test-Path $vj)) { throw "No release found. Run tools\publish.ps1 first, or pass -Zip." }
    $Zip = Join-Path $releases ((Get-Content $vj -Raw | ConvertFrom-Json).zip)
} else { $releases = Split-Path (Resolve-Path $Zip) -Parent }
$Zip = (Resolve-Path $Zip).Path
Write-Host "installing $(Split-Path $Zip -Leaf) into $Target"

# stop the running copy (it saves its settings on the way out)
$exe = Join-Path $Target "app\RoninHost.exe"
if (Test-Path $exe) { & $exe --quit; Start-Sleep -Seconds 2 }
Get-Process RoninHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Target*" } | ForEach-Object { $_.WaitForExit(15000) | Out-Null }

$unpack = Join-Path ([IO.Path]::GetTempPath()) ("ronin-install-" + [Guid]::NewGuid().ToString("N"))
Expand-Archive $Zip -DestinationPath $unpack

New-Item -ItemType Directory -Force $Target | Out-Null
$app = Join-Path $Target "app"
if (Test-Path $app) {
    $prev = Join-Path $Target "app.previous"
    if (Test-Path $prev) { Remove-Item $prev -Recurse -Force }
    Copy-Item $app $prev -Recurse
}
robocopy "$unpack\app" $app /E /NFL /NDL /NJH /NJS | Out-Null
# wallpaper files: add new or changed ones; your own images stay
robocopy "$unpack\ronin-wallpaper" (Join-Path $Target "ronin-wallpaper") /E /NFL /NDL /NJH /NJS | Out-Null
Remove-Item $unpack -Recurse -Force

# where the app looks for updates (Settings -> Display -> Updates can change it)
Set-Content (Join-Path $app "update-source.txt") $releases -Encoding utf8

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "Ronin.lnk"
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = $app; $s.Description = "Ronin wallpaper"; $s.Save()
    Write-Host "Start menu shortcut: $lnk"
}
if ($StartWithWindows) {
    Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "RoninHost" -Value "`"$exe`""
}
if (-not $NoStart) { Start-Process $exe -WorkingDirectory $app }
Write-Host "done"
