# Chapter 8 — Cost Optimization and FinOps

## Intent
Cloud spend grows with every design decision, so cost must be modeled, tracked, and governed continuously rather than corrected after the fact. This chapter sets obligations for cost models, budgets, tagging, allocation, rightsizing, commitment discounts, and scaling. A cost-optimized workload delivers its required value at a justified price, which is not always the lowest price.

## Requirements

### Cost model and accountability
- Every workload MUST have a documented cost model that estimates initial cost, run rate, and ongoing cost, and that includes projected growth. The model SHALL be reviewed at least annually and after any significant design change.
- Every workload MUST have a named owner who is accountable for its spending, and the design document SHALL identify that owner.
- The design document SHOULD state the cost implications of significant architectural and technology choices, including build-versus-buy and licensing models.
- Each workload budget MUST include a buffer for unplanned spending in addition to the cost model estimate.

### Budgets and alerts
- Every production subscription or resource group MUST have an Azure Cost Management budget with notification thresholds. Alerts MUST be sent to the accountable owner.
- Budget alerts MUST include at least one threshold below 100 percent of the budget and one threshold on forecasted spend.
- Cost anomaly detection or alerting SHOULD be enabled so that deviations from the spending trend are surfaced within one day.
- Cost data MUST be collected daily and reviewed by the workload owner and stakeholders at least monthly, comparing actual spend with the budget and cost model.
- Cost reports SHOULD show both incurred costs and amortized costs of prepaid commitments, along with trends and forecasts.

### Tagging and cost allocation
- Every billable Azure resource MUST carry the organization's mandatory tags, which SHALL include at least cost center or owner, workload name, and environment.
- Mandatory tags MUST be enforced by Azure Policy using deny or append and inherit effects, so that untagged resources are prevented or remediated.
- Tag names and permitted values MUST be defined in a central tagging standard, and teams SHALL NOT create variants of mandatory tag names.
- Tag inheritance SHOULD be enabled in Cost Management, so that costs of resources are attributed to the tags set at the subscription or resource group level.
- Shared platform costs MUST be allocated to consuming workloads by a documented allocation rule rather than being left unassigned.

### Showback and chargeback
- The organization MUST produce showback reports at least monthly that show each business unit or workload owner the cost it incurred.
- Where chargeback is practiced, the allocation method MUST be documented, approved by finance, and applied consistently across all consuming teams.

### Guardrails
- Spending guardrails MUST be implemented through platform automation, such as Azure Policy restrictions on permitted SKUs and regions, rather than by manual review alone.
- Resource creation rights in production MUST be restricted to approved roles or pipelines, so that unmanaged resources cannot generate unbudgeted cost.
- Nonproduction environments SHOULD use lower-cost SKUs than production and SHALL be shut down or scaled to zero outside working hours when they are not needed.

### Rightsizing and lifecycle
- Resource sizes MUST be reviewed against measured utilization at least quarterly, and resources whose sustained utilization is below the documented threshold SHALL be downsized or removed.
- Orphaned resources, such as unattached disks, unused public IP addresses, and idle gateways, MUST be identified and deleted on a recurring schedule.
- Data in Azure Storage SHOULD be moved to cooler access tiers through lifecycle management policies when its access frequency declines.
- Log and telemetry volume MUST be reviewed periodically, and low-value data SHOULD be filtered, sampled, or moved to a lower-cost tier.

### Reservations, savings plans and licensing
- Workloads with steady, predictable usage SHOULD be covered by Azure Reservations or Azure savings plans, with coverage reviewed at least annually.
- Commitments MUST NOT be purchased until at least one representative period of utilization data supports the commitment size.
- Existing eligible licenses SHOULD be applied through Azure Hybrid Benefit, and the organization MUST track license entitlements to avoid paying twice.
- Development and test workloads SHOULD use Azure Dev/Test pricing where the organization's agreement permits.

### Autoscaling
- Every production compute service with variable demand MUST use autoscaling with defined minimum and maximum limits, so that capacity follows load without exceeding the budget.
- Autoscale rules SHALL be based on measured metrics, and the maximum instance count MUST be set to a value that fits within the approved budget.
- Autoscale and scaling-to-zero behavior MUST be tested under load before production release, so that cost savings do not compromise availability objectives.

## Sources
- https://learn.microsoft.com/azure/well-architected/cost-optimization/principles
- https://learn.microsoft.com/azure/well-architected/cost-optimization/checklist
- https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-tagging
