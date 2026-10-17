# Chapter 15 — Sustainability and Efficient Resource Use
## Intent
Every design decision affects how much compute, storage and network a workload consumes, and idle or oversized resources waste energy and money alike. Sustainability depends on removing waste, measuring emissions and assigning ownership, without undermining reliability, security or performance. This chapter turns those practices into obligations for design and operations.
## Requirements
### Governance and measurement
- Every production workload MUST have a named owner accountable for resource efficiency and sustainability targets, and the owner SHALL be recorded in the workload documentation.
- The workload team MUST define at least one measurable efficiency or emissions target (for example average CPU utilization, carbon per transaction or monthly emissions) and SHALL review it at least quarterly.
- The team SHOULD measure workload emissions by using Azure carbon optimization or an equivalent tool, and MUST record the baseline and each reported change.
- The architecture document MUST record the sustainability trade-offs of reliability, performance and security decisions, such as multiregion duplication, extra caching or long telemetry retention.

### Right-sizing and utilization
- The team MUST right-size every production compute resource against measured utilization at least every six months, and SHALL document the observed utilization used as evidence.
- The design MUST define a target average utilization for each compute tier and SHALL justify any tier running persistently below that target.
- Stateless and variable-load components MUST use autoscaling (for example virtual machine scale sets, Azure Kubernetes Service autoscalers or App Service autoscale) with a defined minimum and maximum instance count, instead of being sized for peak demand.
- Non-production environments MUST shut down or scale to zero outside documented working hours, unless the document records a reason for continuous operation.
- The team MUST identify and remove idle or orphaned resources (unattached disks, unused public IP addresses, empty clusters and unused snapshots) at least monthly, and SHOULD automate the detection.
- The design SHOULD prefer managed PaaS or serverless services over self-managed virtual machines where they meet requirements, because shared platform capacity is used more efficiently.

### Region and placement
- The design MUST document the selection of each deployment region and SHALL weigh carbon intensity, latency, data residency and service availability.
- Batch, training and other delay-tolerant workloads SHOULD be scheduled in lower-carbon regions or time windows when no latency or residency constraint prevents it.
- The design MUST NOT replicate workloads across more regions than the documented availability objective requires, and every extra region SHALL be justified by a reliability or residency requirement.
- Components that exchange large volumes of data SHOULD be co-located in the same region and zone to avoid unnecessary data transfer.

### Efficient application and data design
- The design SHOULD use caching, asynchronous and event-driven processing, and efficient data access patterns to reduce repeated computation, and the architecture document MUST identify the main hot paths for which this was considered.
- The workload MUST set a documented retention period for every data store, log and backup, and automated lifecycle rules SHALL delete or archive data when that period ends.
- Data that is rarely accessed MUST be moved to cooler or archive storage tiers by lifecycle policy rather than kept in the hot tier.
- The workload MUST NOT replicate data to additional copies, regions or tiers beyond what its recovery objectives require.
- Telemetry collection MUST be limited to data with a defined operational purpose, with log levels, sampling and retention set per environment, and verbose logging SHALL NOT be enabled by default in production.
- Container images and build artifacts MUST be retained under a defined cleanup policy, and the CI/CD pipeline SHOULD avoid redundant full builds and test runs.

### AI and data-intensive workloads
- AI workloads SHOULD use the smallest model and the lowest-precision compute that satisfy the documented quality target, and the selection rationale MUST be recorded.
- GPU and other accelerator resources MUST be released or scaled down when training or inference jobs complete, and idle accelerator time SHALL be reported at least monthly.

## Sources
- https://learn.microsoft.com/azure/well-architected/sustainability/overview
- https://learn.microsoft.com/azure/well-architected/sustainability/sustainability-design-principles
- https://learn.microsoft.com/azure/well-architected/ai/application-design
