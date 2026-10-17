# Chapter 16 — Architecture Documentation, Decisions and Review
## Intent
A workload's architecture is the sum of its decisions, and decisions that are not recorded are forgotten, re-debated or silently contradicted. Documentation that is owned, versioned and reviewed gives operators, auditors and incident responders a single source of truth. This chapter sets obligations for decision records, diagrams, inventories, threat models, reviews and risk acceptance.
## Requirements
### Architecture decision records
- Every workload MUST maintain an architecture decision record (ADR) log that is started at the beginning of the workload and kept for its entire lifetime.
- The ADR log MUST contain a record for each architecturally significant decision, meaning each decision that affects structure, a key quality attribute, or that is hard to reverse.
- Every ADR MUST follow one common template containing at least the problem context, the options considered, the decision outcome, the trade-offs accepted, and the status.
- Every ADR MUST carry a status of Proposed, Accepted or Superseded, and SHOULD record the confidence level of the decision when confidence is low.
- The ADR log SHALL be append-only, so that an accepted record MUST NOT be edited to change its decision, and a changed decision MUST be recorded as a new ADR that links to the record it supersedes.
- An ADR that spans several phases (short-term, mid-term, long-term) MUST be split into one record per phase.
- Brownfield workloads without an ADR log MUST create one and SHOULD reconstruct records for known past decisions.

### Diagrams and workload documentation
- The workload documentation MUST include a context diagram, a logical or component diagram, and a deployment or network topology diagram, each showing the trust boundaries.
- Each diagram MUST identify its subject, its version or date, and its owner, and every diagram SHALL use a consistent notation with a legend.
- The documentation MUST describe the data flows for each category of sensitive data, including where data is stored, processed and transmitted.
- The documentation MUST list the workload's reliability targets (SLOs, RTO and RPO), and SHALL link each to the design elements that support it.
- The documentation MUST include operational runbooks for deployment, rollback, failover and incident response, and the runbooks SHALL be tested at least annually.

### Repository, ownership and versioning
- All workload documentation MUST be stored in a single repository that is accessible to the workload team, auditors and incident responders, and SHALL be referenced as the source of truth.
- Architecture documents MUST be stored under version control with change history, and each released version SHALL be identified by a version number or tag.
- Every architecture document MUST name an accountable owner and a review date, and a document without a current owner SHALL be treated as out of date.
- The workload team MUST update the affected documents in the same change that modifies the architecture, and the change review SHALL verify that the update occurred.

### Dependency inventory
- The workload MUST maintain an inventory of all internal and external dependencies, including cloud services, third-party APIs, libraries, container base images and data sources.
- Each inventory entry MUST record the dependency's owner or vendor, version, criticality, and the effect on the workload if it is unavailable.
- The dependency inventory MUST be reviewed at least quarterly, and a dependency approaching end of support SHALL have a replacement or upgrade plan recorded.

### Threat modeling
- Every workload MUST have a documented threat model, created during design, that identifies assets, trust boundaries, threats and mitigations, for example by using STRIDE.
- The threat model MUST be reviewed and updated at least annually and whenever the architecture, data classification or an external integration changes.
- Each identified threat MUST map to a mitigation, an owner and a status, and the status SHALL be one of mitigated, accepted or open.

### Review cadence and risk acceptance
- Every production workload MUST undergo a documented architecture review (for example Azure Well-Architected Review) at least annually and before any major redesign.
- Each review MUST record its findings, the assigned owner and due date for every finding, and the results SHALL be tracked until closed.
- Any deviation from a mandatory policy requirement MUST be recorded as an exception that states the requirement, the justification, compensating controls, the approver and an expiry date.
- An exception MUST have a named business-level approver with the authority to accept the risk, and MUST NOT remain active beyond its expiry date unless it is formally renewed.
- Risk acceptance for security trade-offs MUST be documented and signed by accountable stakeholders when a recommended control is not funded or implemented.
- The exception register MUST be reviewed at least quarterly, and expired entries SHALL be closed or renewed.

## Sources
- https://learn.microsoft.com/azure/well-architected/architect-role/architecture-decision-record
- https://learn.microsoft.com/azure/well-architected/security/secure-development-lifecycle
