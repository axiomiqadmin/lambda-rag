# Chapter 13 — Multitenancy and SaaS Architecture
## Intent
A multitenant solution serves several customers from shared components, so a weakness in isolation, fairness or tenant identification affects every customer at once. This chapter sets obligations for defining tenants, choosing tenancy models, keeping tenant data separate, controlling noisy neighbours, and automating the tenant lifecycle. Following it lets a provider scale tenants without trading away security, reliability or cost transparency.
## Requirements
### Tenant definition and tenancy model
- The solution architecture document MUST define what a tenant is for the solution (for example a customer organization, a customer division, or a consumer group) and MUST state how tenants differ from individual users. An unclear tenant definition leads to inconsistent isolation and billing boundaries.
- The architecture document MUST state the expected number of tenants at launch and the planned growth, and the tenancy model MUST be justified against that projection.
- The architecture document MUST record, for each solution component (compute, data, messaging, identity), the selected tenancy model (shared, sharded or dedicated per tenant) together with the isolation, cost and compliance reasons for the choice.
- The architecture document SHALL document the tenancy-model decision as an architecture decision record that lists business objectives, compliance needs, scale, automation capacity and SLA expectations as decision inputs.
- Where a design deploys dedicated Azure resources per tenant, the design SHOULD place each tenant in its own Azure subscription or SHALL document how subscription quotas and limits are managed as the tenant count grows.
- The architecture document MUST distinguish logical tenants from physical deployments when more than one tenant can share a deployment, and MUST describe how tenants are mapped to deployments.

### Tenant isolation and data protection
- The design MUST include a documented tenant isolation model that identifies, for every shared component, how one tenant is prevented from reading or modifying another tenant's data.
- Application code and data-access layers MUST apply the tenant identifier as a mandatory filter or partition key on every query against shared data stores. Row-level security, per-tenant schemas, per-tenant databases or equivalent controls SHALL back the application-level filter where the data store supports them.
- The design MUST NOT allow any request to reach tenant data without a resolved and authorized tenant context.
- The design MUST test tenant isolation continuously, including automated cross-tenant access tests in the release pipeline for every production release.
- Tenants with stricter isolation or compliance needs SHOULD be offered a dedicated deployment or dedicated data store, and the document MUST describe how those tenants are routed to it.
- The design MUST document the data residency location of each tenant's data and SHALL ensure that tenant data, backups and replicas remain within the region or geography the tenant contracted for.
- The solution MUST support per-tenant encryption key options (for example customer-managed keys in Azure Key Vault) wherever tenants have contractual requirements for key control, and the design SHALL document which tiers receive them.
- Custom domain handling MUST include a process for removing DNS records when a tenant is offboarded, so that dangling DNS entries cannot be used for subdomain takeover.

### Tenant-aware identity and request mapping
- The identity design MUST map every authenticated request to exactly one tenant by using a verifiable claim in the token, a trusted lookup, or another documented mechanism, and MUST NOT derive the tenant from an unvalidated client-supplied value.
- The architecture document MUST state how tenant context is propagated across services, queues and background jobs, so that downstream components enforce the same tenant boundary.
- The design SHALL apply least privilege and zero trust principles to tenant administrators, and tenant-level roles MUST NOT grant access to platform-level management operations.
- Operator and support access to tenant data MUST be limited to approved, time-bound and logged sessions.

### Noisy neighbour and fairness
- The design MUST include resource governance that prevents a single tenant from exhausting shared capacity, using per-tenant quotas, rate limits or throttling that return a defined error to the caller.
- The platform MUST monitor resource consumption per tenant as well as in aggregate, and SHALL alert on per-tenant usage spikes before shared capacity is exhausted.
- The design SHOULD offer higher-isolation tiers (reserved throughput, premium service tiers or dedicated instances) for tenants whose workload would otherwise threaten shared capacity.
- The team MUST define service-level objectives for the solution based on tenant requirements and SHALL load-test the solution at the projected maximum tenant count and at the projected peak load of its largest tenant.

### Tenant lifecycle, metering and operations
- The platform MUST automate tenant onboarding, configuration, upgrade and offboarding by using infrastructure as code or equivalent scripted workflows, and manual per-tenant provisioning steps SHALL NOT exist for production tenants.
- The offboarding process MUST delete or return tenant data within a documented retention period and MUST record the completion of deletion for audit.
- The solution MUST measure consumption per tenant and SHALL correlate it with infrastructure cost, so that the pricing model reflects actual resource use.
- The design SHOULD separate the control plane (tenant management, provisioning) from the data plane (tenant workload traffic) and MUST document their different scaling and availability requirements.
- The release process MUST define how service updates are rolled out across tenants, including whether tenants can be assigned to update rings or deferred, and SHALL support rollback per ring.

## Sources
- https://learn.microsoft.com/azure/architecture/guide/multitenant/overview
- https://learn.microsoft.com/azure/architecture/guide/multitenant/considerations/tenancy-models
- https://learn.microsoft.com/azure/architecture/guide/multitenant/checklist
- https://learn.microsoft.com/azure/architecture/antipatterns/noisy-neighbor/noisy-neighbor
