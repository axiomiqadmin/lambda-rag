# Chapter 9 — Compute, Containers and Hosting

## Intent
Compute and container hosting decisions determine how a workload scales, recovers, and is patched for its entire life. Choosing the wrong platform, running too few instances, or shipping unverified images creates availability and security debt that is expensive to reverse. This chapter sets baseline obligations for selecting hosting platforms and for operating Azure Container Apps, Azure Kubernetes Service (AKS), Azure App Service and Azure Functions in production.

## Requirements

### Hosting platform selection
- Every workload design document SHALL record the compute platform decision, including the alternatives considered, by reference to the Azure compute decision tree. This makes the rationale for the platform reviewable.
- A design that selects Azure Container Apps express for a production workload MUST document that the workload does not need fine-grained control over compute, networking, or cold-start behavior. Workloads that need that control SHOULD use Container Apps with a workload profiles environment.
- A design that selects AKS for a new production workload SHOULD use AKS Automatic and MUST document the specific requirement, such as Windows node pools or bring-your-own CNI, that justifies AKS Standard when AKS Standard is chosen.
- Every hosting design MUST state which team owns each part of the platform, with the platform team owning cluster or plan configuration and the application team owning manifests, application identity, and workload monitoring.
- Unrelated workloads SHOULD run in separate AKS clusters. A shared AKS cluster MUST use namespaces, dedicated node pools, and network policies to enforce isolation between the workloads it hosts.

### Redundancy and availability
- Every production Container Apps app exposed through HTTP or TCP ingress SHALL run at least three replicas. Apps that are sensitive to cold starts MUST configure a minimum number of always-ready replicas.
- Every production compute deployment in a region that offers availability zones MUST enable zone redundancy for Container Apps environments, AKS node pools, and App Service plans. This keeps a single zone failure from taking the workload down.
- A production App Service app SHALL run on at least two instances. A single-instance deployment is a single point of failure.
- Compute for a critical workload MUST be deployed to at least two Azure regions, with Azure Front Door or Azure Traffic Manager routing traffic between them.
- Every production AKS cluster SHALL place application pods on user node pools, with at least three nodes in the system node pool and at least two nodes in each user node pool. System pods are thereby isolated from application resource demands.
- Every production AKS workload with more than one replica MUST define a pod disruption budget. The budget keeps node upgrades and other voluntary disruptions from removing too many pods at once.
- Stateful container workloads MUST store persistent data on volumes outside the container, and in a zone-redundant region the volumes SHOULD use zone-redundant storage.

### Health probes and self-healing
- Every container in a production deployment MUST define startup, readiness, and liveness probes. Without them the platform cannot restart failed containers or withhold traffic from containers that are not ready.
- Liveness probes MUST test only the health of the container itself and MUST NOT check downstream dependencies. A dependency outage would otherwise trigger needless restarts across every replica.
- Readiness probe and startup probe settings SHALL allow enough time for the slowest expected initialization. Probe thresholds that restart a slow-starting container prematurely MUST NOT be used.
- Applications that call other services SHOULD use platform-level resiliency policies, such as Container Apps retries, timeouts, and circuit breakers, in addition to application-level handling.

### Scaling and resource limits
- Every production container app and Functions deployment SHALL use automatic horizontal scaling with explicit scale rules, such as HTTP concurrency, queue depth, or CPU and memory thresholds, rather than manual instance counts.
- Autoscaling rules MUST be load tested before production release, and scale events SHALL be logged so scaling behavior can be reviewed.
- Every container in production MUST declare CPU and memory requests and limits. Limits prevent a single container from starving neighbors and allow usage to be tuned from monitored data.
- Session or application state SHALL NOT be held only in the memory of a single compute instance. State that must survive restarts or scale-out MUST live in an external store that scales independently.
- Azure Functions that process queue or event triggers MUST be idempotent and MUST use built-in retry policies for transient errors. Long-running, multi-step workflows SHOULD use Durable Functions.

### Image provenance and patching
- Production container images MUST be pulled only from a private registry that the organization controls, such as Azure Container Registry. Images from public registries SHALL first be imported and scanned.
- Every container image deployed to production MUST be scanned for vulnerabilities before release and on a recurring schedule afterward. Images with unresolved critical vulnerabilities MUST NOT be deployed.
- Deployments MUST reference container images by immutable digest or a unique, non-reused version tag. The mutable `latest` tag SHALL NOT be used in production.
- Container images SHOULD be built from minimal, regularly refreshed base images and SHOULD be signed so that deployment can verify their origin.
- Every AKS cluster and App Service plan MUST have a defined patching and upgrade process, including node image updates, that is exercised at least monthly and that honors pod disruption budgets.

### Observability
- Every production compute resource SHALL emit logs and metrics to Azure Monitor and SHOULD emit distributed traces through OpenTelemetry. Alerts MUST exist for availability, restarts, and scaling saturation.

## Sources
- https://learn.microsoft.com/azure/well-architected/service-guides/azure-container-apps
- https://learn.microsoft.com/azure/well-architected/service-guides/azure-kubernetes-service
- https://learn.microsoft.com/azure/well-architected/service-guides/app-service-web-apps
- https://learn.microsoft.com/azure/well-architected/service-guides/azure-functions-security
