# Chapter 4 — Network Architecture and Edge Protection

## Intent
Network controls add defense in depth to identity controls by hiding resources, filtering traffic, and limiting lateral movement. This chapter requires deliberate segmentation, a well-defined hub-and-spoke or equivalent topology, strict filtering of inbound and outbound flows, and private connectivity to platform services. The goal is a small, well-monitored public surface and contained internal movement.

## Requirements

### Traffic classification and topology
- Every production workload SHALL classify each network flow by direction (ingress or egress), scope (north-south or east-west), and visibility (public or private) in its design documentation.
- Every Azure landing zone network design SHALL use a documented topology, such as hub-and-spoke or Azure Virtual WAN, with shared connectivity services placed in a dedicated connectivity subscription.
- Workloads requiring internet exposure SHOULD be placed in subscriptions separate from workloads that are reachable only through private or hybrid connectivity.
- Every virtual network address space SHALL be allocated from an enterprise IP address plan and MUST NOT overlap with any other virtual network or on-premises range that it may connect to.

### Segmentation
- Every production workload SHALL be deployed in virtual networks dedicated to that workload or its landing zone, and production and non-production resources MUST NOT share a virtual network.
- Every virtual network in a production workload SHALL be divided into subnets by tier or security requirement, and each subnet MUST have a network security group attached.
- Every network security group SHALL end with a rule that denies all traffic not explicitly allowed, and allow rules MUST specify the narrowest practical source, destination, and port.
- Network security group rules MUST NOT allow any-to-any traffic, and inbound rules from the internet to management ports such as RDP (3389) and SSH (22) SHALL NOT exist.
- Virtual network peerings SHALL be created only between networks that have a documented communication need, and forwarded traffic and gateway transit MUST be enabled only where the design requires it.
- Application security groups or service tags SHOULD be used in network security group rules instead of hard-coded IP addresses where the platform supports them.

### Edge and ingress protection
- Every public HTTP or HTTPS workload SHALL be exposed through a reverse-proxy or gateway such as Azure Application Gateway or Azure Front Door, and backend compute MUST NOT have public IP addresses.
- Every internet-facing web application SHALL be protected by a web application firewall in prevention mode with the managed rule set enabled.
- Every internet-facing workload SHALL be protected by Azure DDoS Protection at the network or IP-protection tier.
- Every public endpoint SHALL accept only TLS 1.2 or later and MUST redirect or reject plaintext HTTP requests.
- Every public IP address in production SHALL have a documented owner and business purpose, and unused public IP addresses MUST be removed.
- Remote administration of production virtual machines SHALL be performed through Azure Bastion or an equivalent just-in-time access method, and direct inbound management access MUST NOT be used.

### Egress control
- All internet-bound outbound traffic from production virtual networks SHALL be routed through a central egress control such as Azure Firewall or an approved network virtual appliance using user-defined routes.
- Egress rules in production SHALL permit only documented destinations, and a default allow-all outbound rule MUST NOT be used.
- Azure Firewall policies SHOULD enable threat-intelligence filtering in alert-and-deny mode and, for workloads handling sensitive data, SHOULD enable TLS inspection and intrusion detection.
- Every firewall rule change in production SHALL follow a documented change approval process, and firewall rules MUST be reviewed at least semiannually.

### Private connectivity and DNS
- Every production Azure PaaS data service holding sensitive data SHALL be reached through Private Link private endpoints, and public network access on that service MUST be disabled.
- Every production Azure virtual network that uses private endpoints SHALL resolve them through Azure Private DNS zones linked to that network or to a central DNS resolver.
- Hybrid connectivity for production workloads SHALL use ExpressRoute or IPsec site-to-site VPN, and ExpressRoute circuits serving critical flows SHOULD be paired with a redundant path in a distinct location.
- Traffic between production components in different regions or networks SHALL traverse the Azure backbone or a private connection and MUST NOT traverse the public internet unencrypted.

### Monitoring
- Every network security group and Azure Firewall instance in production SHALL send flow or diagnostic logs to a central Log Analytics workspace, and these logs MUST be retained for at least 90 days.
- Every production virtual network SHOULD have Azure Network Watcher enabled and traffic analytics reviewed at least monthly.

## Sources
- https://learn.microsoft.com/azure/well-architected/security/networking
- https://learn.microsoft.com/azure/cloud-adoption-framework/ready/landing-zone/design-area/network-topology-and-connectivity
- https://learn.microsoft.com/azure/well-architected/security/checklist
