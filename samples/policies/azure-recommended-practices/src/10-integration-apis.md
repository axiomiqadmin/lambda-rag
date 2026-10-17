# Chapter 10 — Services, APIs and Messaging

## Intent
Services communicate over networks that fail, throttle, and deliver messages more than once. Well-designed APIs, bounded retries, and durable messaging keep one component's failure from cascading and let producers and consumers evolve independently. This chapter sets obligations for API design and versioning, service-to-service calls, and asynchronous messaging on Azure.

## Requirements

### API design
- Every web API SHALL model its URIs on resources named with nouns, and collection URIs MUST use plural nouns. Verbs such as `create-order` SHALL NOT appear in resource URIs.
- Every web API MUST use HTTP methods according to their standard semantics and MUST return accurate HTTP status codes, such as 201 with a Location header for creation and 404 for missing resources.
- GET, PUT, DELETE, HEAD, and PATCH operations of every web API MUST be idempotent. A repeated request against the same resource must leave the resource in the same state.
- A POST operation that creates a resource MUST NOT cause side effects on resources unrelated to the new resource.
- Every web API operation that takes long to complete SHOULD return HTTP 202 with a status endpoint URI in the Location header, instead of holding the client connection open.
- Every web API collection endpoint MUST support pagination with documented default and maximum page sizes. The maximum limit prevents a single request from retrieving unbounded data.
- Web APIs SHOULD support content negotiation through the Accept header and MUST return 415 for unsupported request media types and 406 when no acceptable response format exists.
- Every public API MUST publish a machine-readable contract, such as an OpenAPI document, that is kept in source control with the implementation.

### API versioning and gateway
- Every API consumed outside its owning team MUST carry an explicit version in the URI, a header, or a query parameter. Unversioned public endpoints SHALL NOT be released.
- A breaking change to an API contract, such as removing a field, changing a type, or changing required parameters, SHALL be released only as a new major version. The existing major version MUST remain available during a published deprecation period.
- Every API version retirement MUST be announced to consumers in advance, with a stated end date, before the version is removed.
- Every API exposed to external or cross-team consumers SHALL be published through an API gateway, such as Azure API Management, rather than directly from the backend service. The gateway applies authentication, rate limiting, and policy consistently.
- API gateway policies for every production API MUST enforce request rate limits or quotas and MUST validate inbound requests against the published contract.

### Service-to-service communication
- Every outbound call to a remote service or dependency MUST have an explicit timeout. Calls without timeouts SHALL NOT be shipped, because blocked threads and connections can exhaust resources.
- Every retry policy MUST apply only to transient faults, use exponential backoff with jitter, and enforce a maximum attempt count. Retries for non-transient errors SHALL NOT be attempted.
- Retry logic SHALL be implemented at only one layer of a call chain. Lower layers MUST fail fast so that nested retries do not multiply delays and load.
- Every dependency call that can fail persistently MUST be wrapped by a circuit breaker with documented failure threshold, open duration, and half-open probe behavior. Retry logic MUST stop when the circuit breaker reports the circuit is open.
- Retried operations that are not naturally idempotent MUST carry an idempotency key or request identifier that the receiving service uses to discard duplicates.
- Early retry failures SHOULD be logged as informational entries and the final exhausted retry MUST be logged as an error, so that operators are not flooded with alerts for recovered faults.
- Each service MUST own its data store, and other services SHALL access that data only through the owning service's API or published events. Shared databases between independently deployed services SHALL NOT be used.
- Service boundaries SHOULD align with business capabilities, and each service MUST be independently deployable and versionable.
- Application code SHOULD use the official Azure SDK client libraries and their built-in retry and authentication support instead of hand-built REST calls to Azure services.

### Messaging
- Every messaging consumer MUST be idempotent, because Azure Service Bus and Azure Event Grid deliver at least once. Consumers SHOULD track processed message identifiers to detect duplicates.
- Every Service Bus queue and subscription SHALL have a dead-letter queue policy and a documented process for reviewing and reprocessing dead-lettered messages. Poison messages MUST NOT block healthy message processing.
- Production Service Bus namespaces that need zone or regional redundancy SHALL use the Premium tier, with zone redundancy and Geo-Replication enabled. The design MUST document how and when a secondary region is promoted.
- Every message and event schema SHALL be versioned, and changes to published schemas MUST remain backward compatible for existing consumers or be released as a new schema version.
- Service Bus consumers MUST handle `QuotaExceeded` and other throttling errors, and SHOULD use a circuit breaker to stop sending while the condition persists.
- The messaging technology for each integration, such as Service Bus, Event Grid, or Event Hubs, MUST be chosen from the documented messaging decision criteria and the choice SHALL be recorded in the design.

## Sources
- https://learn.microsoft.com/azure/architecture/best-practices/api-design
- https://learn.microsoft.com/azure/architecture/best-practices/api-implementation
- https://learn.microsoft.com/azure/architecture/patterns/retry
- https://learn.microsoft.com/azure/architecture/patterns/circuit-breaker
- https://learn.microsoft.com/azure/well-architected/service-guides/azure-service-bus
