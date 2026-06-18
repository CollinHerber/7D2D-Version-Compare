[CmdletBinding()]
param(
    [string]$OutputPath = $env:GITHUB_OUTPUT,
    [string]$NotesPath = "artifacts/release-notes.md"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-GitHead {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    & git rev-parse --verify HEAD > $null 2> $null
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    return $exitCode -eq 0
}

function Get-LatestVersionTag {
    if (-not (Test-GitHead)) {
        return $null
    }

    $tags = & git tag --list "v[0-9]*.[0-9]*.[0-9]*" --sort=-v:refname
    foreach ($tag in $tags) {
        if ($tag -match "^v\d+\.\d+\.\d+$") {
            return $tag
        }
    }

    return $null
}

function Get-CommitRecords {
    param([string]$Range)

    if (-not (Test-GitHead)) {
        return @()
    }

    $arguments = @("log", "--format=%H%x1f%s%x1f%b%x1e")
    if (-not [string]::IsNullOrWhiteSpace($Range)) {
        $arguments += $Range
    }

    $rawLog = (& git @arguments) -join "`n"
    if ([string]::IsNullOrWhiteSpace($rawLog)) {
        return @()
    }

    $recordSeparator = [string][char]0x1e
    $fieldSeparator = [string][char]0x1f
    $records = @()

    foreach ($record in $rawLog.Split($recordSeparator, [StringSplitOptions]::RemoveEmptyEntries)) {
        $fields = [regex]::Split($record.Trim(), [regex]::Escape($fieldSeparator), 3)
        if ($fields.Count -lt 2) {
            continue
        }

        $records += [pscustomobject]@{
            Hash = $fields[0]
            Subject = $fields[1]
            Body = if ($fields.Count -ge 3) { $fields[2] } else { "" }
        }
    }

    return $records
}

function ConvertTo-ConventionalCommit {
    param($Record)

    $pattern = "^(?<type>feat|fix|chore)(?:\([^)]+\))?(?<breaking>!)?:\s*(?<description>.+)$"
    $match = [regex]::Match($Record.Subject, $pattern)
    if (-not $match.Success) {
        return $null
    }

    $isBreaking = $match.Groups["breaking"].Success `
        -or $Record.Body -match "(?m)^BREAKING CHANGE:"

    return [pscustomobject]@{
        Type = $match.Groups["type"].Value
        Description = $match.Groups["description"].Value.Trim()
        Hash = $Record.Hash
        ShortHash = $Record.Hash.Substring(0, [Math]::Min(7, $Record.Hash.Length))
        IsBreaking = $isBreaking
    }
}

function Get-VersionParts {
    param([string]$Tag)

    if ($Tag -notmatch "^v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$") {
        throw "Version tag '$Tag' must match vMAJOR.MINOR.PATCH."
    }

    return [pscustomobject]@{
        Major = [int]$Matches["major"]
        Minor = [int]$Matches["minor"]
        Patch = [int]$Matches["patch"]
    }
}

function New-ReleaseNotes {
    param(
        [string]$Tag,
        [string]$PreviousTag,
        [array]$Commits
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("## $Tag")
    $lines.Add("")

    if (-not [string]::IsNullOrWhiteSpace($PreviousTag)) {
        $lines.Add("Changes since $PreviousTag.")
        $lines.Add("")
    }

    if ($Commits.Count -eq 0) {
        $lines.Add("- Initial release.")
        return $lines
    }

    $sections = @(
        @{ Type = "feat"; Title = "Features" },
        @{ Type = "fix"; Title = "Fixes" },
        @{ Type = "chore"; Title = "Chores" }
    )

    foreach ($section in $sections) {
        $sectionCommits = @($Commits | Where-Object { $_.Type -eq $section.Type })
        if ($sectionCommits.Count -eq 0) {
            continue
        }

        $lines.Add("### $($section.Title)")
        foreach ($commit in $sectionCommits) {
            $breakingText = if ($commit.IsBreaking) { " **BREAKING**" } else { "" }
            $lines.Add("- $($commit.Description) ($($commit.ShortHash))$breakingText")
        }

        $lines.Add("")
    }

    return $lines
}

$latestTag = Get-LatestVersionTag
$range = if ([string]::IsNullOrWhiteSpace($latestTag)) { "" } else { "$latestTag..HEAD" }
$commits = @(Get-CommitRecords -Range $range | ForEach-Object { ConvertTo-ConventionalCommit $_ } | Where-Object { $_ -ne $null })

$shouldRelease = $true
if ([string]::IsNullOrWhiteSpace($latestTag)) {
    $version = "0.1.0"
}
elseif ($commits.Count -eq 0) {
    $parts = Get-VersionParts -Tag $latestTag
    $version = "$($parts.Major).$($parts.Minor).$($parts.Patch)"
    $shouldRelease = $false
}
else {
    $parts = Get-VersionParts -Tag $latestTag
    if (@($commits | Where-Object { $_.IsBreaking }).Count -gt 0) {
        $parts.Major += 1
        $parts.Minor = 0
        $parts.Patch = 0
    }
    elseif (@($commits | Where-Object { $_.Type -eq "feat" }).Count -gt 0) {
        $parts.Minor += 1
        $parts.Patch = 0
    }
    else {
        $parts.Patch += 1
    }

    $version = "$($parts.Major).$($parts.Minor).$($parts.Patch)"
}

$tag = "v$version"
$notes = New-ReleaseNotes -Tag $tag -PreviousTag $latestTag -Commits $commits
$notesDirectory = Split-Path $NotesPath -Parent
if (-not [string]::IsNullOrWhiteSpace($notesDirectory)) {
    New-Item -ItemType Directory -Force -Path $notesDirectory | Out-Null
}

Set-Content -Path $NotesPath -Value $notes -Encoding UTF8

$shouldReleaseValue = $shouldRelease.ToString().ToLowerInvariant()
Write-Output "version=$version"
Write-Output "tag=$tag"
Write-Output "should_release=$shouldReleaseValue"
Write-Output "notes_path=$NotesPath"

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    Add-Content -Path $OutputPath -Value "version=$version"
    Add-Content -Path $OutputPath -Value "tag=$tag"
    Add-Content -Path $OutputPath -Value "should_release=$shouldReleaseValue"
    Add-Content -Path $OutputPath -Value "notes_path=$NotesPath"
}
