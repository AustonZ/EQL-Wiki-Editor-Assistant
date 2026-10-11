<#
.SYNOPSIS
The version a build carries, shared by install.ps1 and release.ps1 so the two cannot drift apart. Dot-source it.

.DESCRIPTION
The app's project file holds the last released version (<Version>). A release is built at exactly that version; every
other build gets a development version that sorts after it and before the next release, so Velopack packages a local
build and an installed copy is still offered the next real release:

    1.0.0-alpha.4  <  1.0.0-alpha.4.dev.170  <  1.0.0-alpha.4.dev.171  <  1.0.0-alpha.5

The number is the count of commits on HEAD, which on this repository's linear history rises by one with every commit.
A stable version cannot take a suffix that sorts after it (1.0.0-dev.N comes *before* 1.0.0), so after one the next
patch is used instead: 1.0.0 gives 1.0.1-dev.N.

The commit itself goes in the build metadata after the +, marked -modified when the working tree has uncommitted
changes; a development number is shared by every build from the same commit, so that is what tells them apart.
#>

function Get-ReleasedVersion([string]$Project) {
    (dotnet msbuild $Project -getProperty:Version -nologo).Trim()
}

function Get-DevelopmentVersion([string]$Repo, [string]$Released) {
    $count = (git -C $Repo rev-list --count HEAD).Trim()
    if ($Released.Contains('-')) { return "$Released.dev.$count" }

    $parts = $Released.Split('.')
    "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)-dev.$count"
}

# The commit a build comes from, as the app's informational version carries it after the +.
function Get-SourceRevision([string]$Repo) {
    $revision = (git -C $Repo rev-parse --short HEAD).Trim()
    if (git -C $Repo status --porcelain) { $revision += '-modified' }
    $revision
}
