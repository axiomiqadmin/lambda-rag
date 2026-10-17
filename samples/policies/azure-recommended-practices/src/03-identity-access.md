# Chapter 3 — Identity and Access Management

## Intent
Identity is the primary security perimeter for cloud workloads, covering people, services, and anonymous callers alike. This chapter requires one trusted identity provider, strong and conditional authentication, least-privilege authorization, and auditable access. Together these controls ensure that every action is attributable and limited to what is necessary.

## Requirements

### Identity provider and authentication
- Every production workload SHALL authenticate all human and system identities through Microsoft Entra ID or another centrally managed identity provider approved by the organization.
- Production workloads MUST NOT implement custom user stores or custom authentication mechanisms in place of the approved identity provider.
- Every production workload SHALL use modern authentication and authorization protocols such as OpenID Connect and OAuth 2.0, and legacy protocols such as basic authentication MUST NOT be enabled.
- Every production workload SHALL document both outside-in access (users and operators reaching the workload) and inside-out access (the workload reaching other resources), together with the identity used for each flow.
- Every production workload SHALL define which resources are accessed anonymously, and anonymous access MUST be limited to resources that are intended to be public.

### Multifactor and conditional access
- Every interactive human account with access to production resources SHALL be protected by multifactor authentication.
- Every administrative role assignment on production resources MUST require phishing-resistant or conditional-access-enforced multifactor authentication.
- Production access policies SHOULD apply Conditional Access that evaluates user risk, sign-in risk, device compliance, and location before granting access.
- Legacy authentication protocols SHALL be blocked by Conditional Access policy for all accounts with access to production resources.
- Break-glass emergency accounts SHALL be limited to at most two per tenant, MUST be excluded from normal Conditional Access dependencies only by documented design, and SHALL have every sign-in alerted.

### Authorization and least privilege
- Every role assignment on production Azure resources SHALL follow least privilege and be scoped to the narrowest resource, resource group, or subscription that meets the need.
- Role assignments for production resources SHOULD be made to groups rather than individual users, so that access follows team membership.
- Every production workload SHALL separate control-plane roles from data-plane roles and MUST NOT grant control-plane permissions to identities that need only data access.
- The Owner and User Access Administrator roles on production subscriptions SHALL be assigned to no more than the number of named individuals documented by the platform team, and MUST NOT be assigned to workload service identities.
- Custom Azure roles SHALL be created only when no built-in role satisfies the requirement, and each custom role MUST have a documented owner and justification.
- Every subscription and management group SHALL have Azure RBAC assignments reviewed against actual need at least semiannually.

### Privileged access
- Standing privileged access to production resources SHOULD be replaced with just-in-time elevation using Microsoft Entra Privileged Identity Management.
- Every privileged elevation on production resources SHALL be time-bound with a maximum activation duration of 8 hours and MUST require a recorded business justification.
- Administrators SHALL use separate, dedicated accounts for privileged operations and MUST NOT use those accounts for email or general web browsing.

### Workload and resource identities
- Every workload component that authenticates to an Azure service SHALL use a managed identity or workload identity federation, and static client secrets MUST NOT be used where those options are available.
- Every workload component SHALL have its own distinct identity, and an identity MUST NOT be shared across unrelated workloads or across production and non-production environments.
- Where a service principal secret or certificate cannot be avoided, the credential SHALL expire within 12 months and be rotated before expiry.
- Shared access signature tokens SHALL be time-limited, scoped to the narrowest permission, and issued by user delegation where the service supports it.
- Storage account shared-key access and similar key-based authorization SHOULD be disabled on production resources that support identity-based authorization.

### Lifecycle and audit
- Every human identity with access to production resources SHALL be removed or disabled within 24 hours of the person leaving the organization or the role requiring the access.
- Every guest or external identity in the tenant SHALL be sponsored by a named internal owner and reviewed at least quarterly.
- Every production workload SHALL log all sign-ins, authorization decisions, and role-assignment changes to a central store retained for at least 12 months.
- Every access review of production role assignments MUST record the reviewer, the date, and the decision for each assignment reviewed.

## Sources
- https://learn.microsoft.com/azure/well-architected/security/identity-access
- https://learn.microsoft.com/azure/well-architected/security/checklist
- https://learn.microsoft.com/azure/well-architected/security/application-secrets
