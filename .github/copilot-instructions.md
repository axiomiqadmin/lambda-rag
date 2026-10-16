# Copilot Instructions — Lambda-RAG

.NET 9 (`global.json`, `Directory.Build.props`) platform for deterministic, auditable rules-based document review. Packages are versioned centrally in `Directory.Packages.props`; `TreatWarningsAsErrors` is on, so any warning breaks the build.

## Build, test, lint

```pwsh
dotnet build
dotnet test                                   # unit + idempotency/golden-master suites

# Single project / single test
dotnet test tests/LambdaRag.UnitTests
dotnet test tests/LambdaRag.IdempotencyTests --filter "FullyQualifiedName~CorpusRegression"
dotnet test tests/LambdaRag.UnitTests --filter "FullyQualifiedName~Ontology"

# Run the CLI
dotnet run --project src/LambdaRag.Cli -- review --document <file> --ruleset <ruleset.json> --out out/x --mode report|markup
dotnet run --project src/LambdaRag.Cli -- topic-map list
```

- Formatting check (used by the quality gate): `dotnet format whitespace LambdaRag.sln --verify-no-changes --no-restore --include <paths>`.
- `scripts/quality_gate.ps1 -Batch quick|idempotency-core|idempotency-other|idempotency-docx|idempotency-review|known-skips|finalize` runs the full gate in batches and writes `reports/quality-gate*.json` / `final_quality_gate.md`. Any failed or skipped test fails the gate.
- CI (`.github/workflows/corpus-regression.yml`) builds Release and runs `tests/LambdaRag.IdempotencyTests` (corpus regression first, then the full suite) on changes to `src/**` or `tests/Goldens/corpus/**`.

## Architecture

Strict **authoring vs. runtime** split (canonical diagram: `docs/diagrams/authoring-vs-runtime.md`; module map: `docs/ARCHITECTURE.md`; stages: `docs/PIPELINE.md`):

- **Authoring (offline, LLM allowed, temp=0, JSON-schema-validated, human-reviewed):** policy → parse → chunk → extraction agent → normalizer → signed, fingerprinted `RuleSet.json` (`LambdaRag.Authoring`, `Authoring.ExtractFunction`, `Persistence`, optional `Indexing`).
- **Runtime (no LLM in the decision loop):** `Parsing` → `Projection` (topic-map, pure code first, AI fallback only when no projector exists and always cached) → doc-kind gate → `Selectors` (JSONPath/regex/topic/heading) → `Evaluation` (Microsoft RulesEngine lambdas) → `Core` `Verdict`/`ComplianceReport` → `Markup` (OpenXml tracked changes/comments in `reviewed.docx`).
- Entry points: `LambdaRag.Cli` (primary), `LambdaRag.Api` (REST).
- `LambdaRag.Core` holds the shared domain types (`Rule`, `RuleSet`, `Verdict`, `SourceSpan`, `Selector` hierarchy with tagged-union JSON converter).
- Review is gated twice before rules run: doc-kind gating (rules whose `appliesToDocKinds` doesn't match emit `Skipped` verdicts; report gets `wrong_profile: true` if all skipped) and `DomainScopeValidator` (`--domain` must match the ruleset's authored domain, otherwise refuse).
- Content lives outside `src/`: `rulesets/` (signed rule sets by domain), `samples/`, `prompt-contracts/` (specs per "pillar"/issue), `policies/`, `docs/`.

## Conventions

- **Four Pillars** (`docs/FOUR-PILLARS.md`): Determinism, Idempotency, Accuracy, Flexibility. Changes must not weaken any silently. Same inputs must give byte-identical `report.json` and OOXML parts in `reviewed.docx`.
  - No wall-clock time, random IDs, or non-deterministic ordering in runtime/markup code (markup uses a fixed timestamp and pinned IDs). Tests in `tests/LambdaRag.IdempotencyTests` (e.g. `RuntimeDeterminismGuardrails`) enforce this.
  - Fingerprint drift in cached/signed artifacts must fail loudly, never silently recompute.
- **Forbidden runtime arrows** (see anti-patterns in the authoring-vs-runtime doc): runtime evaluation/selection/markup must not call an LLM; rules load only from the signed on-disk `RuleSet.json`, never from an Azure AI Search index (`wrong-path-search-index.md`); AI projection fallback must not re-run on cache hit.
- **Golden masters:** corpus goldens in `tests/Goldens/corpus/**` and reviewed-docx goldens are compared byte-for-byte; update them only for intentional behavior changes and note it in `CHANGELOG.md`.
- Rules and prompts are authored from the *policy*, never tuned to a specific reviewed document (Flexibility pillar).
- Accuracy results are tracked in `bench-results/cross-industry-ledger.csv`.
