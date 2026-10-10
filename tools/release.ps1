<#
.SYNOPSIS
Builds a release: a self-contained build with the icon library beside it, packaged by Velopack into an installer.

.DESCRIPTION
Everything lands in artifacts\ (gitignored): artifacts\publish is the app as installed, and artifacts\releases holds
what testers download — EQLWikiEditorAssistant-win-Setup.exe — plus the package (.nupkg) and feed files
(releases.win.json) that an installed copy's Update button downloads. Every release must carry them, or installed
copies are never offered it.

The build is self-contained, so a tester needs no .NET install, and Setup installs the WebView2 runtime if Windows lacks
it. It installs per user, with no administrator prompt, to %LOCALAPPDATA%\EQLWikiEditorAssistant. The app's own state
stays in %APPDATA%\EQLWikiEditorAssistant, so uninstalling or reinstalling never touches a tester's history or settings.

With -Draft it also creates a draft GitHub release, tagged v<version>, carrying those files, for a person to read over
and publish. A draft needs a clean working tree, so what is released is exactly a commit. Uploading goes through the
GitHub CLI, which keeps its own credential: vpk's own upload would need the token on the command line.

.PARAMETER Draft
Also create the draft GitHub release.
#>
param([switch]$Draft)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\EQLWikiEditorAssistant.App'
$repoUrl = 'https://github.com/AustonZ/EQL-Wiki-Editor-Assistant'
$packId = 'EQLWikiEditorAssistant'
$publish = Join-Path $repo 'artifacts\publish'
$releases = Join-Path $repo 'artifacts\releases'

# Windows PowerShell makes a native command's stderr an error, and under 'Stop' that ends the script even when the
# stderr is redirected. For a command whose failure is an answer ("no such release") rather than a fault, the exit code
# is what decides, so it runs under 'Continue' and returns that.
function Get-ExitCode([scriptblock]$Command) {
    $ErrorActionPreference = 'Continue'
    & $Command 2>&1 | Out-Null
    $LASTEXITCODE
}

$version = (dotnet msbuild $project -getProperty:Version -nologo).Trim()
$product = (dotnet msbuild $project -getProperty:Product -nologo).Trim()
$tag = "v$version"

$revision = (git -C $repo rev-parse --short HEAD).Trim()
if (git -C $repo status --porcelain) {
    if ($Draft) { throw 'Commit first: a release is built from a clean working tree, so it is exactly one commit.' }
    $revision += '-modified'
    Write-Host 'Note: the working tree has uncommitted changes, and they are included in this build.' -ForegroundColor Yellow
}
if ($Draft) {
    if ((Get-ExitCode { gh release view $tag --repo $repoUrl }) -eq 0) { throw "GitHub already has a release tagged $tag. Raise <Version> in the app's project file." }
    # The tag is created when the draft is published, on this commit, so GitHub must already have it.
    git -C $repo fetch --quiet origin
    if (-not (git -C $repo branch -r --contains HEAD)) { throw 'Push first: the release is tagged on this commit, and GitHub does not have it yet.' }
}
$commit = (git -C $repo rev-parse HEAD).Trim()

foreach ($dir in $publish, $releases) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

Write-Host "Building $product $version ($revision), self-contained..."
dotnet publish $project -c Release -r win-x64 --self-contained -o $publish --nologo -v q "-p:SourceRevisionId=$revision"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

# A self-contained build ships the .NET runtime and WPF, so their licence and notices ship too. Found through the
# versions the build itself recorded, since those follow the SDK; the app's other notices come from its project file.
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
$runtimeConfig = Get-Content (Join-Path $publish 'EQLWikiEditorAssistant.App.runtimeconfig.json') -Raw | ConvertFrom-Json
# Made here, not assumed: the build used to create it by copying ONNX Runtime's and Skia's notices, which left with the
# text-recognition model, and nothing else the build ships goes in it.
New-Item -ItemType Directory -Force (Join-Path $publish 'licenses') | Out-Null
$runtimeNotices = @{
    'Microsoft.NETCore.App'        = @(@('LICENSE.TXT', 'dotnet-runtime-LICENSE.txt'),
                                       @('THIRD-PARTY-NOTICES.TXT', 'dotnet-runtime-THIRD-PARTY-NOTICES.txt'))
    'Microsoft.WindowsDesktop.App' = @(, @('LICENSE', 'dotnet-windowsdesktop-LICENSE.txt'))
}
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $pack = Join-Path $nugetRoot ("$($framework.name).Runtime.win-x64\$($framework.version)".ToLowerInvariant())
    foreach ($file in $runtimeNotices[$framework.name]) {
        $source = Join-Path $pack $file[0]
        if (-not (Test-Path $source)) { throw "The runtime's notice $source is missing, so the release cannot ship it." }
        Copy-Item $source (Join-Path $publish "licenses\$($file[1])")
    }
}

# The app finds game_assets\item_icons by walking up from its own folder, so beside the executable is enough.
Write-Host 'Adding the icon library...'
robocopy (Join-Path $repo 'game_assets\item_icons') (Join-Path $publish 'game_assets\item_icons') `
    /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the icon library failed (robocopy exit code $LASTEXITCODE)." }

# The previous release, so Velopack can build a delta update from it. Best effort: there is none before the first
# release, and a private repository refuses an anonymous download; a full package works either way.
Push-Location $repo
try {
    if ((Get-ExitCode { dotnet vpk download github --repoUrl $repoUrl --pre --outputDir $releases }) -ne 0) { Write-Host 'No previous release downloaded, so no delta package (expected for the first).' }

    Write-Host 'Packaging with Velopack...'
    $pack = @(
        'vpk', 'pack',
        '--packId', $packId, '--packVersion', $version, '--packTitle', $product, '--runtime', 'win-x64',
        '--packAuthors', 'EQL Wiki Editor Assistant contributors',
        '--packDir', $publish, '--mainExe', 'EQLWikiEditorAssistant.App.exe',
        '--icon', (Join-Path $project 'app.ico'),
        '--framework', 'webview2',
        '--outputDir', $releases
    )
    dotnet @pack
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (exit code $LASTEXITCODE)." }
}
finally {
    Pop-Location
}

$setup = Join-Path $releases "$packId-win-Setup.exe"
if (-not (Test-Path $setup)) { throw "Velopack did not produce $setup." }
Write-Host "Built $product $version ($revision):" -ForegroundColor Green
Get-ChildItem $releases | ForEach-Object { Write-Host ("  {0,-55} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

if ($Draft) {
    # This version's files only: the previous release's package, downloaded above for the delta, is already published.
    $files = Get-ChildItem $releases | Where-Object { $_.Name -notlike '*.nupkg' -or $_.Name -like "*-$version-*" }
    # @(...) around the whole if: an if returning a one-item array unrolls it to a string, and splatting a string
    # passes it one character at a time.
    $prerelease = @(if ($version.Contains('-')) { '--prerelease' })
    gh release create $tag --repo $repoUrl --draft @prerelease --target $commit --title "$product $version" `
        --notes "Draft: replace with the release notes before publishing." @($files.FullName)
    if ($LASTEXITCODE -ne 0) { throw "Creating the draft release failed (exit code $LASTEXITCODE)." }
    Write-Host "Draft release $tag created. Read it over on GitHub, write its notes, and publish it there." -ForegroundColor Green
}
