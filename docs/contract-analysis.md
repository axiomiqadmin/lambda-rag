# Dynamic Document Ontology v1 Contract Analysis

**Source:** `prompt-contracts/dynamic-document-ontology-v1.md`  
**Analysis scope:** Functional behavior, deterministic guarantees, data contracts, boundaries, tests, artifacts, and completion criteria.  
**Status:** Requirements extracted; implementation is in progress. This document does not assert compliance.

## Objective and Invariants

Dynamic ontology v1 replaces manually maintained static topic vocabularies as the eventual runtime source while keeping accepted ontology document-scoped, immutable, versioned, provenance-grounded, replayable, and outside the LLM verdict path. Existing static topic maps must remain available during migration.

The governing invariants are deterministic output for identical inputs and accepted artifacts; idempotent reruns; schema-validated/content-addressed/versioned accepted artifacts; complete span provenance; deterministic rule evaluation; no direct LLM influence on verdicts; fail-closed behavior for invalid, unavailable, incomplete, stale, or ambiguous inputs; and byte-stable preservation of unaffected concepts.

## Functional Requirements

1. Parse canonical document bytes and derive document, section, and span fingerprints/identities.
2. Compare a new document version deterministically and send only changed spans plus relevant accepted concepts to candidate extraction.
3. Produce candidate concepts, aliases, relationships, evidence, and diagnostics under strict versioned contracts. Candidate output has no final concept IDs.
4. Validate candidate schema, duplicate keys/IDs, allowed relationships, span existence, source fingerprints, and internal references before reconciliation.
5. Reconcile candidates in deterministic order: semantic key, same-type normalized alias, source-span continuity, explicit mappings, then unresolved. Similarity is never final identity authority.
6. Produce immutable document-scoped accepted snapshots and deterministic deltas. Preserve unchanged concepts byte-for-byte; additions, augmentation, aliases, relationships, and retirement are evidence-backed.
7. Support deterministic cache reuse and replay without an external generator call. A cache miss with an unavailable generator fails closed.
8. Keep the runtime selection/evaluation path limited to accepted snapshots. Preserve static mode and provide explicit static/dynamic/hybrid selection with backwards-compatible defaults.
9. Expose compile, inspect, diff, validate, and replay CLI operations with separately written candidate/delta/snapshot/audit artifacts.
10. Exercise the pipeline with unit, integration, regression, contract, benchmark-corpus, and primary DOCX acceptance tests.

## Data Contracts

- `CandidateOntologyDocument`: schema version, source document fingerprint, extraction contract version, generator identity, concepts, relationships, diagnostics.
- `CandidateConcept`: proposed label/description/type, aliases, source evidence, proposed semantic key, diagnostic confidence; never a final ID.
- `SourceEvidence`: document fingerprint, section ID, span ID, half-open offsets, exact text fingerprint; evidence must resolve against canonical parsed content.
- `AcceptedConcept`: deterministic concept ID, canonical label/description/type, aliases, status, provenance, creation/change snapshot IDs, content fingerprint.
- `OntologyRelationship`: deterministic relationship ID, allow-listed type, source/target accepted IDs, provenance, status, content fingerprint.
- `OntologySnapshot`: schema, ontology/snapshot IDs, document fingerprint, parent ID, concepts, relationships, unresolved items, generator/canonicalization/pipeline versions, snapshot fingerprint.
- `OntologyDelta`: base snapshot, target document fingerprint, additions, augmentations, retirements, alias/relationship changes, unresolved items, delta fingerprint.

## Non-Functional and Canonicalization Requirements

Canonical JSON uses ordinal object-key ordering and stable semantic-array ordering; strings use explicitly versioned Unicode and whitespace normalization; duplicate keys and IDs are rejected; absent optional values have one representation; environment values and runtime timestamps do not affect fingerprints; fingerprints reuse the repository SHA-256 `ContentHash` convention. Snapshot publication is immutable/no-overwrite. Replay verifies canonical identity. Diagnostics are structured. No graph database or universal ontology is required.

## Security and Failure Boundaries

Reject invalid candidate JSON, missing or fabricated evidence, conflicting semantic keys, unsupported relationships, unknown relationship endpoints, incompatible concept redefinitions, prohibited predicate changes, ambiguous identity, unverifiable snapshots, stale/mismatched cache entries, and generator unavailability without a valid cached artifact. No partial accepted snapshot is published. No LLM call occurs in selection, evaluation, scoring, remediation-for-verdict, markup, or report rendering.

## Validation Scenarios

The contract requires initial compilation, exact rerun/no-call reuse, local changes, additions, aliases, removals/support retention, ambiguity, hallucinated evidence, generator-order variation, unsupported mutation, cross-document isolation, and unchanged static-mode regressions. The benchmark harness additionally covers exact/formatting copies, paraphrase, local add/remove, terminology substitution, contradiction, same-domain unrelated documents, and different-domain documents; it records retention, unsupported rate, unresolved rate, provenance validity, delta precision, avoided calls, replay success, and applicable verdict stability. Metrics are not accuracy claims without independently labeled expected results.

## Deliverable and Completion Interpretation

The contract's Definition of Done has 12 gates, from compiling a previously unseen document into a fully grounded accepted ontology through exact-rerun reuse, local deltas, stable IDs, fail-closed ambiguity, replay, static-mode regression, tests, and documented guarantees/non-guarantees.

The contract's final “First Instruction” says discovery only. The newer session-steering instruction explicitly authorizes proceeding through implementation and validation; this analysis therefore records the complete target while implementation proceeds in small phases. Optional provider integration remains excluded unless separately made possible; deterministic fake/cache/replay must work without Azure or a live LLM.