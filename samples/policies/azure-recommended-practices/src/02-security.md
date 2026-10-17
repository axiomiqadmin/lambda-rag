# Chapter 2 — Security Posture and Threat Protection

## Intent
Security failures expose confidential data, damage integrity, and interrupt service. This chapter requires a measurable security baseline, classified data, strong encryption, hardened components, protected secrets, and monitoring and response capability that assume an attacker will eventually succeed. Layered controls limit the blast radius of any single failure.

## Requirements

### Baseline and secure development
- Every production workload SHALL be assessed against a documented security baseline that is aligned with applicable compliance obligations and with Azure platform recommendations.
- Every production workload SHALL measure its configuration against the security baseline at least quarterly, and every deviation MUST be tracked to closure or to a documented, approved exception.
- Every software development team building production workloads SHALL follow a documented secure development lifecycle that includes threat modeling during design.
- Every build pipeline for a production workload MUST include automated static code analysis and dependency vulnerability scanning, and it SHALL block release when a critical-severity finding is open.
- Every source repository for a production workload SHALL have secret scanning enabled, and credentials MUST NOT be committed to source control.

### Data classification and encryption
- Every data store and data-processing system in a production workload SHALL be assigned a sensitivity classification and an information-type label.
- The classification of a data set MUST determine the minimum encryption, retention, and access controls applied to it.
- All production data at rest SHALL be encrypted with platform-native encryption using industry-standard algorithms.
- All production data in transit SHALL be encrypted with TLS 1.2 or later, and plaintext protocols MUST NOT be used between production components.
- Data classified at the highest sensitivity level SHOULD be protected with customer-managed keys held in Azure Key Vault or Azure Managed HSM.
- Every customer-managed encryption key SHALL have a documented rotation schedule and MUST be recoverable through soft delete and purge protection.
- Production workloads SHALL NOT disable platform-provided encryption in order to implement custom cryptography, and custom cryptographic algorithms MUST NOT be created.
- Every case where encryption of a production data store is not applied SHALL have a documented technical or business justification approved by the security owner.

### Hardening and patching
- Every compute, data, and platform resource in a production workload SHALL be deployed from a hardened configuration that disables unused features, ports, accounts, and services.
- Every virtual machine in a production workload SHALL receive operating-system security updates through an automated patching process, and critical updates MUST be applied within 14 days of release.
- Every container image deployed to production SHALL be built from an approved base image and scanned for vulnerabilities, and images with unremediated critical vulnerabilities MUST NOT be deployed.
- Every production Azure subscription SHOULD have Microsoft Defender for Cloud enabled with its recommendations triaged at least monthly.

### Secrets management
- Every application secret in a production workload SHALL be stored in a dedicated secret store such as Azure Key Vault, and secrets MUST NOT be stored in source code, container images, or plain-text configuration files.
- Every production workload SHOULD use managed identities in place of stored credentials wherever the target service supports them.
- Every production secret SHALL have a defined rotation interval, and the secret management process MUST support emergency rotation outside the schedule.
- Every consumer of a preshared key SHALL receive its own distinct key, and the same key MUST NOT be used in both production and non-production environments.
- Every access to production secrets and every change to them SHALL be written to an audit log that is retained and monitored.

### Threat detection and monitoring
- Every production workload SHALL forward security-relevant logs from compute, identity, network, and data tiers to a central security log store.
- Every production workload SHALL have threat detection enabled that raises alerts into the organization's security operations triage process.
- Every security alert rule SHALL have a named owner and MUST be reviewed at least annually for relevance and false-positive rate.
- Security log data for production workloads MUST be retained for at least 12 months, or longer where a compliance obligation requires it.

### Security testing and incident response
- Every production workload SHALL undergo penetration testing at least annually and after significant architectural change.
- Every production workload SHALL test its threat detection rules at least annually by simulating attacker behavior.
- Every production workload SHALL have a documented security incident response plan that names the team or individual responsible for each procedure.
- The security incident response plan of a production workload MUST be rehearsed at least annually, and lessons learned SHALL be recorded and fed back into the baseline.

## Sources
- https://learn.microsoft.com/azure/well-architected/security/checklist
- https://learn.microsoft.com/azure/well-architected/security/encryption
- https://learn.microsoft.com/azure/well-architected/security/application-secrets
