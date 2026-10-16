# Prompt Contract: Review Finding and Redline Quality Bug Fix

> **Type:** Implementation prompt contract.
> **Status:** Ready for implementation.
> **Scope:** Improve how review findings become DOCX comments and tracked changes.
> **Primary evidence:** `out/sample-scoped/report.json` and the generated
> `rulesets/architecture-review/architecture-v2.json` from the 2026-10-16
> architecture review run.

## Goal

Make review comments specific, non-duplicative, correctly classified, and
grounded in the reviewed document. Make proposed edits minimal and bounded to
the clause they address. When applicability, evidence, or edit scope is
uncertain, preserve the finding for human review and do not emit a tracked
change that could alter unrelated text.

Preserve the existing deterministic evaluation contract: no LLM may decide a
runtime Pass/Fail result. LLM use for rule authoring or rewrite suggestions
must remain schema-validated, bounded, and auditable.

## Reproduction Evidence

- The report contains 1,432 verdicts, including 1,279 failures.
- The generated ruleset contains 436 rules, all with severity `Violation`,
  rendered as MAJOR comments.
- One rule (`EANET-008`) fails against 27 projected sections.
- One clause span is shared by 86 failures from different rules.
- A representative finding has a 22-character `SourceSpan` and a 1,639-
  character `ClauseSpan`. Its comment marker can therefore land at a section
  heading while its rewrite input covers a much larger body.
- The DOCX tracked-change inspection for this run expired before completion.
  Its individual edits are unverified and must not be accepted or rejected on
  the basis of the report alone.

Relevant implementation surfaces include:

- `src/LambdaRag.Authoring/FoundryRuleAuthoringAgent.cs` — generated rule
  schema and default rule fields, including hard-coded severity.
- `src/LambdaRag.Evaluation/Engine/EvaluationService.cs` — candidate
  applicability, verdict fan-out, and evidence/clause span refinement.
- `src/LambdaRag.Markup/Annotation.cs` — one-comment-per-verdict conversion.
- `src/LambdaRag.Cli/Program.cs` — shared-span rewrite suppression and
  annotation orchestration.
- `src/LambdaRag.Authoring/Editing/ComplianceEditor.cs` — rewrite prompt,
  response normalization, and rewrite cache.
- `src/LambdaRag.Markup/OpenXmlMarkupService.cs` — DOCX comment and tracked-
  change application.

## Required Behavior

### 1. Rule applicability must be defensible

- An extracted rule must not be treated as applicable to every projected
  section solely because its selector matches all sections or its predicate is
  `true`.
- Authoring must preserve a machine-usable distinction between the rule's
  obligation, its scope/applicability conditions, and the evidence required to
  determine compliance.
- Validate rules against relevant and irrelevant section examples before
  publishing them. Irrelevant sections must not produce Fail findings.
- Distinguish an explicit contradiction from a missing requirement. Emit a gap
  only when the rule is established as in scope; do not infer scope from a weak
  lexical overlap alone.
- Keep the evaluator deterministic. Similarity or LLM output may inform
  authoring or candidate selection, but must not directly adjudicate runtime
  compliance.

### 2. Severity must not default to MAJOR

- Remove blanket `RuleSeverity.Violation` assignment for generated rules.
- Severity must be present in the validated authoring result or assigned by a
  documented deterministic policy using the source obligation and impact.
- Do not equate mandatory applicability with severity. `MUST`/`SHALL` may
  establish obligation strength but does not, by itself, establish Critical or
  Major impact.
- If severity cannot be supported, fail validation or use an explicitly
  configured non-MAJOR review-needed default. Record the basis in rule
  metadata for audit.

### 3. Comments must be anchored and aggregated correctly

- Treat the comment evidence span and the editable clause span as separate
  concepts. Neither may silently expand to an entire section when a precise
  target cannot be resolved.
- For an explicit contradiction, anchor to the actual contradictory text.
- For a missing requirement, use a clearly identified scoped section/gap
  anchor; do not present a nearby heading or unrelated phrase as contradictory
  evidence.
- Coalesce findings only when they refer to the same normalized obligation
  and the same target location. A grouped comment must retain every relevant
  rule reference and remediation without conflating distinct controls.
- Preserve separate comments for independent violations at genuinely
  different locations, including separate occurrences of the same rule.
- Include a stable, auditable reason whenever a finding is grouped, left
  unanchored, or withheld from markup.

### 4. Tracked changes must be minimal and fail closed

- Do not request or emit a rewrite when the target clause is ambiguous, the
  span is shared by incompatible findings, or the proposed edit would change
  unrelated content. Keep the comment and report the skip reason instead.
- Revise the editor contract so it may explicitly decline (`NO_REWRITE`) when
  no safe, necessary rewrite can be produced. Remove any instruction that
  forces a rewrite regardless of confidence.
- Rewrites must preserve unaffected text, list/paragraph structure, defined
  terms, and factual meaning. Change only wording needed to address the cited
  requirement; do not invent obligations, implementation details, or facts.
- Validate the proposed replacement against the targeted original span before
  emitting OOXML tracked changes. Invalid, over-broad, or structurally unsafe
  replacements remain comments only.
- Markup application failures must be observable in the command result; do not
  silently report a clean output when annotations or tracked changes were
  skipped because of application errors.

### 5. Preserve auditability and deterministic behavior

- Keep each finding traceable to its ruleset ID/version, rule ID, evidence,
  source offsets, applicability decision, severity basis, and rewrite status.
- Stable sorting, IDs, report serialization, and repeated-run determinism must
  be preserved for identical inputs and configuration.
- Any new rule-authoring fields must be schema-validated, fingerprinted, and
  covered by compatibility tests.

## Implementation Sequence

1. Add failing tests using small fixtures for rule fan-out, unrelated
   sections, missing-vs-contradictory requirements, shared anchors, and
   severity classification.
2. Correct authoring output and validation so rules carry defensible scope,
   obligation, and severity data. Reject or quarantine rules that cannot meet
   the authoring contract.
3. Correct evaluation and span refinement so candidate applicability and
   verdict type are not confused, and ambiguous absence findings cannot
   produce broad edit spans.
4. Add finding aggregation at annotation construction, keyed by normalized
   obligation plus target location; retain distinct findings and their
   citations.
5. Tighten rewrite prompting and output validation. Keep unsafe or conflicting
   cases comment-only with an explicit reason.
6. Add DOCX-level verification and run the architecture reproduction plus the
   contract-review fixtures. Inspect the generated DOCX after reopening it in
   a document-capable environment before accepting any individual edit.

Keep changes localized to the owning authoring, evaluation, markup, and CLI
paths. Do not solve the symptoms by simply raising the applicability floor,
globally deduplicating by rule ID, changing the MAJOR banner, or suppressing
all comments on a section.

## Acceptance Criteria

### Rule and finding quality

- The reproduced ruleset is no longer uniformly MAJOR unless each individual
  rule has validated evidence supporting that severity.
- A rule evaluated against irrelevant sections produces no Fail findings.
- An in-scope explicit contradiction produces a failure with a quote/span
  that overlaps the contradictory document text.
- An in-scope omission is represented as a gap, not as a fabricated
  contradictory quote.
- Same-obligation findings at the same location produce one grouped comment
  with all rule references; distinct obligations remain identifiable, and
  distinct violation locations remain separately anchored.
- No comment is anchored to a section heading solely because a missing
  predicate literal could not be found in the body.

### Rewrite safety

- No tracked change is emitted for a missing/ambiguous target span, an
  incompatible shared span, a declined rewrite, or an invalid replacement.
- A valid rewrite changes only the intended clause and preserves surrounding
  text and paragraph/list structure.
- Reopened DOCX inspection confirms comment ranges and every `w:ins`/`w:del`
  target the intended text. All skips and application failures appear in the
  review summary.
- The expired inspection from the reproduced run is rerun in a live document
  environment; its prior output is treated as unverified until then.

### Regression gates

- Add unit tests for the cases above, including multiple independent rules
  sharing an anchor and one rule legitimately failing at multiple locations.
- Add or extend DOCX integration tests to reopen output and assert comment and
  tracked-change ranges, not only output-file existence.
- Existing unit, golden, and idempotency tests pass; identical inputs produce
  byte-identical reports and markup where the rewrite model/cache response is
  held constant.
- The original reproduction report shows fewer duplicate comments and no
  broad section rewrite caused solely by anchor fallback. Any remaining
  findings are individually traceable and appropriately classified.

## Out of Scope

- Replacing deterministic runtime evaluation with an LLM judge.
- Silently accepting or rejecting tracked changes.
- Global deduplication that hides separate violations or distinct controls.
- Rewriting unrelated projector, ruleset, or UI behavior without a failing
  fixture tied to this bug.