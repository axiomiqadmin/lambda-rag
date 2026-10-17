# Chapter 5 — Data, Storage and Privacy

## Intent
Data is the asset most likely to cause lasting harm when it is exposed, altered, or lost. This chapter sets obligations for classifying data, protecting it at rest and in transit, managing encryption keys, and keeping it recoverable. Applying these obligations consistently lets each data store receive protection proportionate to the sensitivity of its contents.

## Requirements

### Data classification and ownership
- Every workload data store and every system that processes workload data SHALL carry a documented sensitivity classification drawn from the organization's classification taxonomy. Classification drives the protection level that is applied to the data.
- The design document MUST record the location of every store that holds classified data, including copies in backups, caches, analytics stores, and nonproduction environments. Data that is not located cannot be protected.
- Each data store MUST have a named owner who is accountable for its classification, access approvals, and retention. Ownership prevents orphaned data from accumulating without controls.
- Automated data discovery tooling SHOULD be used to suggest classifications, and a human review of the suggested classifications MUST occur before they are accepted. Tooling alone does not replace team accountability.
- Production data classified as confidential or restricted SHALL NOT be copied into development or test environments unless it has first been masked or anonymized.

### Encryption at rest
- All data at rest in production storage, including databases, storage accounts, managed disks, and backups, MUST be encrypted. Platform-native encryption SHOULD be used instead of custom encryption.
- Teams SHALL NOT disable platform-provided encryption in order to substitute a custom implementation unless a documented evaluation shows the platform capability cannot meet a stated requirement.
- Any custom cryptography that is permitted MUST use industry-standard algorithms and the cryptography APIs of the operating system or runtime rather than bespoke or nonplatform libraries.
- Data classified as confidential or restricted SHOULD be protected with customer-managed keys when regulation or policy requires the organization to control key rotation, revocation, or the ability to render data undecryptable.
- Any data store that is not encrypted at rest MUST have a documented, approved exception that states the technical or business reason.
- Azure SQL Database and Azure SQL Managed Instance SHALL have transparent data encryption enabled. Sensitive columns SHOULD additionally be protected with Always Encrypted when the database administrator is not trusted to read them.

### Encryption in transit
- All network traffic that carries workload data between clients and services, and between services, MUST be encrypted by using TLS 1.2 or later. Earlier protocol versions SHALL NOT be enabled.
- Azure Storage accounts MUST require secure transfer so that unencrypted HTTP requests are rejected. Database endpoints such as Azure SQL and Azure Cosmos DB MUST be accessed only over encrypted connections.
- Certificates used for data-in-transit protection MUST be issued by a trusted authority, stored in a managed certificate store such as Azure Key Vault, and renewed automatically or alerted on before expiry.

### Key and secret management
- Encryption keys, connection strings, and other secrets MUST be stored in a dedicated managed service such as Azure Key Vault or Managed HSM, and SHALL NOT be stored in source code, configuration files, or container images.
- Encryption keys MUST be rotated according to a documented schedule, and rotation SHOULD be automated. A compromised key MUST be revoked and replaced within the period defined by the incident response plan.
- Key vaults that protect production keys MUST have soft delete and purge protection enabled. This prevents a deleted key from making encrypted data permanently unrecoverable.
- Workloads SHOULD authenticate to data stores by using managed identities with Microsoft Entra ID rather than shared keys or passwords. Where shared keys remain, key-based access MUST be justified in writing.

### Access and exposure
- Production data stores SHALL NOT be reachable from the public internet unless a documented business need exists. Private endpoints or service endpoints with firewall rules SHOULD be used for Azure storage and database services.
- Access to data stores MUST follow least privilege, using role-based access control scoped to the narrowest practical resource and granted only to the identities that need it.
- Storage containers MUST NOT allow anonymous public read access unless the content is explicitly classified as public.

### Backup, retention and recovery
- Every production data store MUST have a backup strategy that states the recovery point objective and recovery time objective it satisfies. The strategy SHALL be reviewed at least annually.
- Backups of production data MUST be encrypted and stored in a location isolated from the primary data, such as a separate region or a separate account with restricted access.
- Restoration from backup MUST be tested at least annually for each production data store, and the outcome of each test SHALL be recorded.
- Each data type MUST have a defined retention period, and data SHALL be deleted or archived when that period expires. Personal data SHALL NOT be retained longer than the stated purpose requires.
- Azure Blob Storage SHOULD have soft delete or versioning enabled for production containers, and immutable storage MUST be used for data subject to legal or regulatory write-once requirements.

### Data residency and privacy
- Data subject to residency requirements MUST remain in the approved regions, and the design document SHALL state the region for every store and replica that holds such data.
- Personal data MUST be collected only for a documented purpose, and the design SHALL describe how access, correction, and deletion requests from data subjects are fulfilled.

## Sources
- https://learn.microsoft.com/azure/well-architected/security/encryption
- https://learn.microsoft.com/azure/well-architected/security/data-classification
- https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-tagging
