# Chapter 11 — Governance, Compliance and Landing Zones

## Intent
Governance determines whether an Azure estate stays secure, compliant, and manageable as the number of subscriptions and teams grows. Guardrails defined once in a management group hierarchy and enforced with Azure Policy scale far better than manual reviews. This chapter sets obligations for landing zone structure, policy, access governance, naming, tagging, and compliance evidence.

## Requirements

### Management groups and subscriptions
- The Azure management group hierarchy SHALL be kept flat, with no more than four levels below the tenant root management group. Deep nesting raises management overhead and obscures inherited policy.
- Management groups MUST be used to assign policy and initiatives to subscriptions with common security and compliance needs, and SHALL NOT be used to mirror the organizational chart.
- The hierarchy SHALL include a platform management group, a landing zones management group with child groups by workload type such as corp and online, and a sandbox management group for experimentation.
- Separate management groups SHALL NOT be created for production, test, and development environments. Environments SHOULD be separated by subscription within the same management group.
- Management groups SHALL NOT be created solely to model Azure regions. A location-based hierarchy MAY be used only where data residency or sovereignty regulation requires it.
- A default management group other than the tenant root MUST be configured for new subscriptions, so no subscription is created directly under the root.
- Azure RBAC authorization for management group operations MUST be enabled in the hierarchy settings so that only privileged principals can create or modify management groups.
- Every new workload landing zone MUST be created through an automated subscription vending process that applies baseline policy, role assignments, tags, and networking.
- Each workload SHALL be assigned its own subscription or subscriptions. Workloads with different security, compliance, or scale boundaries MUST NOT share a subscription.

### Azure Policy
- Compliance and security conventions, including allowed regions, allowed resource types, and required diagnostic settings, MUST be enforced with Azure Policy at management group or subscription scope instead of by manual review.
- The number of Azure Policy assignments at the tenant root management group SHALL be kept to a minimum. Root-scope assignments MUST be limited to controls that apply to every subscription.
- Policy definitions SHALL be written once and reused through initiatives at multiple inherited scopes, and all definitions and assignments MUST be managed as code in source control.
- Every new policy MUST be rolled out first in audit mode or at a limited scope and reviewed for impact before its effect changes to deny or enforce.
- Every non-compliant resource that Azure Policy reports MUST be remediated or covered by an approved exception, and remediation tasks SHOULD be used to correct existing resources automatically.

### Access governance
- Workload team members SHALL NOT receive RBAC role assignments at management group scope. Their roles MUST be assigned at the subscription or resource group scope that they need.
- Standing RBAC assignments at management group scope for platform teams SHOULD be eligible, not permanent, through Microsoft Entra Privileged Identity Management with activation limited to the time needed.
- Every RBAC role assignment SHALL follow least privilege, and assignments MUST be made to groups rather than to individual users where practical.
- Access to every production subscription MUST be reviewed at least every six months, and assignments that are no longer needed MUST be removed.

### Naming and tagging
- The organization SHALL define and publish one resource naming convention that specifies the ordering of components such as resource type, workload, environment, region, and instance number. Every Azure resource MUST follow it.
- Resource names MUST NOT contain sensitive, personal, or confidential information, because names and tags are visible to many services and are not treated as customer data.
- Every Azure resource that supports tags MUST carry a defined mandatory tag set that covers at least workload, environment, owner, and cost center. Azure Policy MUST enforce or append these tags.
- Tag names SHALL use consistent lowercase keys and a controlled vocabulary of values, because tag values are case-sensitive. Tags SHALL NOT hold secrets or sensitive data.

### Compliance, audit and exceptions
- The organization MUST map each applicable regulatory or contractual obligation, such as HIPAA, PCI-DSS, or SOC 2, to specific Azure Policy initiatives or controls, and MUST review the mapping at least annually.
- Compliance status for every production subscription SHALL be evaluated continuously through Azure Policy compliance reporting and Microsoft Defender for Cloud. The results MUST be retained as audit evidence.
- Azure Activity logs for every subscription MUST be exported to a central Log Analytics workspace or storage account with a defined retention period.
- Every exception to a policy or standard MUST be recorded with an owner, a documented justification, a compensating control, and an expiration date no later than twelve months after approval. Expired exceptions MUST be re-approved or removed.
- Designs for new or materially changed production workloads SHALL be reviewed against the Well-Architected Framework by an architecture review function. The review SHOULD be supported by automated policy guardrails rather than manual gating of every change.

## Sources
- https://learn.microsoft.com/azure/cloud-adoption-framework/ready/landing-zone/design-area/resource-org-management-groups
- https://learn.microsoft.com/azure/cloud-adoption-framework/ready/landing-zone/design-area/governance
- https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming
- https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-tagging
