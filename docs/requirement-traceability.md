# Dynamic Document Ontology v1 Requirement Traceability

**Status:** Implementation and verification are in progress. `reports/traceability-matrix.json` is the current machine-readable traceability source. Its rows group related prose requirements and deliberately retain `partial`/`missing` statuses; it is not a claim of one-to-one 100% semantic coverage.

Current evidence includes the deterministic span and concept reconcilers, parsed-span evidence validation, candidate cache/replay, immutable artifact store/compiler, five ontology CLI commands, opt-in static/dynamic/hybrid projection modes, and the Cloud-service Architecture DOCX fake acceptance path. Latest validation: 110/110 ontology tests, 963/963 unit tests, primary DOCX acceptance passed, 10 benchmark scenarios completed, and all bounded idempotency groups passed except for two documented skips. The quality gate remains failed because it requires no skips and the audit correctly remains in progress. The offline fake does not establish semantic extraction accuracy or production readiness; see the matrix and audit for requirement-level evidence.

## Evidence Policy

An implementation reference is not test evidence. A passing unit test is not acceptance evidence. Provider unavailability is recorded as deferred/blocked, never simulated as provider success. Existing skipped tests remain visible. No requirement is marked `complete` until implementation and named validation evidence exist.