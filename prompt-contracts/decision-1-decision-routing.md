# Decision-1 Routing Contract

> **Type:** Evaluation proposal + conditional prompt contract.
> **Status:** Application chat completions use OpenRouter. Decision-1 remains
> proposal-only and is not implemented; do not add it unless the evaluation
> gates below demonstrate measurable value over the current design.
> **Scope:** Authoring and ingestion only. The evaluator remains deterministic
> C# and never calls an AI model.
> **Decision semantics:** [Deploy and use Microsoft-Decision-1 in Microsoft Foundry](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision).
> **Inference provider:** Chat uses OpenRouter's chat-completions endpoint.
> The separate Decision endpoint is `/api/alpha` and remains an unevaluated
> proposal; verify its current availability and response schema before any use.

---

## Intent

Evaluate whether `Microsoft-Decision-1` improves a narrowly defined,
authoring-time classification task. OpenRouter chat completions are the
current text-generation provider; the deterministic runtime remains unchanged.
Adopt Decision-1 only if a controlled comparison shows a material accuracy
improvement that matters to end-to-end results, without unacceptable recall
loss, and if it adds a capability the current implementation does not already
provide.

Decision-1 is not assumed to improve determinism. Its typed output and
probabilities do not make inference byte-deterministic or guarantee 100%
accuracy. LambdaRag's deterministic runtime and cached sidecar replay provide
those properties at their respective boundaries; introducing another
uncached model call cannot strengthen them by itself. If Decision-1 provides
no measurable quality gain over current chat extraction plus validation and
cached replay, do not add it.

This is not a replacement of `IChatClient` with another model name.
OpenRouter exposes separate APIs for chat generation and typed decisions:

- chat endpoint: `https://openrouter.ai/api/v1/chat/completions`
- chat model: `openai/gpt-5.6-luna`
- Decision-1 endpoint: `https://openrouter.ai/api/alpha/decisions`
- Decision-1 model: `microsoft/microsoft-decision-1`
- Decision-1 request shape: `state` plus typed `questions`
- Decision-1 response shape: typed answers with probabilities or scores
- Decision-1 output limitation: it does not return free-form prose or a
  written rationale

The official Microsoft documentation describes the `noul`, `choice`, and
`score` primitives and their intended use for classification, routing,
ranking, grading, and binary decisions. OpenRouter's Decision API syntax is
shown below. Do not substitute the Foundry resource endpoint
`/providers/microsoft/v1/systemone` for OpenRouter's `/api/alpha/decisions`,
or assume the chat-completions endpoint accepts Decision API requests.

---

## OpenRouter API Examples

These examples document the two distinct request surfaces. Keep
`OPENROUTER_API_KEY` in the environment or a secret store; never commit it,
print it, or place it in prompt text.

### Chat generation

Use chat completions for prose and generated artifacts. The model slug is
configurable; the default is `openai/gpt-5.6-luna`.

```python
import os
import requests

openrouter_api_key = os.environ["OPENROUTER_API_KEY"]
response = requests.post(
    url="https://openrouter.ai/api/v1/chat/completions",
    headers={
      "Authorization": f"Bearer {openrouter_api_key}",
        "HTTP-Referer": "<YOUR_SITE_URL>",  # Optional ranking metadata.
        "X-OpenRouter-Title": "<YOUR_SITE_NAME>",  # Optional metadata.
    },
    json={
        "model": "openai/gpt-5.6-luna",
        "reasoning": {"enabled": True},
        "messages": [
            {"role": "user", "content": "What is the meaning of life?"}
        ],
    },
)
response.raise_for_status()
result = response.json()
```

The example prompt is illustrative only. LambdaRag chat callers must send
their own versioned system and user prompts and validate generated output
with their existing schema, quote, DSL, normalization, and cache checks.
All application chat-completion factories use this OpenRouter API with
`reasoning.enabled` set for each request. This provider change does not add or
enable the separate Decision-1 API.

### Text embeddings

Use the OpenRouter embeddings endpoint for in-process text vectors. The
requested output encoding is `float`; the application verifies the returned
dimension before caching the normalized vector.

```python
import os
import requests

response = requests.post(
  "https://openrouter.ai/api/v1/embeddings",
  headers={"Authorization": f"Bearer {os.environ['OPENROUTER_API_KEY']}"},
  json={
    "model": "openai/text-embedding-3-large",
    "input": "Your text string goes here",
    "encoding_format": "float",
  },
)
response.raise_for_status()
embedding = response.json()["data"][0]["embedding"]
```

### Provider and task routing rule

Use OpenRouter chat completions whenever the task is conversational or
text-generative: text goes in and generated text comes out. This includes
multi-turn conversation, summaries, explanations, rule prose, source-grounded
quotes, fact-value extraction, remediations, synopses, and clause rewrites.
Use chat completions when the caller needs natural-language output, even if
that output is constrained to JSON.

Use the OpenRouter Decision-1 API only when the task is a bounded decision
whose output is one of the documented typed primitives (`noul`, `choice`, or
`score`), and only after the evaluation gates in this contract demonstrate
value. Do not route a request to Decision-1 merely because it contains text
or asks the model to "decide" in ordinary language. If the required output
is generated text, keep it on OpenRouter chat completions. If a task needs both
a classification and generated wording, the typed decision may be a separate,
optional step; chat remains responsible for the wording, and the overall
hybrid must independently pass evaluation.

### Decision-1 typed decision

Use the separate OpenRouter decisions endpoint. The model value is the
OpenRouter model identifier, not a Foundry deployment name.

```bash
curl https://openrouter.ai/api/alpha/decisions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENROUTER_API_KEY" \
  -d '{
    "model": "microsoft/microsoft-decision-1",
    "state": "Help! My payouts have been failing for 3 days.",
    "questions": {
      "is_urgent": {
        "type": "noul",
        "instructions": "Does this message convey urgency?",
        "criteria": {
          "true": "Explicitly time-sensitive",
          "false": "No urgency expressed"
        }
      },
      "department": {
        "type": "choice",
        "instructions": "Which team should handle this?",
        "criteria": {
          "billing": "Payments, invoicing, refunds",
          "technical": "Bugs, outages, integrations",
          "sales": "Pricing, upgrades, new accounts"
        }
      },
      "frustration": {
        "type": "score",
        "instructions": "How frustrated is the customer?",
        "criteria": ["Calm", "Frustrated", "Very angry"]
      }
    }
  }'
```

The sample demonstrates the three supported decision primitives, not
LambdaRag's domain prompt. LambdaRag requests must use a closed, explicit
question set with neutral criteria and a documented abstention/fallback
policy. Parse and validate the actual OpenRouter response against its current
Decision API response contract; do not parse it as a chat completion.

---

## Architectural Boundary

```
authoring / ingestion only

source text + bounded candidates
        |
        +--> Decision-1 --> typed decision + probability --> threshold / abstain
        |                                                    |
        |                                                    +--> deterministic filter
        |
        +--> chat model --> prose, JSON artifacts, quotes --> schema + local validators

runtime evaluation

facts / rules / projections --> deterministic evaluator --> verdict
```

If evaluation justifies its use, Decision-1 may narrow candidates or rank
authoring-time work. It must not directly produce or approve a compliance
verdict. `EvaluationService`,
`WorkflowFactory`, compiled predicates, and `SemanticFunctions` remain wholly
deterministic and do not receive a Decision-1 client.

---

## Call-Site Routing

| Current component | Existing operation | Route | Reason |
| --- | --- | --- | --- |
| `FoundryRuleAuthoringAgent` | Generate a rule envelope: prose, lambda, source quote, topic, remediation | **OpenRouter chat** | Generates the required artifact and source quote. |
| `FoundrySectionFactExtractor` | Extract fact values, verbatim duration phrases, and a supporting quote | **OpenRouter chat** | Generates values and quotes; Decision-1 screening is only a future evaluation candidate. |
| `RuleExtractionService` | Generate extracted-rule JSON for Azure AI Search | **OpenRouter chat** | Generates rule fields and requires structured extraction. |
| `LlmTopicSuggester` | Choose an existing topic or invent a new topic with keywords and rationale | **OpenRouter chat** | Generates suggestions; typed selection among existing IDs is only a future evaluation candidate. |
| `ComplianceEditor` | Rewrite a clause as natural language | **OpenRouter chat** | This is sentence generation, not a bounded decision. |
| `SynopsisService` | Write a one-sentence rule synopsis | **OpenRouter chat** | This is sentence generation. |
| `EvaluationService` and `WorkflowFactory` | Apply predicates and determine Pass/Fail | **Neither model** | Runtime verdicts must remain deterministic and replayable. |

The only candidate for a first Decision-1 experiment remains fact-concept
screening in `FoundrySectionFactExtractor`: "Does this section support one of
the schema's already-declared concepts?" It is not active in the application.
Compare against OpenRouter chat extraction, quote validation, and the
sidecar-cache path first. Topic selection is a later candidate only if fact
screening demonstrates value and topic labels have their own evaluation set.

---

## Decision Client Contract

Introduce a provider-specific boundary instead of leaking HTTP or Decision API
types into authoring services.

```csharp
public interface IDecisionClient
{
    Task<DecisionResponse> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DecisionRequest(
    string State,
    IReadOnlyDictionary<string, DecisionQuestion> Questions);

public sealed record DecisionQuestion(
    string Type,
    string Instructions,
    object? Criteria = null);
```

The OpenRouter implementation posts JSON to
`https://openrouter.ai/api/alpha/decisions` with bearer authentication from
`OPENROUTER_API_KEY` and model `microsoft/microsoft-decision-1`. It must record
the requested model identifier and any model/provider metadata returned by the
API, and reject an answer whose type, question name, or selected criterion is
not in the request. Do not use `DefaultAzureCredential` or Foundry's
`systemone` path for this OpenRouter integration.

The client must not implement `IChatClient`: the two response contracts are
incompatible by design.

### Active OpenRouter Configuration

Application chat completions use model `openai/gpt-5.6-luna` by default and
send `reasoning.enabled: true`. In-process text embeddings use
`openai/text-embedding-3-large` with `encoding_format: float`; the current
embedding cache validates 3072 dimensions and normalizes vectors before
persisting them.

```text
OPENROUTER_API_KEY
OPENROUTER_MODEL                     # default: openai/gpt-5.6-luna
OPENROUTER_EMBEDDING_MODEL           # default: openai/text-embedding-3-large
OPENROUTER_BASE_URL                  # default: https://openrouter.ai/api/v1
LAMBDA_RAG_EMBEDDING_DIMENSIONS      # default: model-specific
LAMBDA_RAG_EMBEDDING_CACHE           # default: out/embedding-cache
```

Equivalent application settings are `OpenRouter:ApiKey`, `OpenRouter:Model`,
`OpenRouter:EmbeddingModel`, and `OpenRouter:Endpoint`.

### Conditional Decision-1 Configuration

Only if the experiment passes and implementation is separately approved, add
Decision-1 settings. Do not reuse `LambdaRag:Foundry:Edit:*`:

```text
LambdaRag:OpenRouter:DecisionModel                  # microsoft/microsoft-decision-1
LambdaRag:OpenRouter:DecisionEndpoint               # default: https://openrouter.ai/api/alpha/decisions
LambdaRag:OpenRouter:FactThreshold                  # must be calibrated; no assumed production default
LambdaRag:OpenRouter:TopicThreshold                 # must be calibrated; no assumed production default
```

Environment-variable equivalents:

```text
OPENROUTER_API_KEY
LAMBDA_RAG_OPENROUTER_DECISION_MODEL
LAMBDA_RAG_OPENROUTER_DECISION_ENDPOINT
LAMBDA_RAG_OPENROUTER_FACT_THRESHOLD
LAMBDA_RAG_OPENROUTER_TOPIC_THRESHOLD
```

If Decision-1 is implemented later, it will share the OpenRouter API key with
chat and embedding calls. Its missing configuration must leave current
behavior unchanged and clearly report that the optional experiment is disabled.
There is no requirement to add Decision-1 configuration unless its evaluation
gates pass.

The chat and embedding provider migration is active and independent of any
future Decision-1 experiment.

---

## Prompt Contracts

The following Decision-1 prompts are proposal material only. Current chat and
embedding calls use OpenRouter's chat-completions and embeddings endpoints;
the application does not call the Decision-1 endpoint.

### Fact-concept admission

**Use in:** `FoundrySectionFactExtractor`, before the chat extraction call.

**State:** Canonical JSON containing `section_id`, `section_text`, and the
closed fact-schema concept names and descriptions. The state must not include
rules, desired verdicts, prior model answers, or content from other sections.

**Question type:** One `noul` question per eligible boolean or enum concept.

```json
{
  "state": {
    "section_id": "s_00000004",
    "section_text": "All data at rest is encrypted using AES-256.",
    "concept": {
      "name": "encryption_declared",
      "description": "The section states that data is encrypted at rest or in transit."
    }
  },
  "questions": {
    "encryption_declared": {
      "type": "noul",
      "instructions": "Does this section explicitly support the declared concept? Decide only from the section text.",
      "criteria": {
        "true": "The section explicitly states or unambiguously describes the concept.",
        "false": "The section is silent, discusses a different concept, or only implies it."
      }
    }
  }
}
```

**Acceptance rule:** Admit the concept only when `P(true) >= FactThreshold`.
Otherwise emit `undecided`; do not treat it as false. Chat extraction receives
only admitted concepts and must still provide a byte-for-byte
`supporting_quote`. The existing quote validator remains the final authority.

**Non-boolean facts:** Decision-1 does not extract a verbatim duration,
integer, or text value. It may decide whether a section contains a candidate
value, but the chat model must extract the literal phrase and the deterministic
normalizer must parse it. A failed normalization produces `null`.

### Existing-topic selection

**Use in:** `LlmTopicSuggester`, only when the current map contains two or
more applicable existing topic IDs.

**Question type:** `choice` over a finite, deterministic list of current IDs,
plus `none_of_the_above`.

```json
{
  "state": {
    "heading": "Encryption",
    "body": "...",
    "document_kind": "architecture-review"
  },
  "questions": {
    "existing_topic": {
      "type": "choice",
      "instructions": "Select the one existing topic that best classifies this section. Select none_of_the_above when no listed topic is a clean fit.",
      "criteria": {
        "data_protection": "Encryption, data handling, retention, or access protection.",
        "none_of_the_above": "No listed topic accurately classifies the section."
      }
    }
  }
}
```

**Acceptance rule:** Accept the selected existing topic only when it is not
`none_of_the_above` and its top probability is at least `TopicThreshold`.
Otherwise fall through to the existing chat-based topic suggester. Decision-1
does not invent new IDs, keywords, or rationales.

### Candidate rule triage (future, optional)

**Use in:** a new authoring-time filter after chat generates candidates and
before human review. It is not part of the first implementation slice.

**Question type:** `noul` for each candidate: "Is this candidate grounded in
the quoted source span and limited to a document-testable obligation?"

**Acceptance rule:** A high-confidence positive can prioritize review; it
cannot auto-publish a rule. Existing DSL parsing, source-quote validation, and
human approval remain required.

---

## Determinism and Audit Contract

Decision-1 predictions are model output and are not treated as
byte-deterministic. A probability is a confidence signal, not a determinism
guarantee or calibrated probability unless measured for this workload. The
existing cache already provides deterministic replay for cached artifacts;
Decision-1 adds no determinism benefit on a cache hit. For every experimental
decision, persist:

- normalized request state hash;
- canonicalized questions hash, including criterion order;
- Decision-1 deployment name and returned model identifier;
- answer type, selected value or score, complete probability distribution;
- threshold, routing result (`accepted`, `abstained`, or `rejected`); and
- generated-at timestamp.

Only if adopted, fold a `decisionRoutingHash` into the fact-sidecar
fingerprint alongside the current document, schema, prompt, and model hashes.
A cache hit must replay the recorded decision result and must never call
Decision-1. A changed deployment, threshold, state template, question set, or
criterion ordering invalidates the cached sidecar loudly. This preserves
replay determinism; it does not make fresh Decision-1 inference deterministic.

For topic selection and candidate triage, save an auditable sidecar or review
record. Do not put a probability-only decision into a ruleset as untraceable
metadata.

---

## Calibration and Human Review

Before considering implementation, create a representative, labeled,
held-out evaluation set and compare at minimum:

1. Current chat extraction + existing validation/cache (baseline).
2. Decision-1 screening alone.
3. The proposed Decision-1 + chat extraction + validation hybrid.

Use identical inputs and gold labels. Report precision, recall, F1, abstention
rate, end-to-end rule-level outcomes, repeated-run agreement before caching,
cached replay identity, latency, and cost at several thresholds. Include
confidence intervals or paired significance analysis; do not infer value from
a few examples or an aggregate accuracy number alone. Set thresholds from the
cost of false positives and false negatives, then freeze them before final
held-out evaluation.

**Go/no-go:** Do not implement unless the hybrid materially improves the
pre-agreed end-to-end quality metric over baseline, does not exceed an agreed
recall-regression tolerance, passes the held-out set, and its operational cost
and latency are acceptable. If gains are within measurement noise, if only
classification accuracy improves without improving downstream rule outcomes,
or if current validation/cache already provides equivalent value, the result
is **no-go: retain the current chat and deterministic runtime design**.

- Fact admission: favor precision. A low-confidence concept is `null`, not
  false; the chat extraction and supporting-quote check provide a second gate.
- Topic selection: favor abstention. A wrong existing topic is worse than a
  review-queue item.
- Candidate triage: never use the score as the sole publish decision.

All compliance verdicts and all changes to a topic map or ruleset remain
subject to existing deterministic checks and human approval. This contract is
decision support, not autonomous compliance adjudication.

---

## Conditional Implementation Plan and Exit Gates

Implementation is out of scope until the go/no-go evaluation above passes and
the user explicitly approves implementation. If approved:

1. Add DTOs, an HTTP `OpenRouterDecisionClient`, a factory, and contract tests
  using recorded responses. Do not modify an existing chat factory.
2. Add decision settings and validate endpoint, deployment, auth, response
  type, and option membership at startup.
3. Implement fact-concept admission behind an explicit feature setting;
  preserve the current behavior as the default and rollback path.
4. Add sidecar fingerprinting and replay tests proving no Decision-1 request
  occurs on a cache hit.
5. Keep topic selection and candidate rule triage out of scope until they
  independently pass evaluation and receive approval.

The implementation must not ship until these gates pass:

- request and response contract tests cover `noul`, `choice`, malformed
  answers, timeouts, 401/403/404/422 responses, and retry behavior;
- the decision endpoint and request/response shapes are validated against the
  current OpenRouter Decision API contract, not inferred from the chat API;
- cached fact sidecars replay with zero network calls;
- evaluation and markup golden tests are byte-identical with the feature
  disabled and with cached decisions present; and
- a human review confirms that all new decisions are authoring/ingestion
  support, never a runtime compliance verdict.

---

## Explicit Non-Goals

- Do not configure Decision-1 through `AzureOpenAIClient.GetChatClient`.
- Keep prose and structured-text generation on OpenRouter chat completions,
  including rule authoring, quotations, fact-value extraction, clause
  rewriting, and synopses. Decision-1 is not a replacement for these calls.
- Do not use Decision-1 for embeddings.
- Do not use Decision-1 to write a rule, a quote, a remediation, a synopsis,
  a topic keyword, or a clause rewrite.
- Do not use Decision-1 to return Pass, Fail, or NotApplicable at review
  runtime.