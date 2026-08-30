#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Emit a CI matrix from the solution filters.

.DESCRIPTION
    A SUITE is a .slnf file. Its name is the file's name, and its runners are
    whichever projects inside it MSBuild reports as
    IsTestingPlatformApplication.

    Nothing here declares what a suite is, and that is the point. The .slnf is
    already the thing CI builds -- `dotnet build integration.slnf` -- so
    deriving the matrix from it means the projects that get built and the
    projects that get run cannot drift apart. Adding a suite is adding a .slnf.

    The runner property is asked of MSBuild rather than inferred from a name,
    because names do not discriminate: this solution's e2e filter contains
    OrderBook.Api, which is also an Exe, and OrderBook.Tests.Shared, which is
    also called *Tests. Only IsTestingPlatformApplication separates the two
    projects that can actually run tests, and it is set by the test SDK rather
    than by this repo.

    A .slnf holding no runner is not a suite and is skipped, so a filter that
    exists for some other purpose costs nothing.

    Class names come from the runner's own structured output:

        <exe> -list classes/json -noColor -noLogo

    -noColor and -noLogo matter. Without them the runner prints a banner and
    wraps its output in ANSI codes, and an earlier version of this parsed that
    text: the banner begins with a letter and contains dots, so it was admitted
    as a test class and produced matrix legs named "(64-bit" and "xUnit.net".

.PARAMETER MaxParallel
    Caps the legs PER RUNNER: "2" for every suite, or "default=2,e2e=1" to
    differ. Uncapped means one class per leg.

    That difference is the setting that matters. Classes on one leg run in a
    single process and share their collection's fixture, so packing an
    expensive suite avoids rebuilding its containers per leg. Measured on this
    repo, an e2e leg is 94-99% fixture setup.

.PARAMETER Ado
    Emit Azure DevOps' flat matrix shape instead of the per-suite arrays
    GitHub Actions consumes.
#>
[CmdletBinding()]
param(
    [string] $Solution      = 'OrderBook.slnx',
    [string] $Configuration = 'Release',
    [string] $MaxParallel   = '',
    [switch] $Ado
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Resolve-Path $Solution)

function Get-Projects {
    <# Project paths out of a .slnx (XML) or a .slnf (JSON), repo-relative. #>
    param([string] $Path)

    $paths = if ($Path -like '*.slnx') {
        ([xml](Get-Content $Path)).Solution.SelectNodes('//Project') | ForEach-Object { $_.Path }
    }
    else {
        (Get-Content $Path -Raw | ConvertFrom-Json).solution.projects
    }

    # .slnf always writes Windows separators, even on Linux.
    $paths | ForEach-Object { $_ -replace '\\', '/' }
}

$runnerCache = @{}
function Test-IsRunner {
    <#
    Whether MSBuild considers this project something that can run tests.

    Cached: the same project appears in several filters, and each miss is a
    process launch.
    #>
    param([string] $Project)

    if (-not $runnerCache.ContainsKey($Project)) {
        # A SINGLE -getProperty prints the bare value; ask for two or more and
        # it switches to a JSON envelope instead. Nothing here wants the
        # envelope, so this reads the one line and compares it.
        $value = dotnet msbuild (Join-Path $root $Project) `
            -getProperty:IsTestingPlatformApplication -nologo 2>$null

        $runnerCache[$Project] =
            ($LASTEXITCODE -eq 0) -and (($value | Select-Object -First 1) -eq 'true')
    }

    $runnerCache[$Project]
}

function Get-Caps {
    <# "2" or "default=2,e2e=1" -> a hashtable. Empty means no cap. #>
    param([string] $Spec)

    $caps = @{}
    if ([string]::IsNullOrWhiteSpace($Spec)) { return $caps }

    if ($Spec -notmatch '=') {
        $caps['default'] = [int] $Spec
        return $caps
    }

    foreach ($pair in $Spec.Split(',')) {
        $name, $value = $pair.Split('=', 2)
        $caps[$name.Trim()] = [int] $value
    }
    $caps
}

function Split-Evenly {
    <#
    Deal items round-robin into at most $Into groups.

    Round-robin rather than contiguous chunks, because chunking cannot produce
    exactly $Into groups when it does not divide the count: 6 classes at a cap
    of 5, chunked by ceil(6/5)=2, gives THREE groups -- silently using three
    runners where five were asked for. Dealing gives min(count, $Into) every
    time.
    #>
    param([string[]] $Items, [int] $Into)

    $into = if ($Into -le 0) { $Items.Count } else { [Math]::Min($Into, $Items.Count) }

    $groups = [object[]]::new($into)
    for ($i = 0; $i -lt $into; $i++) { $groups[$i] = [System.Collections.Generic.List[string]]::new() }
    for ($i = 0; $i -lt $Items.Count; $i++) { $groups[$i % $into].Add($Items[$i]) }

    # The leading comma is load-bearing. PowerShell UNROLLS a returned
    # collection into the pipeline, so `return $groups` hands back the two
    # inner groups rather than the outer array of two -- and a cap of 1 then
    # returns its single group, whose .Count is the CLASS count. That silently
    # produced two e2e legs under `-MaxParallel e2e=1`, which is the exact
    # over-parallelisation the cap exists to prevent.
    ,$groups
}

function Get-Runner {
    <# The built runner for a project: its executable and its test classes. #>
    param([string] $Project)

    $name = [IO.Path]::GetFileNameWithoutExtension($Project)

    # The framework folder is globbed rather than named, so a TFM bump needs no
    # edit here.
    $pattern = Join-Path $root (Split-Path -Parent $Project) "bin/$Configuration/*/$name"
    $exe = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not $exe) {
        throw "$name has no $Configuration build at $pattern. Build the solution first."
    }

    [pscustomobject] @{
        Exe     = [IO.Path]::GetRelativePath($root, $exe.FullName)
        Classes = @(& $exe.FullName -list classes/json -noColor -noLogo | ConvertFrom-Json | Sort-Object)
    }
}

$caps   = Get-Caps $MaxParallel
$suites = [ordered] @{}

foreach ($filter in Get-ChildItem -Path $root -Filter '*.slnf' | Sort-Object Name) {
    $runners = @(Get-Projects $filter.FullName | Where-Object { Test-IsRunner $_ })

    # A filter with nothing to run is not a suite. Filters exist for other
    # reasons -- building only src, say -- and those should cost nothing here.
    if (-not $runners) { continue }

    $suites[$filter.BaseName] = $runners
}

# A runner in the solution but in no filter would run nowhere, which from the
# outside is indistinguishable from a suite that passed.
$orphans = @(Get-Projects (Resolve-Path $Solution) |
    Where-Object { Test-IsRunner $_ } |
    Where-Object { $_ -notin ($suites.Values | ForEach-Object { $_ }) })

if ($orphans) {
    # throw, not Write-Error + exit: under $ErrorActionPreference = 'Stop' the
    # Write-Error terminates first and the exit never runs, so the exit CODE
    # was never the thing being relied on. A throw fails the same way whether
    # this is run by `pwsh -File` or dot-called from another script.
    throw (@(
        'These test projects are in the solution but in no .slnf, so no runner'
        'would take them:'
        ($orphans | ForEach-Object { "  $_" })
        'Add each to a filter, or add a filter for them.'
    ) -join [Environment]::NewLine)
}

$matrix = [ordered] @{}
foreach ($suite in $suites.Keys) {
    $cap = if ($caps.ContainsKey($suite)) { $caps[$suite] }
           elseif ($caps.ContainsKey('default')) { $caps['default'] }
           else { 0 }

    $legs = [System.Collections.Generic.List[object]]::new()

    foreach ($project in $suites[$suite]) {
        $runner = Get-Runner $project

        # Assigned first, then iterated. Split-Evenly returns its groups
        # comma-wrapped to survive assignment, and that same wrapper makes
        # `foreach (... in Split-Evenly ...)` yield ONE item -- the whole array
        # -- so every class lands on a single leg.
        $groups = Split-Evenly -Items $runner.Classes -Into $cap

        foreach ($group in $groups) {
            $legs.Add([pscustomobject] @{
                name = "$suite-$($legs.Count + 1)"
                exe  = $runner.Exe
                # Ready-made arguments, so a pipeline never builds them from an
                # array in YAML. Classes on one leg run in a SINGLE process and
                # therefore share whatever fixture their collection defines.
                args    = (($group | ForEach-Object { "-class `"$_`"" }) -join ' ')
                classes = ($group -join ' ')
            })
        }
    }

    $matrix[$suite] = @($legs)
}

if ($Ado) {
    # Azure DevOps wants one flat object keyed by a unique leg name.
    $flat = [ordered] @{}
    foreach ($legs in $matrix.Values) {
        foreach ($leg in $legs) {
            $flat[$leg.name] = [pscustomobject] @{
                EXE = $leg.exe; ARGS = $leg.args; CLASSES = $leg.classes
            }
        }
    }
    $flat | ConvertTo-Json -Compress -Depth 5
}
else {
    $matrix | ConvertTo-Json -Compress -Depth 5
}
