<#
.SYNOPSIS
  Build a Ronin release: releases\Ronin-<version>.zip (app\ + ronin-wallpaper\) and releases\version.json,
  which installed copies read to offer the update (tray -> Install update).

  -Public makes the build you share on GitHub: self-contained (no .NET install needed), its update check points at
  the GitHub Releases page, only images that are free to share are included, and a README is put next to the folders.
  Upload BOTH files it makes (the zip and version.json) to the GitHub release.

.EXAMPLE
  .\tools\publish.ps1                      # release the version in RoninHost.csproj (for this PC)
  .\tools\publish.ps1 -Bump -Notes "Fixes" # next patch version (0.6.0 -> 0.6.1), with release notes
  .\tools\publish.ps1 -Version 0.7.0
  .\tools\publish.ps1 -Public -Bump -Notes "What's new"   # a build to share on GitHub (into releases\public)
#>
param(
    [string]$Version = "",
    [switch]$Bump,
    [string]$Notes = "",
    [string]$Out = "",
    [switch]$Public,
    [string]$UpdateUrl = "https://github.com/HikaYoru/ronin-wallpaper/releases/latest/download"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "ronin-host\RoninHost.csproj"
if (-not $Out) { $Out = if ($Public) { Join-Path $root "releases\public" } else { Join-Path $root "releases" } }

# version: given, bumped, or the project's
$text = [IO.File]::ReadAllText($proj)
$current = [regex]::Match($text, "<Version>([^<]+)</Version>").Groups[1].Value
if (-not $Version) {
    $Version = $current
    if ($Bump) { $v = [Version]$current; $Version = "{0}.{1}.{2}" -f $v.Major, $v.Minor, ($v.Build + 1) }
}
if ($Version -ne $current) {
    [IO.File]::WriteAllText($proj, $text.Replace("<Version>$current</Version>", "<Version>$Version</Version>"))
    Write-Host "version $current -> $Version"
}

$stage = Join-Path ([IO.Path]::GetTempPath()) "ronin-publish-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\app" | Out-Null

Write-Host "building $Version$(if ($Public) { ' (public, self-contained)' })..."
if ($Public) {
    dotnet publish $proj -c Release -r win-x64 --self-contained true -o "$stage\app" --nologo -v quiet
} else {
    dotnet publish $proj -c Release --self-contained false -o "$stage\app" --nologo -v quiet
}
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Get-ChildItem "$stage\app" -Filter *.pdb | Remove-Item -Force

# the wallpaper package (background images, page) next to the app, as the app expects
$wp = Join-Path $root "ronin-wallpaper"
$exclude = @("zz-*")
# third-party art that can't be redistributed stays out of shared builds (it is listed in .gitignore too)
if ($Public) { $exclude += "throne.webp" }
robocopy $wp "$stage\ronin-wallpaper" /E /XF $exclude /NFL /NDL /NJH /NJS | Out-Null

$items = @("$stage\app", "$stage\ronin-wallpaper")
if ($Public) {
    # installed copies check GitHub for updates
    Set-Content "$stage\app\update-source.txt" $UpdateUrl -Encoding utf8
    Copy-Item (Join-Path $root "tools\README-FIRST.txt") "$stage\README-FIRST.txt"
    Copy-Item (Join-Path $root "LICENSE") "$stage\LICENSE.txt"
    Set-Content "$stage\Start Ronin.cmd" "@start `"`" `"%~dp0app\RoninHost.exe`"" -Encoding ascii
    $items += "$stage\README-FIRST.txt", "$stage\LICENSE.txt", "$stage\Start Ronin.cmd"
}

New-Item -ItemType Directory -Force $Out | Out-Null
$zipName = "Ronin-$Version.zip"
$zip = Join-Path $Out $zipName
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $items -DestinationPath $zip -CompressionLevel Optimal
@{ version = $Version; zip = $zipName; notes = $Notes; date = (Get-Date -Format "yyyy-MM-dd") } |
    ConvertTo-Json | Set-Content (Join-Path $Out "version.json") -Encoding utf8
Remove-Item $stage -Recurse -Force

Write-Host "released $Version -> $zip"
if ($Public) { Write-Host "Upload both $zipName and version.json to a new GitHub release (tag v$Version)." }
exit 0 # robocopy's "files copied" code (1) isn't a failure
