# Chapter 14 — AI and Machine Learning Workloads
## Intent
AI workloads add risks that conventional applications lack, such as unsafe or ungrounded output, prompt injection, model drift, model retirement and unbounded token spend. This chapter requires that safety, data governance, identity, evaluation and cost controls are designed in and verified rather than assumed from the hosting platform. It applies to both classic machine learning and generative AI workloads.
## Requirements
### Architecture and layering
- The AI workload design MUST define separate layers for client, intelligence (routing, orchestration, agents), inferencing, knowledge (grounding data) and tools, and each layer SHALL enforce its own identity, authorization and caching policy.
- The client layer MUST remain thin and SHALL NOT contain model invocation logic, prompt content, or credentials for model endpoints.
- The design MUST route model calls from clients through a back-end gateway (for example Azure API Management or an equivalent AI gateway) that enforces authentication, quotas, safety filtering, routing and logging.
- The design MUST abstract models and tools behind stable interfaces so that a model or provider can be replaced without changes to application code.
- The architecture document MUST define measurable business success metrics and regulatory constraints for the AI capability before the model and platform are selected.

### Responsible AI and content safety
- Every AI workload that produces user-facing content MUST apply input and output content filtering (for example Azure AI Content Safety or platform-provided filters), and the team SHALL verify the configured filter levels by testing instead of assuming defaults.
- The design MUST include defenses against prompt injection and jailbreak attempts, including prompt shielding or equivalent controls for both user input and retrieved documents.
- The workload MUST set limits on prompt length, output tokens and request rate per identity, so that abuse cannot cause unbounded consumption.
- The team MUST complete and record a responsible AI impact assessment, covering harms, affected groups and mitigations, before any AI feature enters production.
- AI features that influence decisions about people SHALL provide human oversight, and the design MUST document when a human reviews or can override the output.
- The design MUST disclose to users when content is AI-generated and SHOULD provide a feedback channel for reporting harmful or incorrect output.

### Model, prompt and data governance
- The team MUST record the source, version, licence terms and intended use of every model in a model inventory and MUST confirm that the licence permits the intended commercial use.
- The workload MUST keep prompts, system messages and model configuration in version control and SHALL deploy them through a controlled release process, so that telemetry can be correlated with a specific prompt version.
- The design MUST plan for model deprecation by recording the retirement date of each hosted model version and by providing a tested fallback model or migration path before that date.
- Training, fine-tuning and grounding data MUST have a documented owner, classification, lineage and retention period, and personal or confidential data SHALL NOT be used for training unless the legal basis is recorded.
- Retrieval over grounding data MUST enforce the end user's authorization, by passing user or tenant identity to the retrieval layer so results are security-trimmed, and AI components MUST NOT read data stores directly.

### Evaluation and monitoring
- The team MUST evaluate each model and prompt change against a versioned test dataset for groundedness, relevance, safety and accuracy before release, and SHALL block release when results fall below defined thresholds.
- The workload MUST include red-team or adversarial testing of the AI feature before initial production release and after any change of model, grounding source or system prompt.
- The production workload MUST monitor model quality indicators (accuracy drift, inference latency, confidence scores and input data distribution) and SHALL alert when a defined threshold is breached.
- The workload MUST log prompts, completions and tool calls with user identity and prompt version for audit, and the log retention and redaction rules SHALL be documented for any personal data they contain.

### Identity, secrets and network
- Services calling Azure OpenAI, Azure AI services or Azure Machine Learning endpoints MUST authenticate with managed identities or Microsoft Entra ID, and API keys MUST NOT be embedded in code or configuration files.
- Where API keys cannot be avoided, the workload MUST store them in Azure Key Vault and SHALL rotate them at least every 90 days.
- Production AI endpoints and grounding data stores SHOULD be reachable only through private endpoints, and public network access MUST be disabled unless a documented exception exists.
- Agents and tools MUST run with the least-privilege identity needed for their actions, and any tool that changes state SHALL require explicit authorization checks independent of the model's output.

### Cost and capacity
- The workload MUST define token and compute budgets and quotas per environment, per application and per tenant or user where applicable, and SHALL alert when consumption reaches defined percentages of the budget.
- The design SHOULD use caching, model routing to smaller models for simple tasks, and batch processing where latency allows, and MUST document capacity choices (provisioned throughput or pay-as-you-go) with their rationale.

## Sources
- https://learn.microsoft.com/azure/well-architected/ai/application-design
- https://learn.microsoft.com/azure/well-architected/security/secure-development-lifecycle
