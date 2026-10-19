# Reviewer QC rounds 1-3 and next steps (Azure recommended-practices review)

**Intent.** Record what the human-QC tuning rounds changed, and hand over the
remaining to-do items as an executable contract. Subject:
`samples/architecture/Cloud-service-Architecture.docx` reviewed against
`rulesets/architecture-review/azure-recommended-v1.json`
(`--applicability-floor 0.2 --rule-level-stats --rewrite --annotate-pass`).
Four Pillars apply: Determinism, Idempotency, Accuracy, Flexibility. Fixes stay
generic; nothing may be tuned to this document.

## Done (do not redo; keep green)

1. **Policy + ruleset.** `samples/policies/azure-recommended-practices/`
   (sources, PDF, `scripts/build_policy_pdf.py`) -> `extract-rules` ->
   `azure-recommended-v1.json` (generated, never hand-edited). Authoring emits
   `requiredConcepts`, `appliesWhen` (allowlist: platform, cloud provider,
   product, tenancy) and builds `ScopeFacts` via `ScopeFactCatalog`.
2. **Silence is a Gap, not N/A** for Conditional/Optional rules in strict mode.
3. **Strict placement** (`EvaluationService`): one proving sentence + qualifiers;
   glossary/References/Purpose/Scope/Introduction/Architectural Representation
   sections are never evidence; Partial with reason codes when proof is
   incomplete.
4. **Concept matching** (`CompiledConcepts`): stemmed whole-token matching
   (`sca` != `scan`), descriptor words dropped when >= 2 concepts remain, a
   Pass needs a multi-word phrase hit, enumeration groups ("GET, PUT and
   DELETE") need every item.
5. **Composed evidence** (`COMPOSED_EVIDENCE`): up to two related sentences may
   jointly cover every concept group and qualifier; must share a distinctive
   word; anchor sentence carries at least half the concepts.
6. **Scope facts**: a vendor-wide product statement ("deployed exclusively on
   Azure") never excludes a rule about one of that vendor's products.
7. **Extraction tolerance**: glued modals (`MUSTNOT`, `SHALLensure`) count as
   normative.
8. **Authoring prompt**: <= 3 concept groups, no descriptor/rationale groups,
   short stems, mechanism synonyms.
9. Latest sample result: 14 Pass / 8 Partial / 2341 Gap / 3 N/A; byte-identical
   across runs. Unit 1076 and idempotency 75 (+2 known skips) green.

## To do (in priority order)

### P0 - Pass precision (do before any recall work)
- ADR rule passes without evidence of context, decision, alternatives and
  consequences; generic least-privilege wording passes without every required
  assignment scope. Add qualifier/part completeness: a rule that lists parts
  must have every part evidenced, else Partial.
- Contradiction handling: when the document gives conflicting values for the
  same control (3 replicas in 3.5 vs 2 in 6.3) the result is Partial with a
  contradiction reason code, never Pass.
- Regression tests for every false Pass the human reports.

### P1 - Recall via authoring-time evidence patterns (needs owner approval first)
- Per rule, the authoring step emits structured proof conditions: subject,
  control, polarity, scope, quantifier, numeric threshold, trigger. Accepted
  phrasings must preserve them ("canary" proves progressive exposure, not
  approval or health gating).
- Patterns carry negative examples, are human-reviewed, versioned and signed
  with the ruleset (pinned normalizer and matcher versions). Fingerprint drift
  fails loudly.
- Runtime stays LLM-free; findings list which proof conditions were satisfied
  and the exact evidence location.
- Known misses to cover: no public IP, progressive exposure (canary),
  `:latest` prohibition, restore drills (monthly satisfies annual), API
  versioning, annual penetration testing, database-scoped managed identity.
- Tests cover missed Passes AND false Passes, applicability and extraction.

### P2 - Applicability and extraction
- Audit remaining N/A rows (AKS only today). Keep a hosting-platform
  exclusion only when the evidence sentence names a competing platform.
- Fix joined-word extraction at the source (PDF build) so raw text keeps
  spaces; the engine tolerance stays as a backstop.

### P3 - Policy values (owner decision, not engineering)
- Confirm or replace: patching (document says Critical 24 h, High 7 days; the
  policy draft says 14 days), log retention (12 months security, 7 years
  audit), restore tests (annual vs twice yearly vs documented monthly),
  exception expiry (12 months is a proposed value, unverified).

### Housekeeping
- `bench-results/cross-industry-ledger.csv` has a pre-existing local change;
  leave it out of commits unless the owner says otherwise.
- Ruleset regeneration is slow (about 40 minutes, LLM, throttled) and
  renumbers rule IDs; humans must match findings by requirement text.

## Verification pattern
`dotnet build`; `dotnet test tests/LambdaRag.UnitTests`;
`dotnet test tests/LambdaRag.IdempotencyTests`;
`dotnet format whitespace LambdaRag.sln --verify-no-changes --no-restore --include <paths>`;
run the review twice into separate `--out` folders and compare SHA256 of
`report.json` and `review-findings.csv`; update `CHANGELOG.md`.
