# Chapter 7 — Operational Excellence, CI/CD and Infrastructure as Code

## Intent
Reliable operations depend on repeatable, automated, and reviewed change. This chapter sets obligations for infrastructure as code, pipelines, environment separation, deployment safety, secret handling, documentation, and change management. Following them reduces human error, configuration drift, and the blast radius of faulty releases.

## Requirements

### Infrastructure as code
- All infrastructure and configuration changes to production environments MUST be made through infrastructure as code and deployed through a CI/CD pipeline. Manual changes in the Azure portal SHALL NOT be made to production except during a documented emergency.
- Infrastructure as code SHOULD use a declarative approach, such as Bicep or Terraform, in preference to imperative scripts.
- Infrastructure as code definitions MUST be stored in version control and reviewed through pull requests before they are merged to the deployment branch.
- Infrastructure as code MUST be organized into modules with defined inputs and outputs, and module versions SHALL be pinned.
- Deployment code SHALL NOT reference the latest version of a tool, API, provider, or module. Every such reference MUST name an explicitly tested version.
- The organization SHOULD maintain a style guide that standardizes the infrastructure as code tools, naming, and structure used by all teams.
- Configuration drift between deployed resources and their code definitions MUST be detected on a recurring schedule, and detected drift SHALL be corrected through code rather than manual edits.

### Environments
- Every workload MUST have at least one nonproduction environment that matches production in topology and configuration, so that releases are validated before they reach production.
- All environments of a workload SHALL be created from the same infrastructure as code definitions, differing only by parameter values.
- Production and nonproduction environments MUST be isolated by separate subscriptions or resource groups, with separate identities and access assignments.

### CI/CD pipelines
- Every production deployment MUST be performed by an automated pipeline, and the pipeline definition SHALL be stored in version control.
- Pipelines MUST run automated build, unit test, static analysis, and dependency vulnerability scanning stages before an artifact can be promoted.
- A single build artifact MUST be produced once and promoted unchanged through every environment, rather than being rebuilt per environment.
- Pipeline identities MUST use least-privilege permissions scoped to the target environment, and SHOULD authenticate to Azure by using workload identity federation instead of stored credentials.
- Production deployments MUST require approval from at least one person other than the author of the change.

### Secrets in pipelines
- Secrets SHALL NOT be committed to source control, embedded in pipeline definitions, or printed in pipeline logs. Pipelines MUST retrieve secrets at run time from a secret store such as Azure Key Vault or the pipeline platform's protected secret storage.
- Source repositories MUST have secret scanning enabled, and any credential detected in a repository MUST be rotated immediately.
- Pipeline service connections and tokens MUST have a defined owner and SHALL be reviewed at least annually.

### Branching and code review
- The default branch of every workload repository MUST be protected so that direct pushes are rejected and changes require a reviewed pull request with passing status checks.
- Teams SHOULD use short-lived branches and integrate to the default branch at least daily, so that changes remain small and incremental.

### Deployment safety
- Every deployment, including code, infrastructure, feature flag, and configuration changes, MUST follow the same standardized, automated process. No category of production change SHALL be treated as exempt from risk management.
- Production releases MUST use progressive exposure, such as canary, blue-green, or ring-based rollout, so that a faulty release affects only a subset of users before it is promoted.
- A health check MUST pass before a deployment advances to each subsequent stage of progressive exposure, and the deployment MUST halt automatically when health checks fail.
- Every production deployment MUST have a tested rollback or roll-forward procedure, and the procedure SHALL be exercised at least annually.
- Emergency or hotfix deployments MUST have a documented expedited process that still uses the pipeline and retains an audit record.

### Documentation and decision records
- Significant architectural decisions MUST be recorded as architecture decision records that state the context, the decision, the alternatives considered, and the consequences. Records SHALL be stored in the repository alongside the code.
- Each production workload MUST maintain runbooks for routine operations and recovery procedures, and the runbooks SHALL be reviewed at least annually.

### Change management
- Every change to a production workload MUST be traceable to a work item and a commit, so that the reason for any production change can be audited.
- Changes classified as high risk MUST have a documented risk assessment, a defined maintenance window, and a communication plan before deployment.

## Sources
- https://learn.microsoft.com/azure/well-architected/operational-excellence/infrastructure-as-code-design
- https://learn.microsoft.com/azure/well-architected/operational-excellence/safe-deployments
