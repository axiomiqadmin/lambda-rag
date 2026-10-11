param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipOntologyTests
)

$ErrorActionPreference = 'Stop'
$contractPath = Join-Path $RepositoryRoot 'prompt-contracts\dynamic-document-ontology-v1.md'
$matrixPath = Join-Path $RepositoryRoot 'reports\traceability-matrix.json'
$reportJsonPath = Join-Path $RepositoryRoot 'reports\contract-compliance.json'
$reportMarkdownPath = Join-Path $RepositoryRoot 'reports\contract-compliance-report.md'
$testResult = [ordered]@{ status = 'not-run'; exitCode = $null; summary = 'Ontology tests were not run.' }

if (-not (Test-Path $contractPath)) { throw "Contract is missing: $contractPath" }
if (-not (Test-Path $matrixPath)) { throw "Traceability matrix is missing: $matrixPath" }
$contractText = Get-Content -Raw $contractPath
$matrix = Get-Content -Raw $matrixPath | ConvertFrom-Json
if ($matrix.schemaVersion -ne 'lr-dynamic-ontology-traceability/1.0.0') {
    throw "Unsupported traceability matrix schema: $($matrix.schemaVersion)"
}
if ($matrix.source -ne 'prompt-contracts/dynamic-document-ontology-v1.md') {
    throw 'Traceability matrix points at an unexpected contract.'
}
if ($matrix.requirements.Count -eq 0) { throw 'Traceability matrix contains no requirements.' }

$ids = @{}
$requirementResults = foreach ($requirement in $matrix.requirements) {
    if ([string]::IsNullOrWhiteSpace($requirement.id) -or $ids.ContainsKey($requirement.id)) {
        throw "Missing or duplicate requirement id: $($requirement.id)"
    }
    $ids[$requirement.id] = $true
    if ([string]::IsNullOrWhiteSpace($requirement.sourceRequirement)) {
        throw "Requirement $($requirement.id) has no source requirement text."
    }

    $implementationEvidence = @($requirement.implementationArtifacts)
    $testEvidence = @($requirement.testArtifacts)
    $missingImplementation = @($implementationEvidence | Where-Object {
        -not (Test-Path (Join-Path $RepositoryRoot $_))
    })
    $missingTests = @($testEvidence | Where-Object {
        -not (Test-Path (Join-Path $RepositoryRoot $_))
    })
    $status = $requirement.status
    if ($missingImplementation.Count -gt 0 -or $missingTests.Count -gt 0) { $status = 'evidence-missing' }
    [pscustomobject]@{
        id = $requirement.id
        requirementText = $requirement.sourceRequirement
        implementationEvidence = $implementationEvidence
        testEvidence = $testEvidence
        validationEvidence = @($requirement.validationArtifacts)
        missingImplementationEvidence = $missingImplementation
        missingTestEvidence = $missingTests
        status = $status
    }
}

$contractLineCount = ($contractText -split "`n").Count
if (-not $SkipOntologyTests) {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
    $testOutput = & $dotnet test (Join-Path $RepositoryRoot 'tests\LambdaRag.UnitTests\LambdaRag.UnitTests.csproj') `
        --filter 'FullyQualifiedName~Ontology' --nologo 2>&1
    $testExitCode = $LASTEXITCODE
    $summary = ($testOutput | Select-String 'Test summary:' | Select-Object -Last 1).Line
    $testResult = [ordered]@{
        status = if ($testExitCode -eq 0) { 'passed' } else { 'failed' }
        exitCode = $testExitCode
        summary = $summary
    }
}

$completeCount = @($requirementResults | Where-Object status -eq 'complete').Count
$evidenceMissingCount = @($requirementResults | Where-Object status -eq 'evidence-missing').Count
$overallStatus = if ($completeCount -eq $requirementResults.Count -and $evidenceMissingCount -eq 0 -and $testResult.status -eq 'passed') {
    'complete'
} else {
    'in-progress'
}
$audit = [ordered]@{
    schemaVersion = 'lr-contract-compliance/1.0.0'
    contract = 'prompt-contracts/dynamic-document-ontology-v1.md'
    contractLineCount = $contractLineCount
    asOf = (Get-Date -Format 'yyyy-MM-dd')
    status = $overallStatus
    requirementCount = $requirementResults.Count
    completeCount = $completeCount
    evidenceMissingCount = $evidenceMissingCount
    requirementCoverage = [math]::Round(($completeCount / $requirementResults.Count), 4)
    ontologyTestValidation = $testResult
    requirements = @($requirementResults)
}
$audit | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 $reportJsonPath

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Contract Compliance Audit')
$lines.Add('')
$lines.Add("Status: **$overallStatus**")
$lines.Add("Requirements represented: $($requirementResults.Count); complete: $completeCount; evidence missing: $evidenceMissingCount.")
$lines.Add("Traceability-row coverage: $($audit.requirementCoverage). This is not contract semantic accuracy or overall completion proof.")
$lines.Add("Ontology test validation: $($testResult.status) $($testResult.summary)")
$lines.Add('')
$lines.Add('| ID | Status | Requirement | Missing evidence |')
$lines.Add('|---|---|---|---|')
foreach ($item in $requirementResults) {
    $missing = @($item.missingImplementationEvidence) + @($item.missingTestEvidence)
    $missingText = if ($missing.Count) { $missing -join '; ' } else { '(none)' }
    $safeText = $item.requirementText.Replace('|', '\|').Replace("`n", ' ')
    $lines.Add("| $($item.id) | $($item.status) | $safeText | $missingText |")
}
$lines.Add('')
$lines.Add('The matrix currently represents grouped requirements, not an automated parser proving every prose clause has a row. A non-complete status is retained wherever implementation or test evidence is absent.')
$lines | Set-Content -Encoding utf8 $reportMarkdownPath

Write-Output "Audit status: $overallStatus"
Write-Output "Report: $reportMarkdownPath"
Write-Output "JSON: $reportJsonPath"
if ($testResult.exitCode -ne 0) { exit $testResult.exitCode }