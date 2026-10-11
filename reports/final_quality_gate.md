# Quality Gate

Status: **failed**
Last batch: finalize

Known skipped tests fail the requested no-skips gate; see each test output and contract-compliance limitations.

| Check | Status | Summary |
|---|---|---|
| Core build | passed | Build succeeded. |
| CLI build | passed | Build succeeded. |
| API build | passed | Build succeeded. |
| C# formatting | passed |  |
| Full unit tests | passed | Passed!  - Failed:     0, Passed:   963, Skipped:     0, Total:   963, Duration: 5 s - LambdaRag.UnitTests.dll (net9.0) |
| Ontology contract tests | passed | Passed!  - Failed:     0, Passed:   110, Skipped:     0, Total:   110, Duration: 392 ms - LambdaRag.UnitTests.dll (net9.0) |
| DOCX acceptance test | passed | Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 398 ms - LambdaRag.UnitTests.dll (net9.0) |
| Ontology benchmark | passed | Scenarios: 10 |
| Contract audit | in-progress | Audit status: in-progress |
| Idempotency accuracy | passed | Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 6 s - LambdaRag.IdempotencyTests.dll (net9.0) |
| Idempotency corpus | passed | Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 6 s - LambdaRag.IdempotencyTests.dll (net9.0) |
| Idempotency runtime | skipped | Passed!  - Failed:     0, Passed:    25, Skipped:     1, Total:    26, Duration: 10 s - LambdaRag.IdempotencyTests.dll (net9.0) |
| Idempotency ARB benchmark | passed | Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 7 ms - LambdaRag.IdempotencyTests.dll (net9.0) |
| Idempotency non-DOCX miscellaneous | passed | Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 3 m 30 s - LambdaRag.IdempotencyTests.dll (net9.0) |
| Idempotency reviewed DOCX | passed | Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 2 s - LambdaRag.IdempotencyTests.dll (net9.0) |
| Idempotency review pipeline | passed | Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 1 s - LambdaRag.IdempotencyTests.dll (net9.0) |
| Known skipped tests | skipped | Skipped! - Failed:     0, Passed:     0, Skipped:     2, Total:     2, Duration: 2 ms - LambdaRag.IdempotencyTests.dll (net9.0) |
