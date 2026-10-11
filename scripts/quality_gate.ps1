param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [ValidateSet('quick', 'idempotency-core', 'idempotency-other', 'idempotency-docx', 'idempotency-review', 'known-skips', 'finalize')]
    [string]$Batch = 'quick'
)

$ErrorActionPreference = 'Continue'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$resultsPath = Join-Path $RepositoryRoot 'reports\quality-gate-results.json'
$jsonPath = Join-Path $RepositoryRoot 'reports\quality-gate.json'
$markdownPath = Join-Path $RepositoryRoot 'reports\final_quality_gate.md'
$results = [System.Collections.Generic.List[object]]::new()
if (Test-Path $resultsPath) {
    foreach ($existing in (Get-Content -Raw $resultsPath | ConvertFrom-Json)) { $results.Add($existing) }
}

function Save-GateReports([string]$batchName) {
    $failed = @($results | Where-Object status -eq 'failed')
    $skipped = @($results | Where-Object status -eq 'skipped')
    $inProgress = @($results | Where-Object status -eq 'in-progress')
    $requiredNames = @(
        'Core build', 'CLI build', 'API build', 'C# formatting', 'Full unit tests', 'Ontology contract tests',
        'DOCX acceptance test', 'Ontology benchmark', 'Contract audit',
        'Idempotency accuracy', 'Idempotency corpus', 'Idempotency runtime',
        'Idempotency ARB benchmark', 'Idempotency non-DOCX miscellaneous',
        'Idempotency reviewed DOCX', 'Idempotency review pipeline', 'Known skipped tests')
    $missing = @($requiredNames | Where-Object { $name = $_; -not ($results | Where-Object name -eq $name) })
    $status = if ($failed.Count -gt 0 -or $skipped.Count -gt 0) {
        'failed'
    } elseif ($missing.Count -gt 0 -or $inProgress.Count -gt 0) {
        'in-progress'
    } else {
        'passed'
    }
    $report = [ordered]@{
        schemaVersion = 'lr-quality-gate/1.0.0'
        asOf = (Get-Date -Format 'yyyy-MM-dd')
        batch = $batchName
        status = $status
        allRequirementsCompleteClaimed = $false
        note = if ($skipped.Count -gt 0) {
            'Known skipped tests fail the requested no-skips gate; see each test output and contract-compliance limitations.'
        } elseif ($missing.Count -gt 0 -or $inProgress.Count -gt 0) {
            "Validation remains incomplete. Missing: $($missing -join ', '); in progress: $(($inProgress | ForEach-Object name) -join ', ')."
        } else {
            'All configured commands exited successfully with no skipped tests; this does not establish semantic accuracy.'
        }
        missingChecks = $missing
        results = @($results)
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 $jsonPath
    $lines = @(
        '# Quality Gate',
        '',
        "Status: **$status**",
        "Last batch: $batchName",
        '',
        $report.note,
        '',
        '| Check | Status | Summary |',
        '|---|---|---|'
    )
    foreach ($result in $results) {
        $safeSummary = ([string]$result.summary).Replace('|', '\|')
        $lines += "| $($result.name) | $($result.status) | $safeSummary |"
    }
    $lines | Set-Content -Encoding utf8 $markdownPath
    $results | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 $resultsPath
    Write-Output "Quality gate: $status"
    Write-Output "Report: $markdownPath"
    Write-Output "JSON: $jsonPath"
    return $status
}

function Invoke-GateCommand([string]$Name, [string]$File, [string[]]$Arguments) {
    Write-Output "=== $Name ==="
    $output = & $File @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $summaryLine = ($output | Select-String 'Test summary:|Build succeeded|Build FAILED|Passed!|Failed!|Audit status:|Benchmark report:|Scenarios:' | Select-Object -Last 1).Line
    $summaryText = if ($summaryLine) { $summaryLine } else { ($output | Select-Object -Last 1 | ForEach-Object { $_.ToString() }) -join '' }
    $isSkipped = $output -match '\[SKIP\]'
    $result = [pscustomobject]@{
        name = $Name
        command = "$File $($Arguments -join ' ')"
        exitCode = $exitCode
        status = if ($isSkipped) { 'skipped' } elseif ($exitCode -eq 0) { 'passed' } else { 'failed' }
        summary = $summaryText
        outputTail = @($output | Select-Object -Last 10 | ForEach-Object { $_.ToString() })
    }
    if ($Name -eq 'Contract audit' -and $exitCode -eq 0) {
        $auditPath = Join-Path $RepositoryRoot 'reports\contract-compliance.json'
        if (Test-Path $auditPath) {
            $auditStatus = (Get-Content -Raw $auditPath | ConvertFrom-Json).status
            if ($auditStatus -ne 'complete') { $result.status = 'in-progress' }
        } else {
            $result.status = 'failed'
        }
    }
    $prior = @($results | Where-Object name -ne $Name)
    $results.Clear()
    foreach ($entry in $prior) { $results.Add($entry) }
    $results.Add($result)
    $output | Select-Object -Last 10
    [void](Save-GateReports $Batch)
    if ($exitCode -ne 0) { throw "$Name failed with exit code $exitCode." }
}

$unitProject = Join-Path $RepositoryRoot 'tests\LambdaRag.UnitTests\LambdaRag.UnitTests.csproj'
$idempotencyProject = Join-Path $RepositoryRoot 'tests\LambdaRag.IdempotencyTests\LambdaRag.IdempotencyTests.csproj'
$cliProject = Join-Path $RepositoryRoot 'src\LambdaRag.Cli\LambdaRag.Cli.csproj'

switch ($Batch) {
    'quick' {
        Invoke-GateCommand 'Core build' $dotnet @('build', (Join-Path $RepositoryRoot 'src\LambdaRag.Core\LambdaRag.Core.csproj'), '--nologo')
        Invoke-GateCommand 'CLI build' $dotnet @('build', $cliProject, '--nologo')
        $apiCheckOutput = Join-Path $env:TEMP 'lambda-rag-quality-gate-api\'
        Invoke-GateCommand 'API build' $dotnet @('build', (Join-Path $RepositoryRoot 'src\LambdaRag.Api\LambdaRag.Api.csproj'), '--no-restore', '--nologo', "-p:OutputPath=$apiCheckOutput")
        Invoke-GateCommand 'C# formatting' $dotnet @('format', 'whitespace', (Join-Path $RepositoryRoot 'LambdaRag.sln'), '--verify-no-changes', '--no-restore', '--include', 'src/LambdaRag.Core/Ontology', 'src/LambdaRag.Cli/OntologyCommand.cs', 'src/LambdaRag.Projection/AcceptedOntologyTopicMapAdapter.cs', 'src/LambdaRag.Api/Program.cs', 'tests/LambdaRag.UnitTests/Ontology', 'tests/LambdaRag.UnitTests/Projection/AcceptedOntologyTopicMapAdapterTests.cs')
        Invoke-GateCommand 'Full unit tests' $dotnet @('test', $unitProject, '--nologo')
        Invoke-GateCommand 'Ontology contract tests' $dotnet @('test', $unitProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~Ontology', '--nologo')
        Invoke-GateCommand 'DOCX acceptance test' $dotnet @('test', $unitProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~CloudServiceArchitectureAcceptanceTests', '--nologo')
        Invoke-GateCommand 'Ontology benchmark' 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $RepositoryRoot 'scripts\ontology_benchmark.ps1'), '-RepositoryRoot', $RepositoryRoot)
        Invoke-GateCommand 'Contract audit' 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $RepositoryRoot 'tools\contract_auditor\audit.ps1'), '-RepositoryRoot', $RepositoryRoot)
    }
    'idempotency-core' {
        Invoke-GateCommand 'Idempotency accuracy' $dotnet @('test', $idempotencyProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~AccuracyHarness', '--nologo')
        Invoke-GateCommand 'Idempotency corpus' $dotnet @('test', $idempotencyProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~CorpusRegression', '--nologo')
        Invoke-GateCommand 'Idempotency runtime' $dotnet @('test', $idempotencyProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~RuntimeDeterminismGuardrails', '--nologo')
        Invoke-GateCommand 'Idempotency ARB benchmark' $dotnet @('test', $idempotencyProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~ArbPsaBenchmark&FullyQualifiedName!~Benchmark_matches_committed_golden_master', '--nologo')
    }
    'idempotency-other' {
        Invoke-GateCommand 'Idempotency non-DOCX miscellaneous' $dotnet @('test', $idempotencyProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~AdditiveGuaranteeTests|FullyQualifiedName~ArbPsaDiagnostics|FullyQualifiedName~DomainMismatchGuardrailTests', '--nologo')
    }
    'idempotency-docx' {
        Invoke-GateCommand 'Idempotency reviewed DOCX' $dotnet @('test', $idempotencyProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~ReviewedDocxIdempotency', '--nologo')
    }
    'idempotency-review' {
        Invoke-GateCommand 'Idempotency review pipeline' $dotnet @('test', $idempotencyProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~ReviewPipelineIdempotency', '--nologo')
    }
    'known-skips' {
        Invoke-GateCommand 'Known skipped tests' $dotnet @('test', $idempotencyProject, '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~Benchmark_matches_committed_golden_master|FullyQualifiedName~Snapshot_pull_is_byte_identical_across_runs', '--nologo')
    }
    'finalize' { }
}

[void](Save-GateReports $Batch)