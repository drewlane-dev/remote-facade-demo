#!/usr/bin/env pwsh

#generate a matrix of test legs for a slnx or slnf
#-Granularity Tag (default) splits by [TestCategory], packed down to -MaxParallel
#-Granularity Project splits by test project and ignores tags entirely
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Sln,
    [string[]] $Tags = @(),
    [string] $Suite,
    [ValidateSet('Tag', 'Project')]
    [string] $Granularity   = 'Tag',
    [string] $Configuration = 'Release',
    [int]    $MaxParallel   = 0,
    [switch] $Ado
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# `pwsh -File` passes every argument as a literal STRING with no expression
# evaluation, so -Tags domain,graph arrives as one element, not two, and the
# filter becomes TestCategory!=domain,graph -- which matches everything.
# Splitting here accepts both that form and a real array from -Command.
$Tags = @($Tags | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if ($Granularity -eq 'Tag' -and -not $Tags) {
    throw "-Tags is required at Tag granularity. Use -Granularity Project to run whole projects."
}

if ($Granularity -eq 'Project' -and $MaxParallel -gt 0) {
    throw ("-MaxParallel does not apply at Project granularity: a leg runs one " +
           "executable, so the leg count is the number of test projects and " +
           "cannot be capped below it. Drop one of the two.")
}

$slnPath = Resolve-Path $Sln
$root    = Split-Path -Parent $slnPath

# The suite names the legs, and defaults to the filter's own name so
# integration.slnf yields integration_1, integration_2.
if (-not $Suite) { $Suite = [IO.Path]::GetFileNameWithoutExtension($slnPath) }

# List all projects in a slnx or slnf
function Get-Projects {
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

# ask msbuild if this is a test project
function Get-IsTestProject {
    param([string] $Project)

    $value = dotnet msbuild (Join-Path $root $Project) `
        -getProperty:IsTestingPlatformApplication -nologo 2>$null

    ($LASTEXITCODE -eq 0) -and (($value | Select-Object -First 1) -eq 'true')
}

# generate matrix identifier for pipeline leg
function ConvertTo-Identifier {
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
    exactly $Into groups when it does not divide the count: 6 tags at a cap of
    5, chunked by ceil(6/5)=2, gives THREE groups -- silently using three
    runners where five were asked for. Dealing gives min(count, $Into) every
    time.
    #>
    param([string[]] $Items = @(), [int] $Into)

    $into = if ($Into -le 0) { $Items.Count } else { [Math]::Min($Into, $Items.Count) }

    $groups = [object[]]::new($into)
    for ($i = 0; $i -lt $into; $i++) { $groups[$i] = [System.Collections.Generic.List[string]]::new() }
    for ($i = 0; $i -lt $Items.Count; $i++) { $groups[$i % $into].Add($Items[$i]) }

    # The leading comma is load-bearing. PowerShell UNROLLS a returned
    # collection into the pipeline, so `return $groups` hands back the two
    # inner groups rather than the outer array of two -- and a cap of 1 then
    # returns its single group, whose .Count is the ITEM count. That silently
    # produced two e2e legs under a cap of one, which is the exact
    # over-parallelisation the cap exists to prevent.
    ,$groups
}

# find the built runner; the framework folder is globbed so a TFM bump needs no edit
function Get-Exe {
    param([string] $Project)

    $name = [IO.Path]::GetFileNameWithoutExtension($Project)
    $pattern = Join-Path $root (Split-Path -Parent $Project) "bin/$Configuration/*/$name"
    $exe = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $exe) {
        throw "$name has no $Configuration build at $pattern. Build $Sln first."
    }

    $exe.FullName
}

# count matching tests WITHOUT running them: --list-tests honours --filter, so
# every guard below costs a discovery pass and no test execution
function Measure-Tests {
    param([string] $Exe, [string] $Filter)

    $output = if ($Filter) { & $Exe --list-tests --filter $Filter 2>&1 }
              else         { & $Exe --list-tests 2>&1 }

    $match = $output | Select-String -Pattern 'found (\d+) test' | Select-Object -First 1
    if (-not $match) { throw "could not read a test count from $Exe. Output: $($output -join ' ')" }

    [int] $match.Matches[0].Groups[1].Value
}

$projects = @(Get-Projects $slnPath | Where-Object { Get-IsTestProject $_ })

# no test projects found in the solution or solution filter
if (-not $projects) {
    throw (@(
        "no project in $Sln is a test project (IsTestingPlatformApplication)."
        'Projects in it:'
        (Get-Projects $slnPath | ForEach-Object { "  " + [IO.Path]::GetFileNameWithoutExtension($_) })
    ) -join [Environment]::NewLine)
}

$legs = [System.Collections.Generic.List[object]]::new()

function New-Leg {
    param([string] $Exe, [string] $LegArgs, [string] $Tags)

    [pscustomobject] @{
        name  = "$(ConvertTo-Identifier $Suite)_$($legs.Count + 1)"
        suite = $Suite
        exe   = [IO.Path]::GetRelativePath($root, $Exe)
        # Ready-made arguments, so a pipeline never builds them from an array
        # in YAML. Tags on one leg run in a SINGLE process and therefore share
        # whatever [AssemblyInitialize] sets up.
        args = $LegArgs
        tags = $Tags
    }
}

foreach ($project in $projects) {
    $exe  = Get-Exe $project
    $name = [IO.Path]::GetFileNameWithoutExtension($project)

    if ($Granularity -eq 'Project') {
        $legs.Add((New-Leg -Exe $exe -LegArgs '' -Tags $name))
        continue
    }

    $total = Measure-Tests -Exe $exe
    if ($total -eq 0) {
        throw "$name contains no tests. Remove it from $Sln."
    }

    # A test carrying none of the tags runs on no leg, which from the outside
    # is indistinguishable from a leg that passed.
    $uncoveredFilter = ($Tags | ForEach-Object { "TestCategory!=$_" }) -join '&'
    $uncovered = Measure-Tests -Exe $exe -Filter $uncoveredFilter
    if ($uncovered -gt 0) {
        throw ("$name has $uncovered test(s) carrying none of: $($Tags -join ', '). " +
               "They would run on no leg. Tag them, or add their tag to -Tags.")
    }

    # Sum-vs-total catches the opposite fault, a test in TWO tags running twice.
    # It is only conclusive BECAUSE uncovered is already known to be zero: an
    # untagged test and an overlapping one cancel out exactly, and a sum check
    # alone then passes while a test runs nowhere.
    $perTag = @($Tags | ForEach-Object { Measure-Tests -Exe $exe -Filter "TestCategory=$_" })
    $sum = ($perTag | Measure-Object -Sum).Sum
    if ($sum -ne $total) {
        throw ("$name has tests in more than one of: $($Tags -join ', ') " +
               "($sum tagged across $total tests). They would run on several legs.")
    }

    $groups = Split-Evenly -Items $Tags -Into $MaxParallel

    foreach ($group in $groups) {
        $filter = ($group | ForEach-Object { "TestCategory=$_" }) -join '|'
        $legs.Add((New-Leg -Exe $exe -LegArgs "--filter `"$filter`"" -Tags ($group -join ' ')))
    }
}

if ($Ado) {
    # Azure DevOps wants an object keyed by the matrix configuration name.
    $flat = [ordered] @{}
    foreach ($leg in $legs) {
        $flat[$leg.name] = [pscustomobject] @{
            SUITE = $leg.suite; LEG = $leg.name
            EXE   = $leg.exe;   ARGS = $leg.args; TAGS = $leg.tags
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
