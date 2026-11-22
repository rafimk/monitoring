# Development task: .NET Background Job Monitoring Platform

## Goal and agreed boundaries

Build a reusable .NET job platform with a standardized runner and scheduling mechanism, plus a monitoring dashboard. Keep the worker project in the same solution as the API but deploy it independently. Host the dashboard and its supporting API separately from the worker. Scheduled jobs commonly run every one or two minutes; they must continue when an IIS-hosted API is idle or recycled.

## Decisions to finalize early

- Select the database provider, migration tool, and durable coordination approach compatible with the target infrastructure. Ensure multiple worker replicas cannot claim the same due occurrence.
- Select the authentication provider and roles (`viewer`, `operator`, `administrator`), including how the dashboard obtains API credentials.
- Choose whether cron scheduling belongs in the first release; fixed intervals of one and two minutes are required. Document missed-run behavior and time-zone/DST policy.
- Decide how consuming applications supply job implementations: initially compile/register implementations in a worker host; define a stable contract for future packaged modules. Do not execute arbitrary uploaded code.
- Agree alert channels, initial retention period, and whether live dashboard updates use polling or server push. These are extension points and must not block the core runner.

## Functional requirements

1. Register discoverable job implementations under unique, stable keys with display name and description; validate definitions and report missing implementations.
2. Create, edit, enable, and disable schedules. Support fixed intervals, including one and two minutes; calculate and persist the next due UTC timestamp.
3. Dispatch due jobs durably, with atomic claim and next-due update, across one or more workers. Define missed-run policy (skip, catch-up with cap, or one coalesced run) per schedule or globally.
4. Run jobs through a DI scope with typed execution context, cancellation token, timeout, bounded concurrency, and a consistent result/error contract.
5. Support durable manual triggering, retry policy, attempt history, and per-job nonoverlap or configured concurrency limits. Define behavior when an earlier one-minute run exceeds its interval.
6. Persist status transitions (`Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled`, `TimedOut`, `Abandoned`), timestamps, worker identity, duration, correlation ID, and sanitized error details.
7. Heartbeat active worker instances and running jobs; recover expired claims without silently losing or duplicating an occurrence. Execution is at-least-once when a worker fails, so job code must be idempotent.
8. Expose read-only monitoring views and authorized controls through a versioned API; paginate and filter histories. Return clear validation and concurrency errors.
9. Provide a responsive React dashboard with summary cards, job list/detail, upcoming/running/failed runs, history filters, run-now and enable/disable controls, and visible stale-worker indicators.
10. Add structured logs, health/readiness checks, metrics or equivalent operational counters, audit records for administration, and configurable history retention.

## Proposed solution layout

```text
monitoring/
  README.md
  task.md
  src/
    Monitoring.Contracts/
    Monitoring.Application/
    Monitoring.Infrastructure/
    Monitoring.Api/
    Monitoring.Worker/
    monitoring-web/
  tests/
    Monitoring.UnitTests/
    Monitoring.IntegrationTests/
```

Keep application use cases independent of the HTTP host. The worker owns dispatch and job execution; the API reads state and enqueues commands. Persist job definitions, schedules, run requests, attempts, worker heartbeats, and audit entries in the platform database. Use UTC internally and indexed queries for due schedules and recent runs. Protect lease and run transitions with transactional compare-and-set or an equivalent provider-specific primitive.

## Implementation phases

### Phase 1 — Foundation

- Create the .NET solution and React frontend with pinned versions, configuration validation, local development profiles, logging, and CI build.
- Define contracts, job and run state machines, scheduling and missed-run policies, and database schema/migrations.
- Provide a sample job and seed data confined to development.

**Exit:** Solution builds; migrations apply; sample definition is visible in storage; invalid configurations fail clearly at startup.

### Phase 2 — Durable scheduler and runner

- Implement schedule calculation, polling, atomic due claims, durable queue/run requests, scoped DI execution, cancellation, timeouts, retry/backoff, and per-job concurrency.
- Implement worker and run heartbeats, lease expiry, restart recovery, graceful shutdown, and bounded parallelism.
- Add integration tests for concurrent workers, restart during execution, missed intervals, and a run longer than its interval.

**Exit:** A one-minute job runs repeatedly with the API stopped; two workers do not dispatch the same due occurrence twice under normal operation; failed jobs have visible attempts and deterministic retry behavior.

### Phase 3 — Monitoring API

- Implement job list/detail, schedules, upcoming runs, paginated run/attempt history, summary statistics, and worker status endpoints.
- Add authenticated, authorized manual trigger and enable/disable/edit operations; validate inputs and audit changes.
- Add liveness/readiness checks and API integration tests.

**Exit:** The API exposes accurate persisted state, rejects unauthorized operations, and does not need to be online for schedules to fire.

### Phase 4 — React dashboard

- Build responsive overview, job details, run history/error view, filters, controls, loading/error/empty states, and stale-heartbeat cues.
- Connect to the API with a configurable base URL and selected authentication flow; ensure accessible controls and sensible polling/update frequency.

**Exit:** An operator can locate a failure, inspect its attempts, trigger an allowed job, and see updated status without using the database directly.

### Phase 5 — Operations and release

- Add retention cleanup, backup/restore guidance, security review, metrics/alert hooks, deployment manifests/scripts, and offline package inventory.
- Exercise Windows Service or systemd hosting, IIS API hosting if selected, database migration, multi-worker coordination, worker crash recovery, and API recycle behavior.
- Document exact build, configuration, deployment, upgrade, and rollback commands in `README.md` once the implementation exists.

**Exit:** Production-like deployment passes the acceptance checks below and can be installed without runtime internet access.

## Acceptance criteria

- A one-minute and a two-minute scheduled job each execute at the expected cadence within a documented tolerance while the API is stopped or its IIS pool is idle/recycled.
- Restarting the worker resumes scheduling from persisted state; the configured missed-run policy is observable and documented.
- Concurrent worker instances cannot claim the same schedule occurrence under normal operation. A crash during execution leads to a recoverable, traceable outcome; any possible repeat execution is documented and handled by idempotent sample code.
- A long-running job obeys its concurrency and timeout settings. Retry attempts respect configured delays and maximum attempts.
- Manual triggers are durable, authorization checked, and distinguishable from scheduled runs in history.
- Every run has queryable timestamps, status, attempts, duration, worker identity, and sanitized failure information. History filters and pagination work with realistic data volumes.
- Dashboard state matches API state, shows stale/offline workers, and permits only role-authorized controls.
- Secrets do not appear in responses or logs; migrations, health checks, retention, and offline installation steps are documented and verified.
- Automated tests cover scheduling calculations, state transitions, atomic claiming, recovery, API permissions, and major dashboard flows.

## Deployment deliverables

- Versioned API, worker, and frontend release artifacts with pinned dependencies and database migrations.
- Sample configuration templates for local and production environments, without secrets.
- Windows Service installation or systemd unit instructions, plus IIS/API and static dashboard hosting instructions.
- Operational runbook: startup order, health checks, worker heartbeat, failure triage, backups, upgrade/rollback, retention, and offline dependency transfer.

## Scope review and next work (22 November 2026)

The current solution implements the separate worker, SQL Server interval scheduling, durable runs and retries, API queries and controls, and a React dashboard. `Monitoring.Contracts` lets code compiled into the worker implement `IJob`. It does **not** let another independently deployed application report its own background jobs to this dashboard. The following integration is required for that use case.

### External application publisher library

- Add a separately packable `Monitoring.Publisher` .NET class library with `AddMonitoringPublisher(...)` DI registration, `IMonitoringPublisher`, typed request/result contracts, an `HttpClient` implementation, and configuration validation. It must have no EF Core, worker, or SQL Server dependency.
- Applications assign a stable `ApplicationKey` and `JobKey`. Publish lifecycle events for `Started`, `Succeeded`, `Failed`, and optionally `Progress`/`Heartbeat`, including external run ID, attempt ID or number, UTC timestamps, correlation ID, duration, short message, and safe metadata. Never send secrets or full exception objects.
- Provide an authenticated ingestion API using a machine identity or per-application credential with a dedicated `publisher` permission. Do not reuse the dashboard's interactive operator token or issue production tokens through the development token endpoint. Scope each credential to its application key and enforce payload size and rate limits.
- Persist external applications, external job definitions, run events, and deduplication keys. Use a unique `(ApplicationKey, ExternalRunId, EventId)` or equivalent key so retried HTTP requests are idempotent. Handle out-of-order arrival, repeated terminal events, missing starts, and stale running reports explicitly.
- Keep external reporting separate from worker-owned queue and lease transitions. Reporting must never enqueue or execute external jobs on the monitoring worker. Expose external runs in the existing job and run views, with source/application labels and no `Run now` control unless a separate remote-command protocol is built.
- Implement bounded retry with backoff for transient HTTP failures; permit cancellation, short timeouts, and optionally a durable local outbox for critical applications. State clearly that an in-memory retry alone can lose events during process shutdown or API downtime.
- Add a minimal consumer sample (`services.AddMonitoringPublisher(...)`; `StartAsync`; `SucceededAsync`/`FailedAsync`), NuGet packaging metadata, a versioned wire contract, and offline-feed publishing instructions.
- Test authentication isolation, duplicate delivery, out-of-order lifecycle, API outage/retry, safe error handling, and dashboard visibility across multiple applications.

**Acceptance:** Two separate sample applications can report runs with the same job key under different application keys; each appears independently on the dashboard. Replaying an event does not create another run or attempt. A reporting outage does not affect the applications' own job execution. The monitoring worker never claims an external run.

### Remaining operational work

- Add retention cleanup, cron scheduling if needed, alert channels, dashboard schedule editing, production identity integration, deployment scripts/runbook, and offline dependency inventory.
- Add worker concurrency, crash recovery, long-running overlap, and API recycle integration tests. Current tests mainly cover schedule calculations and API behavior; the end-to-end cadence and recovery acceptance criteria have not been demonstrated by the test suite.
- Fix a potentially serious lease failure path: `RunnerService` logs renewal exceptions but leaves execution running; recovery may claim the same work if the lease expires. Cancel the job after renewal cannot be restored before expiry and document the at-least-once contract.
- Ensure each deployment has a unique worker identity; a manually configured shared `WorkerId` can let one instance renew or complete another instance's run.

See [README.md](README.md) for architecture and current development instructions. The platform exists and runs, but the external publisher and release phase remain to be implemented.

## Keycloak authentication and live updates (22 November 2026)

**Done** (details in [README.md](README.md#authentication-keycloak)):

- Keycloak sign-in for the React dashboard (Authorization Code + PKCE, public client, in-memory tokens, shared-promise refresh, silent SSO on page refresh, visible failure/expiry/no-access states, dev sign-in compiled out of production builds).
- API validates Keycloak access tokens (issuer, signing keys, audience, expiry), maps realm or client roles explicitly to viewer/operator/administrator, rejects users without a monitoring role, keeps all endpoint permissions, allows dev tokens only in Development when explicitly enabled, and refuses to start in production without valid Keycloak settings.
- SignalR hub `/hubs/monitoring` (viewer-authorized, origin-restricted, query token only on the hub path) fed by a transactional outbox (`ChangeEvents`) written by the worker and API; API-hosted leader-only dispatcher with coalescing, replay after restart, worker-liveness diffing and retention cleanup. The worker never calls the API.
- React: one connection per session, fresh token per (re)connect, backoff, refetch on connect, query invalidation, 30 s polling only while disconnected.
- Local Keycloak realm (`docker compose --profile keycloak`), `scripts/verify-keycloak.mjs`.

**Still open:**

- Verify in a real browser: PKCE redirect, silent SSO, token refresh, logout against a real Keycloak (only unit-tested and API-side verified so far).
- Verify hub WebSockets through the real IIS / reverse proxy and that query strings are excluded from its logs.
- SignalR backplane (Redis / Azure SignalR) if more than one API instance must serve live updates.
- Token revocation on logout (back-channel logout or introspection) if 5-minute access tokens are not acceptable.
- External application publisher library (separate follow-up, not started).

## Active/standby worker mode (22 November 2026)

**Done** (details in [README.md](README.md#activestandby-workers)): optional (default on) leader election with one SQL Server `sp_getapplock` session lock on a dedicated non-pooled connection. A standby registers, schedules, recovers and executes nothing; the active node proves ownership every `Worker:LeaderCheckSeconds` and fails closed (cancels jobs, stops renewing leases, discards results) when it cannot; graceful stop releases the lock after the runner has stopped; crash or lost session hands over to a standby with back-off and no restart loop. Per-run claim and attempt fencing stay as the second layer. Windows Service deployment and failover steps are documented.

**Still open:**

- Failover is availability, not exactly-once; a job ignoring cancellation can overlap with its retry. No fencing token reaches job code.
- The dashboard does not show standby nodes (they register nothing by design); an explicit node/role view would need a schema change.
- No automatic fail-back to a preferred node; no multi-node load sharing in `ActiveStandby` mode.
- Failover timing was measured on a local Docker SQL Server only (about 5 s after a hard kill); verify on the real network and a Windows Service host, including the service recovery actions.
- The development bootstrap (create the database when `ApplyMigrationsOnStartup=true`) is not covered by an automated test.

## Nightly data retention (22 November 2026)

**Done** (details in [README.md](README.md#data-retention)): versioned stored procedure `dbo.usp_Monitoring_ApplyRetention_v1` (EF migration `RetentionProcedure`) with separate policies: job history 30 days (terminal runs by `EndedUtc`, attempts with them), audit entries 365 days, dispatched change-feed events 7 days. Queued/Running runs, pending change events, schedules, definitions, workers and capabilities are never deleted. Batched short transactions, counts per entity returned, idempotent. `RetentionService` runs only inside the active worker's tenure, re-proves leadership before every batch, takes its own `monitoring-retention` app lock, persists the last success in `MaintenanceState`, runs one catch-up after downtime, retries with backoff and cannot affect job execution. The schedule is computed in the worker from `RunAt` and `TimeZoneId` (DST rules documented). The old 5-minute change-feed purges in the worker and API (which deleted undispatched rows after 24 h) were removed.

**Still open:**

- Heartbeat rows of long-dead workers are not cleaned (by design, pending a policy).
- A manual procedure call does not record itself in `MaintenanceState`.
- The first cleanup on a very large history was only tested at small scale; watch the transaction log on the first run.
- No metric or alert on cleanup failure beyond logs.

## Worker nodes grouped by application (22 November 2026)

**Done** (details in [README.md](README.md#worker-nodes-by-application)): required `Worker:ApplicationName` persisted on every `WorkerHeartbeats` row next to the stable node name and the unique instance id (EF migration `WorkerApplicationName`; old rows backfilled as `Unassigned`). New `GET /api/v1/workers/applications` (four queries) reuses `WorkerJobs` as the service registry and returns, per application and node, registered services vs currently running services. The Overview panel groups by application, shows expandable node cards with the service list, keeps previous instances collapsed and labelled historical, and refreshes through SignalR. A stopped instance is marked `StoppedUtc` so restarts and failovers do not show two online nodes. Stale or offline nodes never report capabilities.

**Registered vs running:** registered = a `WorkerJobs` row (can execute, even when idle); running = a `Running` run on that node. Distinct counts de-duplicate a service registered by several online nodes.

**Still open:**

- A standby node is not listed (it registers nothing by design); showing standby nodes would need a registration row with a role.
- `ApplicationName` has a development default (`monitoring-local`); nothing forces production deployments to override it beyond documentation.
- Previous instances are limited to the last 24 hours and 300 rows.

