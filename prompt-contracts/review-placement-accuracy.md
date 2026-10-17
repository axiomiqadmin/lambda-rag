# Review placement & classification accuracy (evidence-proof verdicts)

**Intent.** Make every comment and verdict in `reviewed.docx` land on the
text that actually proves the rule, and make Passed / Not applicable / Gap
mean exactly what a human reviewer means by them. Triggered by human review
of `samples/architecture/Cloud-service-Architecture.docx` against
`rulesets/architecture-review/architecture-v2.json`
(`--applicability-floor 0.2 --rule-level-stats --rewrite --annotate-pass`).
Placement/classification must be 100% explainable, deterministic and
idempotent (Four Pillars: Determinism, Idempotency, Accuracy, Flexibility).

**Already shipped (do not redo; keep green).**
- `AbsenceEvidence.FindExclusion`: Not applicable needs a directional negation
  ("no X" / "without X" look right; "X is not used" / "X is out of scope" look
  left, 6-word window) next to the rule's own subject terms; multi-word
  `topicSlug` terms all required; single-word slugs (e.g. `DATA`) ignored.
- Soft-cohesion demotion of a thin Pass yields `Gap`, never `NotApplicable`.
- With `--applicability-floor`: sections with `category: definitions` never
  host a verdict (unless the rule is about definitions), and a section must
  mention a qualifier of the rule's subject phrase (text before the modal verb).
- Result on the sample: EASTOR-001 no longer attaches to 1.3.

**Classification rules (normative).**
- **Passed**: one sentence (or one table row) in the document explicitly
  satisfies EVERY material part of the rule (subject, modal/qualifier such as
  "only", "external", "personal data", negation). Related or partial text is
  not a Pass.
- **Not applicable**: explicit document evidence that the triggering
  technology, workload, data class or condition is absent (or a rule that is
  an out-of-document process). Silence, floor-filtering or unrelated
  architecture statements never qualify.
- **Gap**: rule is relevant but the document is silent, incomplete, ambiguous
  or only loosely related.
- One evidence sentence must not be reused across rules unless it
  independently proves each rule.

## Work items

### WI-1 Evidence-proof Pass (anchor on the proving sentence)
- Add `EvidenceSpan` (char start/length within the document) and
  `EvidenceQuote` to the Pass `Verdict`. Compute it deterministically as the
  first sentence/table-row (document order) in the section for which the rule's
  lambda is satisfied by that sentence alone. If no single sentence satisfies
  the lambda, the verdict is `Gap` (`finding: partial_evidence`), not Pass.
- `Annotation.BuildPassAnnotations` anchors the comment on `EvidenceSpan`
  (fallback: section heading span, flagged `AnchorKind=section`) and
  `CommentFormatting.BuildPassBody` quotes the evidence sentence.
- Sentence splitting reuses `AbsenceEvidence`'s splitter (`(?<=[.!?])\s+|[\r\n]+`).

### WI-2 One best Pass per rule
- A rule with several passing sections emits ONE primary Pass comment (best
  section). Score = (distinct rule-subject terms present) then (lambda literals
  satisfied) then earliest `charStart`; ordinal tie-breaks only.
- Additional passing sections are retained in `report.json`
  (`supportingEvidence[]`) but get no comment unless `--annotate-pass-all`.
- Targets: EAPRIV-001 (4 comments today -> 1), EAIAC-008 (7.4 -> the section that
  states the tooling).

### WI-3 Material-part checks
- Extract required qualifiers from the rule's subject/predicate phrase
  ("only", "external", "any/every environment", "personal data", "production")
  and require them co-located in the proving sentence/row.
- Authoring side (`Authoring`/normalizer): emit them as `requiredQualifiers`
  on the rule; runtime only checks, never infers with an LLM.
- Known false passes to fix: EACICD-015 at 8.1 and 8.4 (no "only"),
  EADATA-002 at 3.3.3 (internal traffic only; rule is external networks).

### WI-4 Authoring quality gate
- Flag non-normative rules (source text lacks SHALL / MUST / MUST NOT /
  SHOULD) at authoring time (`kind: catalog`); runtime skips them from Pass
  annotation and from the gap list. Known: EAARCH-003, EAIAC-008.
- Lint lambdas for negative requirements ("MUST NOT use :latest") that only
  test positive proxies (EACNTR-010 tests "pinned/digest", not ":latest").
  Fail ruleset validation, never rewrite silently.

### WI-5 Not-applicable strictness
- `ResolveSilence` / floor-filtered path returns `Gap` unless
  `AbsenceEvidence` finds an explicit exclusion or the rule is an
  out-of-document process. Fixes EATRACE-011.
- NA appendix cites the exact excluding sentence and its section.

### WI-6 Gap presentation
- Replace the single 70 KB document-level Gap comment with a Gap appendix
  grouped by rule-id prefix/category, ordered `OrdinalCompare` on
  (category, ruleId). Cap the top-of-document comment to counts + link-style
  pointers. Cloud/tech-specific alternatives inside a cloud-neutral rule stay
  Gap (not NA) unless the document states exclusion.
- Review EANET-018 (Azure Bastion in 7.2.1 Hub VNet) for a missed Pass.

### WI-7 Rewrite determinism
- `--rewrite` (LLM-backed `ComplianceEditor`, `out/rewrite-cache`) in
  deterministic/CI mode must use a hash-keyed pre-populated cache or
  `DeterministicMockClauseRewriter`, and fail loudly on a cache miss. The
  cache key includes rule id+version, clause text hash, model id, prompt hash.
- No LLM call may influence a verdict, anchor or classification.

## Inputs / Outputs
- Inputs: `ProjectedDocument` sections (`text`, `category`, paragraphs/rows
  with `char_start`), `Rule` (`naturalLanguage`, `evidenceQuote`, `lambda`,
  `metadata.topicSlug`, new `requiredQualifiers`, `kind`).
- Outputs: `report.json` verdicts with `evidenceSpan`, `evidenceQuote`,
  `supportingEvidence[]`; `reviewed.docx` comments anchored on the evidence
  sentence; Gap appendix.

## Edge cases
- Table evidence: the proving unit is a row; anchor to the row's first cell
  paragraph range; header cells never prove a rule.
- Sentence spanning two paragraphs: not provable -> Gap.
- Rule with no modal verb: subject check skipped (current behavior), qualifier
  check skipped, Pass still needs a single satisfying sentence.
- Section category `definitions` / `references`: never hosts a Pass/NA unless
  the rule is about definitions/references (EAARCH-001 in 1.4 stays suppressed).
- Duplicate headings: anchors resolve by char offsets, never by heading text.
- Mojibake/encoding: offsets are computed on the normalized projected text;
  quoted evidence must round-trip byte-identically.
- Floor `0.0` (default): behavior and goldens unchanged.

## Determinism / idempotency
- No wall-clock, random IDs, or unordered iteration (use ordinal-sorted
  collections); comment/annotation ids derive from `ContentHash.Compose`.
- Same inputs -> byte-identical `report.json` and OOXML parts
  (`document.xml`, `comments.xml`, `styles.xml`), including re-running into an
  existing output folder (verified on the sample before this contract).
- Cached/signed artifacts: fingerprint drift fails loudly.
- Corpus goldens change only where a classification intentionally changes;
  list every changed golden in `CHANGELOG.md`.

## Acceptance
- On the sample document with the command above:
  - EASTOR-001: Pass at 9.2 only, comment anchored on the Cosmos DB managed
    identity sentence; never on 1.3, 3.3.1 or 4.3.
  - EACICD-015, EADATA-002, EAIAC-008 (7.4): no longer Pass. EAARCH-003 may
    stay Pass (its ADR sentence proves the rule).
  - EAPRIV-001: exactly one primary Pass comment (others in `supportingEvidence`).
  - EATRACE-011: Gap, not Not applicable.
  - Gap list grouped by category (single document-level comment).

### Implementation status
- Done: WI-1, WI-2, WI-3, WI-4 (non-normative Skipped), WI-5, WI-6 (grouping).
- Open: dedicated Gap appendix, EANET-018 (lexical floor drops it; not tuned),
  negative-requirement lambda lint (EACNTR-010), WI-7 rewrite cache-miss CI mode,
  idempotency golden for `--annotate-pass` output.
- Unit tests (`tests/LambdaRag.UnitTests`): one per edge case; regression tests
  for each rule id above using minimal synthetic sections (never tuned to the
  sample document text).
- Idempotency tests (`tests/LambdaRag.IdempotencyTests`): add a golden for
  `--annotate-pass --applicability-floor 0.2` output and run it twice, asserting
  identical OOXML parts; `RuntimeDeterminismGuardrails` stays green.
- `dotnet build` (warnings as errors), `dotnet test`, and
  `scripts/quality_gate.ps1` pass with 0 failed/skipped tests.
- Accuracy deltas recorded in `bench-results/cross-industry-ledger.csv`.

### Implementation status (update)
- Done: Partial outcome + reason codes; `review-findings.csv`; short Gap pointer;
  `NON_NORMATIVE` skip + author warning; engine support for per-rule
  `requiredConcepts`, `appliesWhen` and ruleset `scopeFacts` (unit-tested).
- Open (needs the authoring pipeline + re-signing, not engine code): populate
  `requiredConcepts` / `appliesWhen` / `scopeFacts` in the signed rulesets; Word
  appendix table for findings.

## Out of scope
- Tuning rules or lambdas to this specific document (Flexibility pillar).
- Any LLM in the runtime decision loop.
- Changing the signed `RuleSet.json` fingerprint scheme.
