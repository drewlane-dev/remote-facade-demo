#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Emit the CI legs for ONE test suite.

.DESCRIPTION
    Given a solution or solution filter, this finds the test projects inside
    it, reads their test classes, and packs those classes into legs for a
    build matrix. One suite per call:

        suites.ps1 -Sln integration.slnf -MaxParallel 2
        suites.ps1 -Sln e2e.slnf         -MaxParallel 1

    Two calls rather than one that discovers everything, because a suite's
    filter and its parallelism are decided together and belong in the same
    line. It also means the cap is a plain number instead of a per-suite
    string, and each caller reads as what it is.

    Which projects are runners is asked of MSBuild --
    IsTestingPlatformApplication, set by the test SDK -- rather than matched by
    name. Names do not discriminate here: e2e.slnf also contains OrderBook.Api,
    which is also an Exe, and OrderBook.Tests.Shared, which is also called
    *Tests. A name pattern has to be kept correct by hand as projects are added
    and renamed; this cannot go stale.

    Class names come from the runner's own structured output:

        <exe> -list classes/json -noColor -noLogo

    -noColor and -noLogo matter. Without them the runner prints a banner and
    wraps its output in ANSI codes, and an earlier version of this parsed that
    text: the banner begins with a letter and contains dots, so it was admitted
    as a test class and produced matrix legs named "(64-bit" and "xUnit.net".

    A filter holding no runner is fatal. A suite that silently produced no legs
    would leave a green pipeline that ran nothing, which is the failure this
    whole arrangement exists to prevent.

.PARAMETER Sln
    The .slnx or .slnf to read projects from. A filter is the usual choice:
    it is already what CI builds for that suite, so the projects that get
    built and the projects that get run cannot drift apart.


.PARAMETER MaxParallel
    Most legs to produce PER TEST PROJECT. Omitted or 0 means one class per leg.

    Per project rather than per suite because a leg runs one executable, so a
    cap spanning several runners would have to be divided among them. Every
    filter here holds exactly one runner and the two readings coincide; pass a
    whole .slnx with two runners and a cap of 3 and you get up to six legs, not
    three.

    Classes on one leg run in a single process and share their collection's
    fixture, so packing an expensive suite avoids rebuilding its containers
    per leg. Measured on this repo, an e2e leg is 94-99% fixture setup, which
    is why e2e is packed to a single leg and integration is not.

.PARAMETER Ado
    Emit Azure DevOps' matrix shape -- an object keyed by leg name -- instead
    of the array GitHub Actions consumes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Sln,
    [string] $Suite,
    [string] $Configuration = 'Release',
    [int]    $MaxParallel   = 0,
    [switch] $Ado
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$slnPath = Resolve-Path $Sln
$root    = Split-Path -Parent $slnPath

# The suite names the legs, and defaults to the filter's own name so
# integration.slnf yields integration_1, integration_2.
if (-not $Suite) { $Suite = [IO.Path]::GetFileNameWithoutExtension($slnPath) }

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

function Test-IsRunner {
    <#
    Whether MSBuild considers this project something that can run tests.

    A SINGLE -getProperty prints the bare value; ask for two or more and it
    switches to a JSON envelope instead. Nothing here wants the envelope, so
    this reads the one line and compares it.
    #>
    param([string] $Project)

    $value = dotnet msbuild (Join-Path $root $Project) `
        -getProperty:IsTestingPlatformApplication -nologo 2>$null

    ($LASTEXITCODE -eq 0) -and (($value | Select-Object -First 1) -eq 'true')
}

function ConvertTo-Identifier {
    <#
    Azure DevOps matrix configuration names accept only letters, digits and
    underscores, must start with a letter, and cap at 100 characters. A leg
    called "integration-1" is therefore rejected outright, before a single test
    runs -- which is exactly how the first real ADO run of this failed.

    Sanitising here rather than demanding it of whoever names a filter, and
    applied to both output shapes so a leg is called the same thing on either
    platform.
    #>
    param([string] $Name)

    $safe = $Name -replace '[^A-Za-z0-9_]', '_'
    if ($safe -notmatch '^[A-Za-z]') { $safe = "suite_$safe" }
    if ($safe.Length -gt 100) { $safe = $safe.Substring(0, 100) }
    $safe
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
    # produced two e2e legs under a cap of one, which is the exact
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
        throw "$name has no $Configuration build at $pattern. Build $Sln first."
    }

    [pscustomobject] @{
        Exe     = [IO.Path]::GetRelativePath($root, $exe.FullName)
        Classes = @(& $exe.FullName -list classes/json -noColor -noLogo | ConvertFrom-Json | Sort-Object)
    }
}

$projects = @(Get-Projects $slnPath | Where-Object { Test-IsRunner $_ })

if (-not $projects) {
    throw (@(
        "no project in $Sln is a test project (IsTestingPlatformApplication)."
        'Projects in it:'
        (Get-Projects $slnPath | ForEach-Object { "  " + [IO.Path]::GetFileNameWithoutExtension($_) })
    ) -join [Environment]::NewLine)
}

$legs = [System.Collections.Generic.List[object]]::new()

foreach ($project in $projects) {
    $runner = Get-Runner $project

    # Assigned first, then iterated. Split-Evenly returns its groups
    # comma-wrapped to survive assignment, and that same wrapper makes
    # `foreach (... in Split-Evenly ...)` yield ONE item -- the whole array --
    # so every class would land on a single leg.
    $groups = Split-Evenly -Items $runner.Classes -Into $MaxParallel

    foreach ($group in $groups) {
        $legs.Add([pscustomobject] @{
            name  = "$(ConvertTo-Identifier $Suite)_$($legs.Count + 1)"
            suite = $Suite
            exe   = $runner.Exe
            # Ready-made arguments, so a pipeline never builds them from an
            # array in YAML. Classes on one leg run in a SINGLE process and
            # therefore share whatever fixture their collection defines.
            args    = (($group | ForEach-Object { "-class `"$_`"" }) -join ' ')
            classes = ($group -join ' ')
        })
    }
}

if ($Ado) {
    # Azure DevOps wants an object keyed by the matrix configuration name.
    $flat = [ordered] @{}
    foreach ($leg in $legs) {
        $flat[$leg.name] = [pscustomobject] @{
            SUITE = $leg.suite; LEG = $leg.name
            EXE   = $leg.exe;   ARGS = $leg.args; CLASSES = $leg.classes
        }
    }
    $flat | ConvertTo-Json -Compress -Depth 5
}
else {
    # -InputObject, not the pipeline, and no -AsArray. The pipeline unrolls the
    # list so a single leg would serialise as a bare object that fromJSON
    # cannot iterate; -AsArray on top of an already-array input wraps it a
    # second time and yields [[...]].
    ConvertTo-Json -InputObject @($legs) -Compress -Depth 5
}
