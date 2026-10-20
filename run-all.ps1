<#
.SYNOPSIS
    Runs every non-interactive MXC demo in order and prints a summary.
.EXAMPLE
    .\run-all.ps1              # everything except the interactive shell (07)
    .\run-all.ps1 -SkipWslc    # on a machine without WSL 2.9.9+
#>
param(
    [switch] $SkipWslc
)

$ErrorActionPreference = 'Continue'
$demos = Get-ChildItem (Join-Path $PSScriptRoot 'demos') -Filter '*.cs' |
    Sort-Object Name |
    Where-Object { $_.Name -notlike '07-*' }          # 07 needs a real terminal; run it by hand
if ($SkipWslc) { $demos = $demos | Where-Object { $_.Name -notlike '*Wslc*' } }

$results = @()
foreach ($demo in $demos) {
    Write-Host "`n################ $($demo.Name) ################" -ForegroundColor Cyan
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    dotnet run $demo.FullName
    $results += [pscustomobject]@{
        Demo    = $demo.BaseName
        Exit    = $LASTEXITCODE
        Seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
    }
}

Write-Host "`n=== summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
Write-Host "07-InteractiveShell is interactive. Run it yourself:  dotnet run demos\07-InteractiveShell.cs" -ForegroundColor DarkGray

$failed = @($results | Where-Object Exit -ne 0).Count
exit $failed
