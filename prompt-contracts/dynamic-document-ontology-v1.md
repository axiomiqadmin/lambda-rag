# Prompt Contract: Dynamic Document Ontology v1

## Role

You are modifying the Lambda-RAG repository.

Lambda-RAG is a deterministic, auditable rules-over-documents engine. Its core invariants are:

1. Same input bytes plus the same accepted artefacts must produce the same output.
2. Repeated execution must be idempotent.
3. LLM output must never directly determine a runtime verdict.
4. Every accepted artefact must be schema-validated, content-addressed, versioned and traceable to its source.
5. Existing deterministic rule evaluation, provenance, markup and report generation must remain intact.
6. Existing tests must continue to pass unless a test is intentionally replaced by a stricter equivalent.

Do not weaken these invariants.

## Objective

Remove the runtime dependency on manually maintained static topic ontologies.

Implement a document-scoped dynamic ontology compilation pipeline that:

1. Derives a candidate ontology from a document during an authoring or compilation phase.
2. Freezes the accepted ontology before rule selection and evaluation.
3. Reuses the frozen ontology when the document content has not changed.
4. Produces a deterministic ontology delta when a previously processed document changes.
5. Retains unchanged concept identifiers and accepted concept definitions.
6. Adds, supersedes or retires concepts only when supported by changed document spans.
7. Keeps all LLM activity outside the deterministic verdict decision loop.
8. Preserves complete provenance from every concept to the originating document spans.
9. Supports deterministic replay without calling an LLM when cached accepted artefacts exist.
10. Fails closed when ontology generation is invalid, ambiguous, incomplete or unavailable.

This is not a request to create a globally self-modifying ontology.

The initial implementation must create immutable, document-scoped ontology snapshots.

## Required Execution Model

Implement the following pipeline:

```text
Document bytes
→ canonical document parsing
→ stable document fingerprint
→ stable section and span identities
→ deterministic comparison with previous document version
→ candidate concept extraction
→ candidate relationship extraction
→ schema validation
→ deterministic ontology reconciliation
→ immutable accepted ontology snapshot
→ deterministic topic selection
→ existing deterministic rule evaluation
→ existing report and markup generation
```

The accepted ontology snapshot becomes an input to evaluation.

The LLM-generated candidate ontology must never be read directly by the selection or evaluation stages.

## Terminology

Use these terms consistently:

- **Candidate ontology:** Untrusted structured output produced by an ontology generator.
- **Accepted ontology:** Validated, reconciled and immutable ontology used by deterministic runtime components.
- **Ontology snapshot:** A versioned accepted ontology associated with a specific canonical document fingerprint.
- **Ontology delta:** A deterministic set of proposed additions, updates, retirements, aliases and relationship changes between two ontology snapshots.
- **Concept:** A canonical semantic unit grounded in one or more document spans.
- **Alias:** A document phrase referring to an existing concept.
- **Provenance:** The source document, section, span, offsets, text fingerprint and extraction version supporting a concept or relationship.
- **Stable concept ID:** An identifier derived by deterministic code. It must not be invented by the LLM.

## Architectural Constraints

### LLM boundaries

The LLM may:

- Propose concept labels.
- Propose concept descriptions.
- Propose aliases.
- Propose parent-child relationships.
- Propose related-to relationships.
- Classify concept type.
- Identify the source spans supporting each proposal.
- State extraction confidence for diagnostics.

The LLM must not:

- Generate final concept IDs.
- Directly update an accepted ontology.
- Delete existing concepts.
- Retire existing concepts without changed-source evidence.
- Determine whether a rule passes, fails, is a gap or is not applicable.
- Change predicates or rule semantics.
- Select final document sections for rule evaluation.
- Resolve ambiguous concept identity without deterministic validation.
- Introduce concepts without source-span evidence.
- Rewrite unchanged ontology entries.

### Deterministic code responsibilities

Pure deterministic code must:

- Canonicalise document content.
- Compute document, section, span and artefact fingerprints.
- Detect changed, added, removed and unchanged spans.
- Generate stable concept IDs.
- Validate all candidate output against a strict schema.
- Reject unsupported concepts and relationships.
- Match candidates to existing concepts.
- Preserve unchanged concept IDs.
- Apply accepted deltas in a stable order.
- Serialise accepted ontology snapshots canonically.
- Cache and retrieve accepted snapshots.
- Detect ambiguity and fail closed.
- Produce complete audit events.
- Supply only accepted snapshots to runtime selection and evaluation.

## Stable Identity Requirements

Do not derive concept identity from an LLM-generated label alone.

Create stable concept IDs using deterministic inputs such as:

- ontology namespace
- document family identifier
- normalised concept type
- canonical semantic key
- stable originating source identity

The implementation must define and document the identity algorithm.

Renaming a label must not automatically create a new concept if deterministic reconciliation proves the semantic identity is unchanged.

Changes to descriptive text must not change the concept ID.

Concept IDs must remain stable across document versions when their semantic identity and supporting content remain materially unchanged.

If identity cannot be established without ambiguity, do not merge automatically.

Emit an unresolved reconciliation record and fail the compilation unless an explicit policy permits isolated new concepts.

## Incremental Update Requirements

When a new document version is provided:

1. Compute its canonical fingerprint.
2. If the fingerprint is unchanged, return the existing accepted ontology snapshot.
3. If changed, compare canonical spans against the previous version.
4. Send only new or materially changed spans to the candidate ontology generator.
5. Include the relevant existing accepted concepts as read-only reconciliation context.
6. Do not ask the LLM to regenerate the complete ontology.
7. Reconcile candidate concepts against the accepted ontology using deterministic code.
8. Preserve all unaffected concepts byte-for-byte in canonical serialisation.
9. Mark concepts whose only supporting spans were removed as retirement candidates.
10. Do not retire concepts still supported by unchanged spans.
11. Create a new immutable ontology snapshot.
12. Record the parent snapshot fingerprint and the applied delta fingerprint.

## Data Contracts

Create explicit versioned schemas for at least:

### CandidateOntologyDocument

Required fields:

- `schemaVersion`
- `sourceDocumentFingerprint`
- `extractionContractVersion`
- `generatorIdentity`
- `concepts`
- `relationships`
- `diagnostics`

### CandidateConcept

Required fields:

- `proposedLabel`
- `proposedDescription`
- `conceptType`
- `aliases`
- `sourceEvidence`
- `proposedSemanticKey`
- `diagnosticConfidence`

No final concept ID may be provided by the generator.

### SourceEvidence

Required fields:

- `documentFingerprint`
- `sectionId`
- `spanId`
- `startOffset`
- `endOffset`
- `exactTextFingerprint`

The evidence must reference content that exists in the canonical parsed document.

Do not rely on copied quotation text as the evidence identity.

### AcceptedConcept

Required fields:

- `conceptId`
- `canonicalLabel`
- `description`
- `conceptType`
- `aliases`
- `status`
- `provenance`
- `createdInSnapshot`
- `lastChangedInSnapshot`
- `contentFingerprint`

### OntologyRelationship

Required fields:

- `relationshipId`
- `relationshipType`
- `sourceConceptId`
- `targetConceptId`
- `provenance`
- `status`
- `contentFingerprint`

Support only an explicit allow-list of relationship types in v1.

Suggested v1 relationship types:

- `parent-of`
- `child-of`
- `alias-of`
- `related-to`
- `requires`
- `prohibits`

Do not infer transitive relationships unless deterministic code explicitly implements and tests that behaviour.

### OntologySnapshot

Required fields:

- `schemaVersion`
- `ontologyId`
- `snapshotId`
- `sourceDocumentFingerprint`
- `parentSnapshotId`
- `concepts`
- `relationships`
- `unresolvedItems`
- `generatorContractVersion`
- `canonicalisationVersion`
- `createdByPipelineVersion`
- `snapshotFingerprint`

Avoid runtime timestamps in canonical fingerprint material.

### OntologyDelta

Required fields:

- `baseSnapshotId`
- `targetDocumentFingerprint`
- `addedConcepts`
- `augmentedConcepts`
- `retiredConcepts`
- `aliasChanges`
- `relationshipChanges`
- `unresolvedItems`
- `deltaFingerprint`

## Canonicalisation Requirements

Canonical serialisation must be deterministic.

At minimum:

- Use ordinal key ordering.
- Use stable array ordering.
- Normalise supported whitespace consistently.
- Define Unicode normalisation explicitly.
- Exclude volatile timestamps and environment-specific values from fingerprints.
- Reject duplicate keys.
- Reject duplicate concept IDs.
- Reject unsupported relationship types.
- Represent absent optional values consistently.
- Use the repository's existing fingerprinting approach where available instead of creating a competing implementation.

The same accepted semantic content must serialise to the same bytes.

## Generator Abstraction

Create an interface similar in intent to:

```csharp
public interface ICandidateOntologyGenerator
{
    Task<CandidateOntologyDocument> GenerateAsync(
        OntologyGenerationRequest request,
        CancellationToken cancellationToken);
}
```

The request must include:

- canonical document fingerprint
- changed canonical spans
- relevant accepted concepts as read-only context
- schema version
- extraction contract version
- deterministic generation settings
- optional cached generation result

Do not couple the core pipeline directly to one LLM provider.

Provide:

1. A fake deterministic generator for tests.
2. A cached generator decorator.
3. A provider adapter only if the repository already contains an appropriate AI abstraction.
4. A replay mode that performs no external model call.

## Reconciliation

Implement reconciliation as deterministic code.

The reconciler must classify every candidate as one of:

- Exact existing concept
- Existing concept alias
- Existing concept augmentation
- New concept
- Potential conflict
- Ambiguous
- Invalid
- Unsupported

Use explicit deterministic comparison stages.

Suggested order:

1. Exact stable semantic key match.
2. Exact normalised alias match within the same concept type.
3. Exact supporting-span continuity from the parent snapshot.
4. Explicit deterministic mapping rules.
5. Otherwise unresolved.

Do not use an embedding similarity threshold as the final authority in v1.

Embeddings may eventually provide candidate suggestions, but they must not silently merge or replace accepted concepts.

## Fail-Closed Behaviour

Ontology compilation must fail without publishing a new accepted snapshot when:

- Candidate JSON is invalid.
- Required provenance is absent.
- Evidence references nonexistent spans.
- Duplicate semantic keys conflict.
- An existing concept would be silently redefined.
- A relationship references an unknown concept.
- Candidate output attempts to change rule predicates.
- Reconciliation is ambiguous beyond the configured threshold.
- An accepted snapshot fingerprint cannot be reproduced.
- A cached candidate artefact does not match its source fingerprint.
- The generator is unavailable and no accepted cached artefact exists.

Return structured diagnostics, not a partial runtime ontology.

## CLI Behaviour

Add or extend CLI commands in the project's established style.

Required capabilities:

```text
ontology compile
ontology inspect
ontology diff
ontology validate
ontology replay
```

Expected behaviours:

### ontology compile

- Build the first snapshot for a document.
- Accept an optional parent snapshot for incremental compilation.
- Write candidate, delta, accepted snapshot and audit outputs separately.

### ontology inspect

- Display concepts, aliases, relationships and provenance.
- Clearly distinguish accepted and unresolved items.

### ontology diff

- Compare two accepted snapshots.
- Display additions, augmentations, retirements, alias changes and relationship changes.

### ontology validate

- Validate schema, provenance, fingerprints and internal references.
- Make no LLM call.

### ontology replay

- Reconstruct and verify an accepted snapshot from cached artefacts.
- Make no LLM call.
- Fail if byte-equivalent or canonical-equivalent reproduction is not achieved, according to repository policy.

Follow existing CLI conventions. Do not create an unrelated command framework.

## Persistence Layout

Use a transparent file-based layout for v1 unless the existing repository provides a more appropriate abstraction.

Suggested logical structure:

```text
ontology-artifacts/
  {document-family-id}/
    documents/
      {document-fingerprint}/
        canonical-document.json
        candidate-ontology.json
        ontology-delta.json
        accepted-ontology.json
        audit.json
```

The exact paths may change to fit repository conventions.

Do not overwrite an existing accepted snapshot.

## Runtime Integration

Refactor topic selection behind an abstraction that can consume an accepted ontology snapshot.

Requirements:

- Existing static topic maps must continue to work during migration.
- Add dynamic accepted ontology support as a new implementation.
- Do not delete existing topic maps in the first change.
- Create a compatibility adapter where practical.
- Add an explicit mode such as `static`, `dynamic` or `hybrid`.
- Default behaviour must remain backwards compatible until tests prove the dynamic path.

The dynamic ontology must be frozen before deterministic rule selection begins.

No generator call is permitted from:

- rule selection
- rule evaluation
- scoring
- remediation generation used in verdict calculation
- markup
- report rendering

## Testing Requirements

Do not claim success based only on the existing test documents.

Add tests for:

### Initial compilation

- A previously unseen document produces a valid accepted ontology.
- Every accepted concept has valid source evidence.
- Repeated compilation with cached artefacts produces the same snapshot fingerprint.
- Replay can reproduce the accepted snapshot without an LLM call.

### Exact rerun

- Identical document bytes reuse the same accepted snapshot.
- No generator invocation occurs.
- Output is byte-identical or canonical-byte-identical according to repository guarantees.

### Local document change

- Change one paragraph.
- Only that paragraph and required context are sent to the generator.
- Unaffected concepts retain IDs and canonical bytes.
- The delta contains only justified changes.

### Addition

- Add a genuinely new concept.
- One new accepted concept is created.
- Existing concept identifiers remain unchanged.

### Alias

- Add a new phrase for an existing concept.
- The existing concept ID is retained.
- The alias is added through an explicit delta.

### Removal

- Remove one supporting span.
- A concept is not retired if other unchanged supporting spans remain.
- A concept becomes a retirement candidate when its final support is removed.

### Ambiguity

- Add wording that may refer to two existing concepts.
- The reconciler must not select one silently.
- Compilation fails closed or records the item as unresolved according to explicit policy.

### Hallucinated evidence

- Candidate output references a nonexistent span.
- Validation rejects the entire candidate artefact.

### Generator variation

- Two fake generator responses present concepts in different orders.
- Canonical accepted serialisation remains stable when semantic content is equivalent.

### Unsupported mutation

- Candidate output attempts to redefine an existing concept incompatibly.
- Reconciliation rejects the change.

### Cross-document isolation

- Two unrelated documents use the same phrase with different meanings.
- Their document-scoped concepts are not automatically merged.

### Existing regression suite

- All existing unit, idempotency, accuracy and golden-master tests continue to pass.
- Static ontology mode remains functional.

## Benchmark Corpus

Create a test harness supporting:

- Original document
- Exact copy
- Formatting-only change
- Paraphrase
- Local clause addition
- Local clause removal
- Terminology substitution
- Contradictory clause
- Unrelated document from the same domain
- Document from a different domain

Record:

- stable-concept retention
- unsupported concept rate
- unresolved rate
- provenance validity
- delta precision
- generator calls avoided
- deterministic replay success
- verdict stability where the applicable facts did not change

Do not market these measurements as accuracy until independently labelled expected results exist.

## Implementation Process

Work in small, reviewable phases.

### Phase 1: Discovery only

Before changing code:

1. Inspect the solution structure.
2. Locate topic-map models and loaders.
3. Locate document canonicalisation and projection code.
4. Locate fingerprinting utilities.
5. Locate cache abstractions.
6. Locate rule selection and evaluation boundaries.
7. Locate CLI registration.
8. Locate golden-master and idempotency tests.
9. Locate current prompt contracts.
10. Produce an implementation map naming the exact files and types that should change.

Do not modify code during Phase 1.

### Phase 2: Contracts and pure deterministic core

Implement:

- Versioned data contracts
- JSON schemas or equivalent strict validation
- Canonical serialisation
- Fingerprinting
- Provenance validation
- Snapshot and delta models
- Deterministic reconciler
- Unit tests

Do not integrate an actual LLM during Phase 2.

### Phase 3: Generator boundary and fake generator

Implement:

- `ICandidateOntologyGenerator`
- Request and response models
- Fake deterministic generator
- Cache
- Replay
- Failure diagnostics
- Tests

### Phase 4: CLI and artefact workflow

Implement:

- compile
- inspect
- diff
- validate
- replay
- integration tests

### Phase 5: Runtime selection integration

Implement:

- accepted ontology selector
- compatibility adapter for existing topic maps
- explicit static/dynamic/hybrid modes
- regression tests

Do not remove static topic maps.

### Phase 6: Optional LLM adapter

Only after Phases 1 through 5 pass:

- Use an existing repository AI/provider abstraction if available.
- Require structured schema-constrained output.
- Configure the most deterministic supported settings.
- Cache raw candidate output by the complete generation request fingerprint.
- Record model deployment identity and extraction contract version as provenance.
- Never place the model adapter in the verdict path.

## Deliverables

For each phase, provide:

1. Files inspected.
2. Files added.
3. Files modified.
4. Architectural decision summary.
5. Tests added.
6. Test results.
7. Known limitations.
8. Open risks.
9. Exact commands used.
10. A small, reviewable diff.

Do not state that a phase is complete unless the relevant tests pass.

## Non-Goals for v1

Do not implement:

- A universal enterprise ontology.
- Silent cross-document concept merging.
- Autonomous ontology deletion.
- Embedding-based final identity decisions.
- Online learning during verdict evaluation.
- LLM-based rule evaluation.
- A graph database unless required by demonstrated scale.
- Background ontology mutation.
- Replacement of all existing static topic maps in one change.
- Marketing claims of 100% semantic accuracy.

## Definition of Done

Dynamic ontology v1 is complete only when:

1. A previously unseen test document can produce a valid document-scoped accepted ontology.
2. The accepted ontology is fully grounded in source spans.
3. The LLM or fake generator does not create final identities.
4. Identical document bytes reuse the same ontology snapshot without a generator call.
5. A local document change produces a local deterministic delta.
6. Unchanged concepts retain stable identities.
7. Ambiguous reconciliation fails closed.
8. Accepted artefacts can be replayed without an LLM call.
9. No LLM executes inside deterministic selection or evaluation.
10. Existing static-mode tests continue to pass.
11. New incremental, replay, ambiguity and provenance tests pass.
12. The implementation documents its guarantees and explicit non-guarantees.

## First Instruction

Perform Phase 1 only.

Return:

- Current architecture map
- Exact static-ontology dependency points
- Existing reusable components
- Proposed interfaces and models
- Proposed test locations
- Migration risks
- Recommended first implementation slice

Do not edit files yet.
Do not produce placeholder code.
Do not redesign unrelated components.
