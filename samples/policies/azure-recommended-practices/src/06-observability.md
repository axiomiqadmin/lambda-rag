# Chapter 6 — Monitoring, Logging, Tracing and SRE

## Intent
Teams can only operate what they can observe. This chapter sets obligations for telemetry design, log handling, alerting, service-level objectives, distributed tracing, and incident response so that degradation is detected early and diagnosed quickly. It treats the monitoring stack as a first-class part of the workload rather than an afterthought.

## Requirements

### Health model and telemetry design
- Every workload SHALL define a health model that lists its health indicators, key performance indicators, and the metrics that feed them. Telemetry collection MUST be derived from that model.
- The health model MUST map user flows and system flows to the resources that support them, so that degradation in a dependency is visible at the level where business impact occurs.
- Workload components MUST emit structured telemetry that uses a consistent schema and includes the source component, a timestamp, and a correlation identifier.
- Applications SHOULD use a configurable logging framework that is decoupled from any specific monitoring backend, so the backend can change without code changes.

### Logging practices
- Logs MUST NOT contain secrets, credentials, or unmasked personal or sensitive data. Applications SHALL scrub such values before writing log entries.
- Telemetry MUST be classified by operational concern, such as audit, security, debugging, and performance, so that access controls and retention can be applied per category.
- Workload business data SHALL NOT be stored in telemetry stores. Telemetry and workload data MUST be kept separate.
- Production logging verbosity SHOULD be lower than development verbosity and MUST be adjustable at runtime or by configuration without redeployment.
- Telemetry emission MUST be non-blocking and MUST NOT cause the application to fail when the monitoring backend is unavailable. The only exception is critical audit logging that is required by regulation.

### Azure Monitor and Application Insights
- Every production Azure resource MUST send platform logs and metrics to a Log Analytics workspace through diagnostic settings. Diagnostic settings SHOULD be deployed by Azure Policy so that new resources are covered automatically.
- Every production application MUST be instrumented with Application Insights, or an OpenTelemetry-based equivalent that exports to Azure Monitor, so that requests, dependencies, exceptions, and performance counters are captured.
- Workspace-based Application Insights resources SHOULD be used so that application and infrastructure telemetry can be queried together.
- Access to log workspaces MUST be controlled by role-based access control, and workspaces holding security or audit data SHALL be restricted to authorized personnel.

### Retention
- The retention period for each log category MUST be defined in the design document and SHALL satisfy the longest applicable regulatory or organizational requirement.
- Audit and security logs SHOULD be kept for at least one year, using archive or long-term storage tiers for the period beyond interactive retention to control cost.
- Log data MUST be protected from alteration and deletion for the duration of its retention period.

### Distributed tracing and sampling
- Every request that crosses a service boundary MUST propagate a correlation identifier, using the W3C Trace Context standard, so that a transaction can be traced end to end.
- Services in a distributed system SHALL emit trace spans for all inbound requests and outbound dependency calls.
- Sampling, when used for traces or telemetry in production, MUST preserve all failed requests and all exceptions. The sampling rate SHALL be documented, and sampling decisions MUST be consistent across the services in a single transaction.

### Alerting
- Every alert MUST have a named owning team, a severity, and a linked runbook or response procedure. Alerts without an owner SHALL NOT be enabled in production.
- Alerts MUST be based on symptoms that affect users or on breach of a service-level objective, and SHOULD NOT fire on causes that require no human action.
- Production alert rules and action groups MUST be defined as code and stored in version control. Alerting MUST include at least one notification path that is independent of the monitored workload.
- Alert rules SHALL be reviewed at least quarterly, and rules that are noisy or never actionable MUST be tuned or removed.

### SLIs, SLOs and error budgets
- Every production service MUST define at least one service-level indicator for availability and one for latency, each with a documented measurement method.
- Every production service MUST have a service-level objective for each indicator, agreed with the service's business owner, with a stated measurement window.
- Teams SHOULD track an error budget derived from each objective and MUST define the actions taken, such as pausing feature releases, when the budget is exhausted.

### Incident response
- Every production workload MUST have a documented incident response process that defines severity levels, on-call responsibilities, escalation paths, and communication procedures.
- On-call engineers MUST have access to dashboards, logs, and traces sufficient to diagnose an incident without requesting additional access during the incident.
- A blameless post-incident review MUST be completed within 10 business days after every incident at the highest two severity levels, and SHALL record corrective actions with owners and due dates.

## Sources
- https://learn.microsoft.com/azure/well-architected/operational-excellence/observability
- https://learn.microsoft.com/azure/well-architected/operational-excellence/safe-deployments
