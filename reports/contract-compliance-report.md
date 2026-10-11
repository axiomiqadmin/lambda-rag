# Contract Compliance Audit

Status: **in-progress**
Requirements represented: 30; complete: 0; evidence missing: 0.
Traceability-row coverage: 0. This is not contract semantic accuracy or overall completion proof.
Ontology test validation: passed 

| ID | Status | Requirement | Missing evidence |
|---|---|---|---|
| T01 | partial | Same bytes and accepted artefacts are deterministic; repeated execution is idempotent. | (none) |
| T02 | partial | Canonical parsing and stable section/span identities include parser/canonicalisation versions. | (none) |
| T03 | partial | CandidateOntologyDocument has strict versioned required fields and rejects malformed/extra/duplicate properties. | (none) |
| T04 | partial | Candidate concepts carry proposed fields and no generator-created final concept ID. | (none) |
| T05 | partial | Every source evidence item resolves to actual canonical document spans and fingerprints. | (none) |
| T06 | partial | Accepted concept and relationship models have required fields, allow-listed types, provenance, and content fingerprints. | (none) |
| T07 | partial | Snapshot and delta models are versioned, canonically serialized, and fingerprint-verifiable without volatile time fields. | (none) |
| T08 | partial | Concept IDs are deterministic and candidate identity is reconciled through ordered exact rules; ambiguity never silently merges. | (none) |
| T09 | partial | Only changed canonical spans are sent to generation; unaffected accepted concepts remain byte-stable. | (none) |
| T10 | partial | Generator is provider-neutral and supplies a deterministic fake for tests; live adapter is optional after phases 1-5. | (none) |
| T11 | partial | Candidate caching and replay avoid generator calls on valid hits and fail on fingerprint drift. | (none) |
| T12 | partial | Invalid, missing, ambiguous, unsupported, or unverifiable compilation fails closed without publishing a partial snapshot. | (none) |
| T13 | partial | CLI ontology compile supports initial and parent-snapshot compilation and writes separate artifacts. | (none) |
| T14 | partial | CLI ontology inspect distinguishes accepted and unresolved items and shows provenance. | (none) |
| T15 | partial | CLI ontology diff displays deterministic snapshot changes. | (none) |
| T16 | partial | CLI ontology validate checks schema, evidence, fingerprints, and references without generator calls. | (none) |
| T17 | partial | CLI ontology replay reproduces accepted snapshots without generator calls and verifies canonical identity. | (none) |
| T18 | partial | Artifacts are stored transparently and accepted snapshots are never overwritten. | (none) |
| T19 | partial | Static maps remain functional/default; accepted ontology adds static/dynamic/hybrid selection modes. | (none) |
| T20 | partial | No generator is called from selection, evaluation, scoring, remediation, markup, or report rendering. | (none) |
| T21 | partial | Initial compilation and exact reruns produce valid/reused snapshots without generator calls. | (none) |
| T22 | partial | Local change, addition, alias, and support-aware removal yield justified deltas and retained IDs. | (none) |
| T23 | partial | Ambiguity, hallucinated evidence, unsupported mutation, generator order variation, and cross-document isolation are guarded. | (none) |
| T24 | partial | Existing unit/idempotency/accuracy/golden tests pass and static mode remains functional. | (none) |
| T25 | partial | Benchmark corpus covers required perturbations and records raw stability/provenance/delta/replay metrics without unlabeled accuracy claims. | (none) |
| T26 | partial | Primary Cloud-service-Architecture.docx is processed as acceptance input. | (none) |
| T27 | partial | Contract analysis, gap analysis, and requirement traceability are generated and maintained. | (none) |
| T28 | partial | Independent auditor discovers requirements, implementation, tests, validations, coverage, and evidence. | (none) |
| T29 | partial | Quality gate verifies build, lint, unit, integration, contract, acceptance, and audit outputs. | (none) |
| T30 | partial | Guarantees and non-guarantees are documented; no unsupported semantic-accuracy or production claims. | (none) |

The matrix currently represents grouped requirements, not an automated parser proving every prose clause has a row. A non-complete status is retained wherever implementation or test evidence is absent.
