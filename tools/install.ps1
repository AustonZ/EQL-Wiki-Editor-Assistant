<#
.SYNOPSIS
Builds the app in Release and installs it where it can run on its own, outside the repository — the developer's
quick install. Testers get the real installer, built by tools/release.ps1.

.DESCRIPTION
Publishes to a staging folder, mirrors that into the install folder, copies the game's icon library beside it and
adds a Start menu shortcut. Run it again to update; it refuses while the installed copy is running, since Windows will
not replace a running executable and a half-updated install is worse than none.

Running from an install rather than `dotnet run` is what lets a build and a running copy coexist: building the repo
no longer has to fight the app you are using for its own output files.

This copy and a `dotnet run` copy keep their state (ledger, settings, caches) in %APPDATA%\EQLWikiEditorAssistant-dev,
apart from an installed release's %APPDATA%\EQLWikiEditorAssistant, so a build that changes what it records cannot
damage the release's data. Each run refreshes the dev folder from the release's, when there is one: the ledger,
settings and caches are copied over whatever the dev folder held, so work done only in a dev build lasts until the
next install. Its own errors.log and saved screenshots are left alone and none of the release's are copied.

.PARAMETER Destination
Where to install. Defaults to %LOCALAPPDATA%\EQLWikiEditorAssistant-dev, beside the release, which Velopack installs to
%LOCALAPPDATA%\EQLWikiEditorAssistant. The review browser's cache lives in it too, and survives each install.
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'EQLWikiEditorAssistant-dev')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\EQLWikiEditorAssistant.App'
. (Join-Path $PSScriptRoot 'build-version.ps1')
$exe = Join-Path $Destination 'EQLWikiEditorAssistant.App.exe'

$running = Get-Process -Name 'EQLWikiEditorAssistant.App' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Host "The installed copy is running (process $($running.Id -join ', ')). Close it and run this again." -ForegroundColor Red
    exit 1
}

# A development version, after the last release and before the next (see build-version.ps1), with the commit after
# the + so an installed copy can be traced to it.
$version = Get-DevelopmentVersion $repo (Get-ReleasedVersion $project)
$revision = Get-SourceRevision $repo
if ($revision.EndsWith('-modified')) {
    Write-Host "Note: the working tree has uncommitted changes, and they are included in this build." -ForegroundColor Yellow
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("EQLWikiEditorAssistant-publish-" + [guid]::NewGuid().ToString('N'))
try {
    Write-Host "Building $version ($revision) in Release..."
    dotnet publish $project -c Release -o $staging --nologo -v q "-p:Version=$version" "-p:SourceRevisionId=$revision"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

    # /MIR removes files a newer build no longer has. The icon library is excluded here, which also keeps /MIR from
    # deleting it, and mirrored separately below. WebView2 is the review browser's cache (AppPaths.BrowserDataDirectory),
    # which no build contains and /MIR would otherwise delete on every install.
    Write-Host "Installing to $Destination..."
    robocopy $staging $Destination /MIR /XD game_assets WebView2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the build failed (robocopy exit code $LASTEXITCODE)." }
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}

# The app finds game_assets\item_icons by walking up from its own folder, so beside the executable is enough.
# The cached icon index is stamped with the files' names and sizes, not their dates, so this copy, the repo's and the
# released app's are all recognized as the same library rather than rebuilt.
Write-Host "Copying the icon library..."
robocopy (Join-Path $repo 'game_assets\item_icons') (Join-Path $Destination 'game_assets\item_icons') `
    /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the icon library failed (robocopy exit code $LASTEXITCODE)." }

# The release's data, copied over the dev copy's so it works on real data without being able to damage it. Not a
# mirror: the dev folder's own errors.log and saved screenshots stay, and the release's are not copied — a log belongs
# to the copy that wrote it, and the screenshots are many and large.
$releaseData = Join-Path $env:APPDATA 'EQLWikiEditorAssistant'
$devData = Join-Path $env:APPDATA 'EQLWikiEditorAssistant-dev'
if (Test-Path $releaseData) {
    Write-Host "Copying the release's data to $devData..."
    robocopy $releaseData $devData /E /XF errors.log /XD debug-captures /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the release's data failed (robocopy exit code $LASTEXITCODE)." }
}
else {
    Write-Host "No release data in $releaseData, so the dev copy keeps its own."
}

# "(dev build)", because the released installer's own shortcut is "EQL Wiki Editor Assistant" and the two would
# overwrite each other.
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'EQL Wiki Editor Assistant (dev build).lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $Destination
$shortcut.Description = 'Checks EverQuest Legends item windows against eqlwiki.com'
$shortcut.Save()

Write-Host "Installed $version ($revision). Start it from the Start menu (EQL Wiki Editor Assistant (dev build)) or run:" -ForegroundColor Green
Write-Host "  $exe"
exit 0
