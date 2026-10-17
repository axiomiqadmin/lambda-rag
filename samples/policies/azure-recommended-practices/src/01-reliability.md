# Chapter 1 — Reliability and Resiliency

## Intent
Workloads fail in ways that cannot be fully predicted, so reliability must be designed in from the start rather than added after an outage. This chapter requires that availability and recovery targets are derived from business needs, that redundancy and self-healing are built into critical flows, and that recovery is rehearsed before it is needed. Meeting these obligations keeps outages short, data loss bounded, and incident response predictable.

## Requirements

### Reliability targets
- Every production workload SHALL define an availability SLO, an RTO, and an RPO that are agreed in writing with business stakeholders. Targets set without business agreement cannot be used to justify design cost or to judge whether the design is adequate.
- The availability SLO of a production workload MUST reflect the quality promised to its users for the whole workload, including its dependencies, and MUST NOT be computed solely as the composite of dependency SLAs.
- Every production workload SHALL define at least one SLI for each availability SLO so that compliance with the SLO can be measured continuously.
- Reliability targets for a production workload SHOULD be reviewed at least annually and after every major incident, and the review outcome MUST be recorded.

### Flow criticality and failure analysis
- Every production workload SHALL identify its user flows and system flows and rate each flow on a documented criticality scale. The ratings drive design investment, test priority, and incident prioritization.
- Every critical flow MUST have its own reliability targets, which MAY be stricter than the targets of the workload as a whole.
- Every production workload SHALL maintain a failure mode analysis that lists each component and dependency, its possible failure modes, the expected impact, and the planned mitigation.
- The failure mode analysis for a production workload MUST be updated whenever a new dependency is added or an architecture change alters a critical flow.

### Redundancy and zone resilience
- Every production workload that has a critical flow SHALL deploy the components serving that flow redundantly, with no single instance acting as a single point of failure.
- Production workloads deployed to an Azure region that offers availability zones SHOULD use zone-redundant or zonal-spread deployments for compute, data, and networking components serving critical flows.
- Every Azure service tier chosen for a production workload MUST have a published SLA whose figure is at least equal to the availability SLO that the component must support.
- Every production workload with a regional-outage RTO shorter than the time needed to rebuild it SHALL be deployed to at least two Azure regions.
- Every stateful data store in a production workload SHALL use replication that matches the workload RPO, and the replication mode (synchronous or asynchronous) MUST be documented.

### Scaling
- Every production workload SHALL define a scaling strategy covering the application, data, and infrastructure tiers, based on measured or forecast usage patterns.
- Scaling for production workloads SHOULD be automated through autoscale rules, and manual intervention MUST NOT be required to handle expected load peaks.
- Every autoscale configuration SHALL define both scale-out and scale-in rules together with explicit minimum and maximum instance counts.

### Self-preservation and self-healing
- Every network call from a production workload to a remote dependency SHALL use a timeout, and calls to transient-fault-prone dependencies MUST use retries with exponential back-off and jitter. Unbounded waits and immediate retries amplify outages.
- Every production workload that depends on a remote service SHOULD apply a circuit breaker so that repeated failures stop further calls until the dependency recovers.
- Every critical flow in a production workload SHOULD define graceful degradation behavior that preserves core function when a non-essential dependency is unavailable.
- Every load balancer or traffic router fronting a production workload SHALL use health probes that exercise the dependencies needed by the application, and unhealthy instances MUST be removed from rotation automatically.
- Every message-processing component in a production workload SHALL handle duplicate delivery safely, so that retried operations are idempotent.

### Reliability testing
- Every production workload SHALL undergo fault-injection or chaos testing against its critical flows at least annually, and the results MUST be documented with follow-up actions.
- Every production workload SHALL be load tested before launch and after any change that materially alters capacity, to confirm that scaling behaves as designed.

### Backup and disaster recovery
- Every production workload SHALL maintain a documented disaster recovery plan covering every component and the system as a whole, aligned to the workload RTO and RPO.
- The disaster recovery plan of a production workload MUST define the activation criteria that distinguish a disaster from a minor or major incident, and it MUST name the role authorized to declare a disaster.
- The disaster recovery plan of a production workload SHALL be exercised by a DR drill, including failover and failback, at least annually.
- Every production data store SHALL have automated backups with a retention period defined by business requirements, and at least one backup copy MUST be stored in a location isolated from the primary failure domain.
- Backup restores for every production data store SHALL be tested at least twice a year, and the measured restore time MUST be compared with the workload RTO.

### Health monitoring
- Every production workload SHALL maintain a health model that maps component health signals to the health of each critical flow.
- Every production workload SHALL collect availability and reliability telemetry from all critical-flow components and MUST retain it long enough to support post-incident analysis.

## Sources
- https://learn.microsoft.com/azure/well-architected/reliability/checklist
- https://learn.microsoft.com/azure/well-architected/reliability/metrics
- https://learn.microsoft.com/azure/well-architected/reliability/disaster-recovery
