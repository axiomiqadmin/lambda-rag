# Dynamic Document Ontology v1 Gap Analysis

**As of:** 2026-10-11  
**Status:** In progress; not a compliance sign-off.  
**Evidence basis:** Source inspection, focused/full unit results, DOCX fake acceptance, and CLI artifact checks. No live LLM/Azure execution.

## Existing Work

- Static maps: `src/LambdaRag.Projection/TopicMap.cs`, `TopicMapRegistry.cs`, embedded `TopicMaps/*.json`, and `DeterministicContractProjector.cs` classify/project sections. CLI `topic-map` and `--topic-map` support the static path.
- Canonical document: `SourceDocument`, `ParserRegistry`, and Markdown/DOCX/PDF parsers produce `ParsedDocument`/`ContentBlock` with parser metadata and spans.
- Fingerprinting/canonical JSON: `ContentHash`, `CanonicalJson`, plus ontology-specific NFC/control-rejecting canonical JSON and length-prefixed fingerprints.
- Span proof: `DocumentFamilyManifest`, `SpanObservationBuilder`, `DeterministicSpanReconciler`, result models, and parser-to-span adapter. Strict duplicate ambiguity is unresolved; split/merge mint new IDs.
- Ontology contracts currently present: strict candidate parsing, accepted concepts/relationships, snapshots/deltas, versioned canonical-document/audit artifacts, deterministic IDs/fingerprints, and parsed-span evidence resolution.
- Generator/cache/replay: provider-neutral interface, deterministic fake, complete request fingerprint, immutable file/in-memory cache, and cache-only replay are implemented. No live provider adapter is configured.
- Compiler/store/CLI: compiler blocks unresolved spans, generates only new spans, reuses exact snapshots, and persists immutable artifacts. CLI exposes compile/inspect/diff/validate/replay. Static mode remains the default; accepted snapshots can feed static/dynamic/hybrid topic-map adapters.
- Acceptance evidence: Cloud-service Architecture DOCX compiles with the clearly marked fake; five core artifacts validate; CLI cache replay and dynamic projection smoke tests pass. `artifacts/golden/` and `artifacts/sample-run/` contain deterministic fake evidence, not semantic gold labels.
- Benchmark harness: `scripts/ontology_benchmark.ps1` runs ten perturbation scenarios and leaves accuracy-like metrics unclaimed without independently labeled truth.
- Static regression tests cover topic-map registry/projector/vocabulary; ontology tests cover contracts, reconciliation, cache/replay, compiler, store, runtime adapter, and the primary DOCX.

## Gaps by Capability

| Capability | Current status | Evidence / gap |
|---|---|---|
| Parser-to-span handoff | Partial | Markdown/DOCX adapter and acceptance test pass; no checked-in PDF fixture, so PDF parser behavior is unverified here. |
| Canonical document/request fingerprint | Partial | Complete request key binds document, parser/canonicalization, changed spans, accepted context, contract, generator, and settings; normalization beyond parser output remains parser-owned. |
| Candidate contracts | Partial | Strict typed exact-field validation is implemented; there are no standalone JSON Schema files. |
| Accepted artifacts | Partial | Snapshot/delta parsers, fingerprints, immutable store, and parent lookup exist; snapshot ID is caller-supplied. |
| Concept identity | Partial | Exact key/alias/span/mapping stages and family-scoped IDs exist. Semantic quality is unproven because only a deterministic fake is available. |
| Provenance validation | Partial | Evidence resolves document fingerprint, span ID, ancestry path, offsets, and exact text fingerprint against parsed spans; PDF path is unverified. |
| Candidate generator | Partial | Interface, fake, cache, and replay exist. A live provider adapter is absent because no Azure model deployment is available. |
| Cache/replay | Partial | Candidate cache and accepted store are immutable; CLI replay works without calls. Cache corruption/multi-process stress coverage can be broadened. |
| Snapshot compilation | Partial | Initial/incremental compile, exact reuse, local generation scope, fail-closed publication, and no-overwrite storage exist; relationship lifecycle coverage remains focused, not exhaustive. |
| CLI | Partial | All five commands exist. Automated CLI coverage for malformed paths and every multi-version case remains limited. |
| Runtime integration | Partial | CLI review/project and API review accept opt-in `static`/`dynamic`/`hybrid`; static remains default. Evaluation sees only the frozen projected graph. API dynamic review was smoke-tested with the small cloud ruleset. |
| Benchmark harness | Partial | Ten perturbations and raw diagnostics are emitted; no independent labels, so delta precision and verdict stability remain null. |
| Primary DOCX acceptance | Partial | Offline fake acceptance passes, artifacts validate, replay succeeds. This is pipeline/provenance acceptance, not semantic extraction validation. |
| Traceability/auditor/quality gate | Partial | Matrix, auditor, and gate exist; matrix rows are grouped and cannot prove one-to-one coverage of every prose clause. |

## Existing Verification and Limits

- Latest completed ontology test checkpoint before final matrix refresh: 107 passed, 0 failed, 0 skipped.
- Latest completed full unit checkpoint: 960 passed, 0 failed, 0 skipped; rerun is required after final edits.
- Idempotency suite reports two pre-existing skips: bootstrap-only benchmark and ruleset-pull test blocked on an unrelated CLI feature. A full run was cancelled by MSBuild at about 244 seconds; a no-build/no-restore retry is still running, so no final idempotency summary is claimed yet.
- The Cloud-service Architecture DOCX fake acceptance passed; core snapshot/candidate/delta/canonical-document/audit artifacts passed strict CLI validation; cache-only replay and dynamic projection smoke tests succeeded.
- No live LLM/Azure deployment exists. Fake extraction validates plumbing and determinism, not semantic extraction quality or provider behavior.

## Highest-Priority Remaining Risks

1. The fake generator echoes spans into concepts; it proves artifact plumbing and deterministic boundaries, not ontology extraction semantics.
2. A real provider adapter/model deployment is unavailable and untested.
3. Dynamic labels and aliases adapt to the existing keyword projector; rule predicates are not yet semantic-key aware end-to-end.
4. CLI/API dynamic modes currently adapt accepted concept labels and aliases to the existing keyword projector; rule predicates are not yet semantic-key aware end-to-end.
5. Benchmark outcomes lack independent gold labels; unsupported concept rate, delta precision, and verdict stability cannot be claimed from fake output.
6. Existing idempotency skips and the cancelled run prevent a no-skips quality-gate pass as currently specified.

## Next Implementation Order

1. Finish final post-edit full unit, CLI, benchmark, acceptance-artifact, and idempotency verification.
2. Refresh auditor and quality-gate reports with final command summaries, including skipped/cancelled outcomes.
3. Obtain a live generator deployment and independently labeled expected concepts/deltas before evaluating semantic quality claims.