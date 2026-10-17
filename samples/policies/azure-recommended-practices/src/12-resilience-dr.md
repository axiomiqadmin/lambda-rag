# Chapter 12 — Disaster Recovery, Business Continuity and Performance Efficiency

## Intent
Recovery targets, tested failover, and measured performance determine whether a workload meets its commitments when conditions are bad. Untested backups and untested scaling assumptions fail at the worst moment. This chapter sets obligations for recovery objectives, multi-region design, restore and failover testing, load testing, capacity planning, and caching.

## Requirements

### Recovery objectives and planning
- Every production workload SHALL be assigned a business criticality tier, and every tier MUST have documented RTO and RPO targets. Components with different criticality inside one workload MUST each have their own recovery approach documented.
- Every production workload MUST have a written disaster recovery plan covering all components and the system as a whole, with explicit activation criteria that distinguish a disaster from a minor incident.
- The disaster recovery plan MUST name owners for declaring a disaster, executing recovery, communicating with stakeholders, and closing the incident, and it SHALL define escalation paths and prepared communication channels.
- The disaster recovery plan MUST be reviewed and updated at least annually and after every architecture change that affects recovery.

### Multi-region design and data replication
- Each workload's design document MUST state the recovery pattern chosen, such as active-active, active-passive warm standby, or active-passive cold standby, and show that the pattern can meet the documented RTO.
- The secondary region of every multi-region production workload MUST have its network topology, identity configuration, and deployment automation prepared before a disaster, so that recovery does not depend on building them during the event.
- Data replication mode SHALL follow RPO targets, with synchronous replication used where zero data loss is required and asynchronous replication accepted only where the RPO tolerates the lag. Replication lag MUST be monitored and alerted.
- Workloads SHOULD be isolated into components that can be failed over independently, and each partial-failover path MUST be documented and tested separately.
- Failback to the primary region MUST be planned and documented as a separate procedure from failover.
- Traffic redirection between regions SHALL be automated through a global routing service, such as Azure Front Door or Azure Traffic Manager, for workloads whose RTO is shorter than the time a manual DNS change would take.

### Backup and restore
- Every stateful production data store MUST have automated backups, with a retention period aligned to the RPO and to regulatory retention requirements, and at least one copy stored in a different region or fault domain than the primary.
- Backups of production data MUST be protected from deletion and tampering through immutability or soft-delete and separate access controls.
- Restore procedures for every production data store SHALL be tested at least annually and after any change to backup configuration or data schema. Test results MUST include measured restore time compared with the RTO and a completeness check of restored data.
- Restore tests SHALL be performed in an environment separate from production.

### Failover drills and reliability testing
- A full or partial failover drill for every mission-critical and business-critical workload MUST be run at least annually. The drill MUST measure total recovery time across the whole critical flow, including detection time, against the documented RTO and RPO.
- Disaster recovery drills MUST produce a recorded after-action report, and identified gaps SHALL be tracked to closure.
- Fault injection or chaos testing SHOULD be run against production-like environments, and its scope MUST stay within the workload's error budget.

### Load testing and performance targets
- Every production workload MUST define measurable performance targets, such as latency at a stated percentile, throughput, and concurrent users, derived from its SLOs and documented with per-layer performance budgets.
- Every performance test MUST have pass and fail acceptance thresholds for each target metric, so that results are unambiguous.
- Load tests SHALL be executed in the release pipeline for every workload release, and the results MUST be compared against a stored performance baseline to detect regression.
- Load tests for mission-critical workloads MUST run in an environment that matches production in compute SKUs, autoscale settings, caching configuration, and dependencies.
- Production load testing, where performed, MUST be limited to controlled windows with progressive traffic increases, stated safeguards, and a rollback plan.
- Performance results MUST be re-tested at least quarterly against the baseline for every production workload to detect gradual drift.

### Capacity planning
- Capacity planning MUST be performed before any predicted change in usage, such as seasonal peaks, product launches, or marketing events. It SHALL be based on historical utilization data, or on pilots and expert estimates when no history exists.
- The capacity plan for every production workload MUST be reviewed at least annually and MUST include a demand forecast, identified bottleneck components, and the resource quotas and regional capacity that need to be reserved or increased.
- Subscription quotas and regional capacity limits for every service that a critical workload depends on MUST be verified in both primary and secondary regions before launch.

### Caching
- Cache use SHALL be limited to data that is read frequently and changed infrequently. A cache MUST NOT be the authoritative store of critical data, and the system of record MUST remain a persistent data store.
- Every cached item MUST have an explicit expiration policy, and the design MUST describe how stale or inconsistent cached data is detected or invalidated.
- Applications MUST continue to operate, with degraded performance if necessary, when the cache is unavailable. Cache seeding of a large cache SHOULD be staged to avoid overloading the data store at startup.
- Workloads with multiple application instances that need a consistent view of cached data SHOULD use a shared cache, such as Azure Managed Redis, instead of per-instance in-memory caches.

## Sources
- https://learn.microsoft.com/azure/well-architected/reliability/disaster-recovery
- https://learn.microsoft.com/azure/well-architected/reliability/testing-strategy
- https://learn.microsoft.com/azure/well-architected/performance-efficiency/performance-test
- https://learn.microsoft.com/azure/well-architected/performance-efficiency/capacity-planning
- https://learn.microsoft.com/azure/architecture/best-practices/caching
