param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutputDirectory = 'artifacts/benchmark'
)

$ErrorActionPreference = 'Stop'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$cliProject = Join-Path $RepositoryRoot 'src\LambdaRag.Cli\LambdaRag.Cli.csproj'
$outputRoot = Join-Path $RepositoryRoot $OutputDirectory
$workRoot = Join-Path $outputRoot 'corpus'
$artifactRoot = Join-Path $outputRoot 'ontology-artifacts'
New-Item -ItemType Directory -Force $workRoot | Out-Null
New-Item -ItemType Directory -Force $artifactRoot | Out-Null

$baseText = @'
# Service Terms

The service uses private endpoints for all production data access.

Administrators must use multi-factor authentication for privileged access.
'@
$variants = [ordered]@{
    original = $baseText
    exactCopy = $baseText
    formattingOnly = "# Service Terms`r`n`r`n`r`nThe service uses private endpoints for all production data access.`r`n`r`nAdministrators must use multi-factor authentication for privileged access.`r`n"
    paraphrase = "# Service Terms`n`nProduction data is reachable only through private network endpoints.`n`nAdministrators must use multi-factor authentication for privileged access.`n"
    localClauseAddition = "# Service Terms`n`nThe service uses private endpoints for all production data access.`n`nNew audit events are retained for 400 days.`n`nAdministrators must use multi-factor authentication for privileged access.`n"
    localClauseRemoval = "# Service Terms`n`nThe service uses private endpoints for all production data access.`n"
    terminologySubstitution = "# Service Terms`n`nThe service uses isolated connections for all production data access.`n`nAdministrators must use multi-factor authentication for privileged access.`n"
    contradictoryClause = "# Service Terms`n`nThe service uses private endpoints for all production data access.`n`nPublic network access is enabled for all production data.`n`nAdministrators must use multi-factor authentication for privileged access.`n"
    unrelatedSameDomain = "# Disaster Recovery`n`nRecovery point objective is fifteen minutes.`n`nRecovery time objective is four hours.`n"
    differentDomain = "# Clinical Trial`n`nParticipant consent is recorded before enrollment.`n"
}

function Run-Compile([string]$Name, [string]$Text, [string]$FamilySalt, [string]$Version, [string]$SnapshotId, [string]$ParentId, [bool]$UseBaseParent) {
    Write-Host "Benchmark scenario: $Name"
    $documentPath = Join-Path $workRoot "$Name.md"
    [System.IO.File]::WriteAllText($documentPath, $Text, [System.Text.UTF8Encoding]::new($false))
    $familyNamespace = if ($Name -eq 'differentDomain') { 'ontology-benchmark-clinical' } else { 'ontology-benchmark-architecture' }
    $familySaltValue = if ($Name -eq 'differentDomain') { 'different-domain' } else { $FamilySalt }
    $args = @('run', '--no-build', '--project', $cliProject, '--', 'ontology', 'compile',
        '--document', $documentPath,
        '--namespace', $familyNamespace,
        '--registrant', 'benchmark-harness',
        '--salt', $familySaltValue,
        '--version', $Version,
        '--snapshot-id', $SnapshotId,
        '--artifacts', $artifactRoot,
        '--fake-generator')
    if ($UseBaseParent) { $args += @('--parent', $ParentId) }
    $lines = & $dotnet @args 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -notin @(0, 2)) { throw "Scenario $Name failed ($exitCode): $($lines -join "`n")" }
    $pathLine = $lines | Where-Object { $_ -like 'Artifacts:*' } | Select-Object -Last 1
    $artifactPath = if ($pathLine) { $pathLine.Substring('Artifacts:'.Length).Trim() } else { $null }
    $reused = [bool]($lines | Where-Object { $_ -match '^Reused:\s+True$' })
    $audit = if ($artifactPath -and (Test-Path (Join-Path $artifactPath 'audit.json'))) {
        Get-Content -Raw (Join-Path $artifactPath 'audit.json') | ConvertFrom-Json
    } else { $null }
    $snapshotPath = if ($artifactPath -and (Test-Path (Join-Path $artifactPath 'accepted-ontology.json'))) {
        Join-Path $artifactPath 'accepted-ontology.json'
    } else { $null }
    $snapshot = if ($snapshotPath) { Get-Content -Raw $snapshotPath | ConvertFrom-Json } else { $null }
    [pscustomobject]@{
        name = $Name
        exitCode = $exitCode
        documentFingerprint = $audit.documentFingerprint
        status = $audit.status
        reusedSnapshot = $reused
        changedSpanCount = $audit.changedSpanCount
        generatorCallsAvoided = ($reused -or $audit.changedSpanCount -eq 0)
        snapshotId = $snapshot.snapshotId
        snapshotFingerprint = $snapshot.snapshotFingerprint
        conceptIds = if ($snapshot) { @($snapshot.concepts | ForEach-Object conceptId) } else { @() }
        conceptCount = if ($snapshot) { @($snapshot.concepts).Count } else { 0 }
        relationshipCount = if ($snapshot) { @($snapshot.relationships).Count } else { 0 }
        unresolvedSpanCount = $audit.unresolvedSpanCount
        unresolvedOntologyItemCount = $audit.unresolvedOntologyItemCount
        provenanceValid = if ($snapshot) { $true } else { $null }
        stableConceptRetention = $null
        replaySuccess = if ($reused) { $true } else { $null }
        unsupportedConceptRate = $null
        deltaPrecision = $null
        verdictStability = $null
        note = if ($Name -eq 'paraphrase' -or $Name -eq 'terminologySubstitution') { 'Fail-closed span replacement is not semantic paraphrase resolution.' } else { $null }
    }
}

$results = [System.Collections.Generic.List[object]]::new()
$baseResult = Run-Compile 'original' $variants.original 'same-domain' 'v1' 'bench-original' '' $false
$results.Add($baseResult)
$exact = Run-Compile 'exactCopy' $variants.exactCopy 'same-domain' 'v2' 'bench-exact-copy' 'bench-original' $true
$results.Add($exact)
$results.Add((Run-Compile 'formattingOnly' $variants.formattingOnly 'same-domain' 'v2-format' 'bench-format' 'bench-original' $true))
$results.Add((Run-Compile 'paraphrase' $variants.paraphrase 'same-domain' 'v2-paraphrase' 'bench-paraphrase' 'bench-original' $true))
$results.Add((Run-Compile 'localClauseAddition' $variants.localClauseAddition 'same-domain' 'v2-add' 'bench-add' 'bench-original' $true))
$results.Add((Run-Compile 'localClauseRemoval' $variants.localClauseRemoval 'same-domain' 'v2-remove' 'bench-remove' 'bench-original' $true))
$results.Add((Run-Compile 'terminologySubstitution' $variants.terminologySubstitution 'same-domain' 'v2-term' 'bench-term' 'bench-original' $true))
$results.Add((Run-Compile 'contradictoryClause' $variants.contradictoryClause 'same-domain' 'v2-contradiction' 'bench-contradiction' 'bench-original' $true))
$results.Add((Run-Compile 'unrelatedSameDomain' $variants.unrelatedSameDomain 'same-domain' 'v2-unrelated' 'bench-unrelated' 'bench-original' $true))
$different = Run-Compile 'differentDomain' $variants.differentDomain 'different-domain' 'v1' 'bench-different-domain' '' $false
$differentDocumentScoped = @($different.conceptIds | Where-Object { $baseResult.conceptIds -contains $_ }).Count -eq 0
$different | Add-Member -NotePropertyName crossDocumentConceptIsolation -NotePropertyValue $differentDocumentScoped
$results.Add($different)

foreach ($item in $results) {
    if ($item.name -eq 'original' -or $null -eq $item.snapshotId) { continue }
    $retained = @($item.conceptIds | Where-Object { $baseResult.conceptIds -contains $_ }).Count
    $item.stableConceptRetention = if ($baseResult.conceptCount -gt 0) {
        [math]::Round($retained / $baseResult.conceptCount, 4)
    } else { $null }
}

$report = [ordered]@{
    schemaVersion = 'lr-ontology-benchmark/1.0.0'
    generator = 'deterministic-document-span-fake/1.0.0'
    semanticAccuracyClaimed = $false
    independentExpectedLabelsAvailable = $false
    primaryDocument = 'samples/architecture/Cloud-service-Architecture.docx (covered by separate acceptance test)'
    metrics = @{
        stableConceptRetention = 'Per-scenario ID intersection / original concept count; not accuracy.'
        unsupportedConceptRate = 'null: fake generator only emits span-backed candidates; unsupported semantic concepts require independent labels.'
        unresolvedRate = 'Raw unresolved counts are recorded; denominator is not an independently labeled item set.'
        provenanceValidity = 'Structural source-evidence resolution status is recorded for published snapshots.'
        deltaPrecision = 'null: no independently labeled expected delta.'
        generatorCallsAvoided = 'Inferred from exact reuse or zero changed spans.'
        replaySuccess = 'Exact-copy reuse is recorded; replay command is tested separately.'
        verdictStability = 'null: benchmark does not run a labeled ruleset/verdict comparison.'
    }
    scenarios = @($results)
}
$jsonPath = Join-Path $outputRoot 'ontology-benchmark.json'
$markdownPath = Join-Path $outputRoot 'ontology-benchmark.md'
$report | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $jsonPath
$table = @('# Ontology Perturbation Benchmark', '', 'Generator: deterministic span fake; semantic accuracy is not claimed.', '', '| Scenario | Status | Changed spans | Unresolved spans | Concepts | Reused |', '|---|---|---:|---:|---:|---|')
foreach ($item in $results) { $table += "| $($item.name) | $($item.status) | $($item.changedSpanCount) | $($item.unresolvedSpanCount) | $($item.conceptCount) | $($item.reusedSnapshot) |" }
$table += ''
$table += 'Delta precision and verdict stability are unreported because there are no independently labeled expected deltas or verdicts. These measurements are deterministic pipeline diagnostics, not accuracy.'
$table | Set-Content -Encoding utf8 $markdownPath
Write-Output "Benchmark report: $jsonPath"
Write-Output "Scenarios: $($results.Count)"