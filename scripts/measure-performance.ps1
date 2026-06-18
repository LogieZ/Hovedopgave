param(
    [string]$RunCommand = 'dotnet run --project "src/VideoArchiveManager/VideoArchiveManager.csproj" -c Release',
    [int]$Runs = 5,
    [int]$SampleIntervalSeconds = 1,
    [string]$OutputRoot = 'perf-results'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Median {
    param([double[]]$Values)

    if (-not $Values -or $Values.Count -eq 0) {
        return [double]::NaN
    }

    $sorted = @($Values | Sort-Object)
    $count = $sorted.Count
    $mid = [int]($count / 2)

    if (($count % 2) -eq 1) {
        return [double]$sorted[$mid]
    }

    return ([double]$sorted[$mid - 1] + [double]$sorted[$mid]) / 2.0
}

function Get-Stats {
    param(
        [double[]]$Values,
        [double]$SampleInterval,
        [switch]$TreatAsRatePerSecond
    )

    if (-not $Values -or $Values.Count -eq 0) {
        return [pscustomobject]@{
            Count  = 0
            Avg    = [double]::NaN
            Median = [double]::NaN
            Min    = [double]::NaN
            Max    = [double]::NaN
            Total  = [double]::NaN
        }
    }

    $avg = ($Values | Measure-Object -Average).Average
    $min = ($Values | Measure-Object -Minimum).Minimum
    $max = ($Values | Measure-Object -Maximum).Maximum
    $median = Get-Median -Values $Values

    $total = [double]::NaN
    if ($TreatAsRatePerSecond) {
        $total = (($Values | Measure-Object -Sum).Sum * $SampleInterval)
    }

    return [pscustomobject]@{
        Count  = $Values.Count
        Avg    = [double]$avg
        Median = [double]$median
        Min    = [double]$min
        Max    = [double]$max
        Total  = [double]$total
    }
}

function Start-CounterSampler {
    param(
        [string[]]$Counters,
        [int]$SampleInterval,
        [string]$RawCsvPath,
        [string]$StopSignalPath
    )

    # Create file up-front to avoid race conditions on short app runs.
    'Timestamp,CounterPath,CookedValue' | Out-File -FilePath $RawCsvPath -Encoding utf8

    $job = Start-Job -ScriptBlock {
        param($CounterList, $Interval, $OutPath, $StopPath)

        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'

        while (-not (Test-Path -LiteralPath $StopPath)) {
            $timestamp = (Get-Date).ToString('o')
            $sample = Get-Counter -Counter $CounterList

            foreach ($counterSample in $sample.CounterSamples) {
                $line = '{0},"{1}",{2}' -f $timestamp, $counterSample.Path.Replace('"', '""'), $counterSample.CookedValue
                Add-Content -LiteralPath $OutPath -Value $line
            }

            Start-Sleep -Seconds $Interval
        }
    } -ArgumentList @($Counters, $SampleInterval, $RawCsvPath, $StopSignalPath)

    return $job
}

function Stop-CounterSampler {
    param(
        [System.Management.Automation.Job]$SamplerJob,
        [string]$StopSignalPath
    )

    if (-not (Test-Path -LiteralPath $StopSignalPath)) {
        New-Item -ItemType File -Path $StopSignalPath -Force | Out-Null
    }

    # Give sampler loop a moment to stop naturally.
    Wait-Job -Job $SamplerJob -Timeout 10 | Out-Null

    if ($SamplerJob.State -eq 'Running') {
        Stop-Job -Job $SamplerJob -Force | Out-Null
    }

    Receive-Job -Job $SamplerJob -ErrorAction SilentlyContinue | Out-Null
    Remove-Job -Job $SamplerJob -Force | Out-Null

    Remove-Item -LiteralPath $StopSignalPath -Force -ErrorAction SilentlyContinue
}

function Write-CounterSample {
    param(
        [string[]]$Counters,
        [string]$RawCsvPath
    )

    $timestamp = (Get-Date).ToString('o')
    $sample = Get-Counter -Counter $Counters

    foreach ($counterSample in $sample.CounterSamples) {
        $line = '{0},"{1}",{2}' -f $timestamp, $counterSample.Path.Replace('"', '""'), $counterSample.CookedValue
        Add-Content -LiteralPath $RawCsvPath -Value $line
    }
}

function Convert-RawCountersToSamples {
    param(
        [string]$RawCsvPath,
        [string]$SamplesCsvPath
    )

    $rows = Import-Csv -LiteralPath $RawCsvPath

    $groupedByTimestamp = $rows | Group-Object -Property Timestamp
    $samples = New-Object System.Collections.Generic.List[object]

    foreach ($group in $groupedByTimestamp) {
        $timestamp = [datetime]::Parse($group.Name)

        $netInBps = ($group.Group | Where-Object { $_.CounterPath -match 'Bytes Received/sec' } | ForEach-Object { [double]$_.CookedValue } | Measure-Object -Sum).Sum
        $netOutBps = ($group.Group | Where-Object { $_.CounterPath -match 'Bytes Sent/sec' } | ForEach-Object { [double]$_.CookedValue } | Measure-Object -Sum).Sum

        $diskReadBps = ($group.Group | Where-Object { $_.CounterPath -match 'Disk Read Bytes/sec' } | ForEach-Object { [double]$_.CookedValue } | Measure-Object -Sum).Sum
        $diskWriteBps = ($group.Group | Where-Object { $_.CounterPath -match 'Disk Write Bytes/sec' } | ForEach-Object { [double]$_.CookedValue } | Measure-Object -Sum).Sum

        $queueValues = @($group.Group | Where-Object { $_.CounterPath -match 'Avg\. Disk Queue Length' } | ForEach-Object { [double]$_.CookedValue })
        $queueAvg = if ($queueValues.Count -gt 0) { ($queueValues | Measure-Object -Average).Average } else { [double]::NaN }

        $sample = [pscustomobject]@{
            Timestamp        = $timestamp.ToString('o')
            NetIn_MBps       = [double]$netInBps / 1MB
            NetOut_MBps      = [double]$netOutBps / 1MB
            NetTotal_MBps    = ([double]$netInBps + [double]$netOutBps) / 1MB
            DiskRead_MBps    = [double]$diskReadBps / 1MB
            DiskWrite_MBps   = [double]$diskWriteBps / 1MB
            DiskTotal_MBps   = ([double]$diskReadBps + [double]$diskWriteBps) / 1MB
            DiskQueue_AvgLen = [double]$queueAvg
        }

        $samples.Add($sample)
    }

    $samples | Export-Csv -LiteralPath $SamplesCsvPath -NoTypeInformation -Encoding utf8
    return $samples.ToArray()
}

function New-RunSummary {
    param(
        [int]$RunNumber,
        [datetime]$StartedAt,
        [datetime]$EndedAt,
        [int]$ExitCode,
        [double]$SampleInterval,
        [object[]]$Samples
    )

    $sampleRows = @($Samples)

    $netIn = New-Object System.Collections.Generic.List[double]
    $netOut = New-Object System.Collections.Generic.List[double]
    $netTotal = New-Object System.Collections.Generic.List[double]
    $diskRead = New-Object System.Collections.Generic.List[double]
    $diskWrite = New-Object System.Collections.Generic.List[double]
    $diskTotal = New-Object System.Collections.Generic.List[double]
    $queue = New-Object System.Collections.Generic.List[double]

    foreach ($row in $sampleRows) {
        try { $netIn.Add([double]$row.NetIn_MBps) } catch {}
        try { $netOut.Add([double]$row.NetOut_MBps) } catch {}
        try { $netTotal.Add([double]$row.NetTotal_MBps) } catch {}
        try { $diskRead.Add([double]$row.DiskRead_MBps) } catch {}
        try { $diskWrite.Add([double]$row.DiskWrite_MBps) } catch {}
        try { $diskTotal.Add([double]$row.DiskTotal_MBps) } catch {}

        try {
            $q = [double]$row.DiskQueue_AvgLen
            if (-not [double]::IsNaN($q)) {
                $queue.Add($q)
            }
        }
        catch {}
    }

    $netInStats = Get-Stats -Values $netIn.ToArray() -SampleInterval $SampleInterval -TreatAsRatePerSecond
    $netOutStats = Get-Stats -Values $netOut.ToArray() -SampleInterval $SampleInterval -TreatAsRatePerSecond
    $netTotalStats = Get-Stats -Values $netTotal.ToArray() -SampleInterval $SampleInterval -TreatAsRatePerSecond

    $diskReadStats = Get-Stats -Values $diskRead.ToArray() -SampleInterval $SampleInterval -TreatAsRatePerSecond
    $diskWriteStats = Get-Stats -Values $diskWrite.ToArray() -SampleInterval $SampleInterval -TreatAsRatePerSecond
    $diskTotalStats = Get-Stats -Values $diskTotal.ToArray() -SampleInterval $SampleInterval -TreatAsRatePerSecond

    $queueStats = Get-Stats -Values $queue.ToArray() -SampleInterval $SampleInterval

    return [pscustomobject]@{
        Run                         = $RunNumber
        StartedAt                   = $StartedAt.ToString('o')
        EndedAt                     = $EndedAt.ToString('o')
        DurationSeconds             = [math]::Round(($EndedAt - $StartedAt).TotalSeconds, 3)
        ExitCode                    = $ExitCode

        NetIn_Avg_MBps              = [math]::Round($netInStats.Avg, 6)
        NetIn_Median_MBps           = [math]::Round($netInStats.Median, 6)
        NetIn_Peak_MBps             = [math]::Round($netInStats.Max, 6)
        NetIn_Total_MB              = [math]::Round($netInStats.Total, 3)

        NetOut_Avg_MBps             = [math]::Round($netOutStats.Avg, 6)
        NetOut_Median_MBps          = [math]::Round($netOutStats.Median, 6)
        NetOut_Peak_MBps            = [math]::Round($netOutStats.Max, 6)
        NetOut_Total_MB             = [math]::Round($netOutStats.Total, 3)

        NetTotal_Avg_MBps           = [math]::Round($netTotalStats.Avg, 6)
        NetTotal_Median_MBps        = [math]::Round($netTotalStats.Median, 6)
        NetTotal_Peak_MBps          = [math]::Round($netTotalStats.Max, 6)
        NetTotal_Total_MB           = [math]::Round($netTotalStats.Total, 3)

        DiskRead_Avg_MBps           = [math]::Round($diskReadStats.Avg, 6)
        DiskRead_Median_MBps        = [math]::Round($diskReadStats.Median, 6)
        DiskRead_Peak_MBps          = [math]::Round($diskReadStats.Max, 6)
        DiskRead_Total_MB           = [math]::Round($diskReadStats.Total, 3)

        DiskWrite_Avg_MBps          = [math]::Round($diskWriteStats.Avg, 6)
        DiskWrite_Median_MBps       = [math]::Round($diskWriteStats.Median, 6)
        DiskWrite_Peak_MBps         = [math]::Round($diskWriteStats.Max, 6)
        DiskWrite_Total_MB          = [math]::Round($diskWriteStats.Total, 3)

        DiskTotal_Avg_MBps          = [math]::Round($diskTotalStats.Avg, 6)
        DiskTotal_Median_MBps       = [math]::Round($diskTotalStats.Median, 6)
        DiskTotal_Peak_MBps         = [math]::Round($diskTotalStats.Max, 6)
        DiskTotal_Total_MB          = [math]::Round($diskTotalStats.Total, 3)

        DiskQueue_Avg               = [math]::Round($queueStats.Avg, 6)
        DiskQueue_Median            = [math]::Round($queueStats.Median, 6)
        DiskQueue_Peak              = [math]::Round($queueStats.Max, 6)
    }
}

function New-AggregatedSummary {
    param([object[]]$RunSummaries)

    $successful = @($RunSummaries | Where-Object { $_.ExitCode -eq 0 })
    if ($successful.Count -eq 0) {
        return [pscustomobject]@{ Note = 'No successful runs to aggregate.' }
    }

    $fields = @(
        'DurationSeconds',
        'NetTotal_Median_MBps',
        'NetTotal_Peak_MBps',
        'NetTotal_Total_MB',
        'DiskTotal_Median_MBps',
        'DiskTotal_Peak_MBps',
        'DiskTotal_Total_MB',
        'DiskQueue_Median',
        'DiskQueue_Peak'
    )

    $out = [ordered]@{
        SuccessfulRuns = $successful.Count
    }

    foreach ($field in $fields) {
        $values = $successful | ForEach-Object { [double]($_.$field) }
        $stats = Get-Stats -Values $values -SampleInterval 1
        $out["$field`_MedianOfRuns"] = [math]::Round($stats.Median, 6)
        $out["$field`_MinOfRuns"] = [math]::Round($stats.Min, 6)
        $out["$field`_MaxOfRuns"] = [math]::Round($stats.Max, 6)
    }

    return [pscustomobject]$out
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $OutputRoot "runset-$timestamp"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$counters = @(
    '\Network Interface(*)\Bytes Received/sec',
    '\Network Interface(*)\Bytes Sent/sec',
    '\PhysicalDisk(_Total)\Disk Read Bytes/sec',
    '\PhysicalDisk(_Total)\Disk Write Bytes/sec',
    '\PhysicalDisk(_Total)\Avg. Disk Queue Length'
)

$runSummaries = New-Object System.Collections.Generic.List[object]

for ($run = 1; $run -le $Runs; $run++) {
    Write-Host "=== Run $run of $Runs ===" -ForegroundColor Cyan

    $runDir = Join-Path $outDir ("run-{0:d2}" -f $run)
    New-Item -ItemType Directory -Path $runDir -Force | Out-Null

    $rawCsv = Join-Path $runDir 'raw-counters.csv'
    $samplesCsv = Join-Path $runDir 'samples.csv'
    $stdoutLog = Join-Path $runDir 'app-stdout.log'
    $stderrLog = Join-Path $runDir 'app-stderr.log'

    'Timestamp,CounterPath,CookedValue' | Out-File -FilePath $rawCsv -Encoding utf8

    $wrappedRunCommand = "& { $RunCommand; if (`$LASTEXITCODE -ne `$null) { exit `$LASTEXITCODE } else { exit 0 } }"

    $startedAt = Get-Date
    $proc = Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $wrappedRunCommand) -PassThru -NoNewWindow -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog

    while (-not $proc.HasExited) {
        Write-CounterSample -Counters $counters -RawCsvPath $rawCsv
        Start-Sleep -Seconds $SampleIntervalSeconds
        $proc.Refresh()
    }

    # One final sample after process exit to capture tail activity.
    Write-CounterSample -Counters $counters -RawCsvPath $rawCsv
    $proc.WaitForExit()
    $proc.Refresh()
    $exitCode = if ($null -ne $proc.ExitCode) { [int]$proc.ExitCode } else { 0 }
    $endedAt = Get-Date

    $samples = Convert-RawCountersToSamples -RawCsvPath $rawCsv -SamplesCsvPath $samplesCsv
    $summary = New-RunSummary -RunNumber $run -StartedAt $startedAt -EndedAt $endedAt -ExitCode $exitCode -SampleInterval $SampleIntervalSeconds -Samples $samples

    $summaryPath = Join-Path $runDir 'summary.json'
    $summary | ConvertTo-Json -Depth 4 | Out-File -FilePath $summaryPath -Encoding utf8

    $runSummaries.Add($summary)

    if ($exitCode -eq 0) {
        Write-Host "Run $run completed successfully." -ForegroundColor Green
    }
    else {
        Write-Host "Run $run failed with exit code $exitCode. See logs in $runDir" -ForegroundColor Yellow
    }
}

$runSummaryCsv = Join-Path $outDir 'run-summary.csv'
$runSummaries | Export-Csv -LiteralPath $runSummaryCsv -NoTypeInformation -Encoding utf8

$aggregate = New-AggregatedSummary -RunSummaries $runSummaries.ToArray()
$aggregatePath = Join-Path $outDir 'aggregate-summary.json'
$aggregate | ConvertTo-Json -Depth 4 | Out-File -FilePath $aggregatePath -Encoding utf8

Write-Host ''
Write-Host '=== Completed measurement runset ===' -ForegroundColor Cyan
Write-Host "Output folder: $outDir"
Write-Host "Per-run summary CSV: $runSummaryCsv"
Write-Host "Aggregate summary JSON: $aggregatePath"

Write-Host ''
Write-Host 'Example usage:' -ForegroundColor DarkGray
Write-Host '  .\scripts\measure-performance.ps1 -Runs 5'
Write-Host '  .\scripts\measure-performance.ps1 -Runs 7 -SampleIntervalSeconds 1'
Write-Host '  .\scripts\measure-performance.ps1 -RunCommand ''dotnet run --project "src/VideoArchiveManager/VideoArchiveManager.csproj" -c Release'''
