<#
.SYNOPSIS
Builds the app in Release and installs it where it can run on its own, outside the repository.

.DESCRIPTION
Publishes to a staging folder, mirrors that into the install folder, copies the game's icon library beside it and
adds a Start menu shortcut. Run it again to update; it refuses while the installed copy is running, since Windows will
not replace a running executable and a half-updated install is worse than none.

Running from an install rather than `dotnet run` is what lets a build and a running copy coexist: building the repo
no longer has to fight the app you are using for its own output files.

The app's own state (ledger, settings, caches) lives in %APPDATA%\EQLWikiAssistant regardless of where it runs, so
the installed copy and a `dotnet run` copy share it.

.PARAMETER Destination
Where to install. Defaults to %LOCALAPPDATA%\Programs\EQLWikiAssistant, where per-user programs go.
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\EQLWikiAssistant')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $Destination 'EQLWikiAssistant.App.exe'

$running = Get-Process -Name 'EQLWikiAssistant.App' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Host "The installed copy is running (process $($running.Id -join ', ')). Close it and run this again." -ForegroundColor Red
    exit 1
}

# Stamped into the build's informational version, so an installed copy can be traced to the commit it came from.
$revision = (git -C $repo rev-parse --short HEAD).Trim()
if (git -C $repo status --porcelain) {
    $revision += '-modified'
    Write-Host "Note: the working tree has uncommitted changes, and they are included in this build." -ForegroundColor Yellow
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("EQLWikiAssistant-publish-" + [guid]::NewGuid().ToString('N'))
try {
    Write-Host "Building $revision in Release..."
    dotnet publish (Join-Path $repo 'src\EQLWikiAssistant.App') -c Release -o $staging --nologo -v q `
        "-p:SourceRevisionId=$revision"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

    # /MIR removes files a newer build no longer has. The icon library is excluded here, which also keeps /MIR from
    # deleting it, and mirrored separately below.
    Write-Host "Installing to $Destination..."
    robocopy $staging $Destination /MIR /XD game_assets /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the build failed (robocopy exit code $LASTEXITCODE)." }
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}

# The app finds game_assets\item_icons by walking up from its own folder, so beside the executable is enough.
# robocopy keeps each file's write time, which keeps the cached icon index valid: it is stamped with the file count
# and the newest write time, so an identical copy elsewhere is recognised as the same library rather than rebuilt.
Write-Host "Copying the icon library..."
robocopy (Join-Path $repo 'game_assets\item_icons') (Join-Path $Destination 'game_assets\item_icons') `
    /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the icon library failed (robocopy exit code $LASTEXITCODE)." }

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'EQL Wiki Assistant.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $Destination
$shortcut.Description = 'Checks EverQuest Legends item windows against eqlwiki.com'
$shortcut.Save()

Write-Host "Installed $revision. Start it from the Start menu (EQL Wiki Assistant) or run:" -ForegroundColor Green
Write-Host "  $exe"
exit 0
