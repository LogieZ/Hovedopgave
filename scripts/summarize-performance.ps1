param(
    [string]$RunsetPath,
    [switch]$Latest,
    [string]$OutputFile = "performance-summary.csv"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RunsetPath {
    param(
        [string]$ProvidedPath,
        [switch]$UseLatest
    )

    if (-not [string]::IsNullOrWhiteSpace($ProvidedPath)) {
        if (-not (Test-Path -LiteralPath $ProvidedPath)) {
            throw "RunsetPath does not exist: $ProvidedPath"
        }
        return (Resolve-Path -LiteralPath $ProvidedPath).Path
    }

    if ($UseLatest) {
        $latest = Get-ChildItem -Path "perf-results" -Directory |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($null -eq $latest) {
            throw "No runsets found under perf-results/."
        }

        return $latest.FullName
    }

    throw "Provide -RunsetPath or use -Latest."
}

function Convert-ToDouble {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [double]::NaN
    }

    $normalized = $Value.Replace(',', '.')
    $parsed = 0.0
    if ([double]::TryParse($normalized, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return [double]::NaN
}

function Parse-RunLog {
    param([string]$LogPath)

    if (-not (Test-Path -LiteralPath $LogPath)) {
        return [pscustomobject]@{
            CpuDeltaMsTotal = [double]::NaN
            WorkingSetMaxMB = [double]::NaN
            PrivateMemoryMaxMB = [double]::NaN
            ManagedHeapMaxMB = [double]::NaN
        }
    }

    $cpuDeltaTotal = 0.0
    $workingSetMax = [double]::NegativeInfinity
    $privateMax = [double]::NegativeInfinity
    $heapMax = [double]::NegativeInfinity

    foreach ($line in Get-Content -LiteralPath $LogPath) {
        if ($line -match 'CPU delta=([0-9]+(?:\.[0-9]+)?) ms') {
            $cpuDeltaTotal += Convert-ToDouble $matches[1]
        }

        if ($line -match 'WorkingSet=([0-9]+(?:\.[0-9]+)?) MB') {
            $value = Convert-ToDouble $matches[1]
            if ($value -gt $workingSetMax) { $workingSetMax = $value }
        }

        if ($line -match 'PrivateMemory=([0-9]+(?:\.[0-9]+)?) MB') {
            $value = Convert-ToDouble $matches[1]
            if ($value -gt $privateMax) { $privateMax = $value }
        }

        if ($line -match 'ManagedHeap=([0-9]+(?:\.[0-9]+)?) MB') {
            $value = Convert-ToDouble $matches[1]
            if ($value -gt $heapMax) { $heapMax = $value }
        }
    }

    if ([double]::IsNegativeInfinity($workingSetMax)) { $workingSetMax = [double]::NaN }
    if ([double]::IsNegativeInfinity($privateMax)) { $privateMax = [double]::NaN }
    if ([double]::IsNegativeInfinity($heapMax)) { $heapMax = [double]::NaN }

    return [pscustomobject]@{
        CpuDeltaMsTotal = $cpuDeltaTotal
        WorkingSetMaxMB = $workingSetMax
        PrivateMemoryMaxMB = $privateMax
        ManagedHeapMaxMB = $heapMax
    }
}

$resolvedRunset = Get-RunsetPath -ProvidedPath $RunsetPath -UseLatest:$Latest
$runSummaryPath = Join-Path $resolvedRunset "run-summary.csv"

if (-not (Test-Path -LiteralPath $runSummaryPath)) {
    throw "Missing run-summary.csv in runset: $resolvedRunset"
}

$rows = Import-Csv -LiteralPath $runSummaryPath
$out = New-Object System.Collections.Generic.List[object]

foreach ($row in $rows) {
    $runId = [int]$row.Run
    $runFolder = Join-Path $resolvedRunset ("run-{0:d2}" -f $runId)
    $logPath = Join-Path $runFolder "app-stdout.log"

    $logStats = Parse-RunLog -LogPath $logPath

    $durationSeconds = Convert-ToDouble $row.DurationSeconds
    $cpuSeconds = if ([double]::IsNaN($logStats.CpuDeltaMsTotal)) { [double]::NaN } else { $logStats.CpuDeltaMsTotal / 1000.0 }
    $cpuPctSingleCore = if ($durationSeconds -gt 0 -and -not [double]::IsNaN($cpuSeconds)) {
        ($cpuSeconds / $durationSeconds) * 100.0
    }
    else {
        [double]::NaN
    }

    $out.Add([pscustomobject]@{
        Run = $runId
        DurationSeconds = [math]::Round($durationSeconds, 3)
        CpuTotalSeconds = [math]::Round($cpuSeconds, 3)
        CpuSingleCoreEquivalentPct = [math]::Round($cpuPctSingleCore, 2)
        WorkingSetMaxMB = [math]::Round($logStats.WorkingSetMaxMB, 2)
        PrivateMemoryMaxMB = [math]::Round($logStats.PrivateMemoryMaxMB, 2)
        ManagedHeapMaxMB = [math]::Round($logStats.ManagedHeapMaxMB, 2)
        NetTotalMB = [math]::Round((Convert-ToDouble $row.NetTotal_Total_MB), 3)
        NetTotalAvgMBps = [math]::Round((Convert-ToDouble $row.NetTotal_Avg_MBps), 6)
        NetTotalPeakMBps = [math]::Round((Convert-ToDouble $row.NetTotal_Peak_MBps), 6)
    })
}

$outputPath = Join-Path $resolvedRunset $OutputFile
$out | Export-Csv -LiteralPath $outputPath -NoTypeInformation -Encoding utf8

Write-Host "Runset: $resolvedRunset"
Write-Host "Summary written: $outputPath"
Write-Host ""
$out | Format-Table -AutoSize
