export type RunStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled" | "TimedOut" | "Abandoned";
export type TriggerSource = "Schedule" | "Manual" | "Api";
export type Role = "viewer" | "operator" | "administrator";

export interface LastRun { id: string; status: RunStatus; startedUtc: string | null; endedUtc: string | null; durationMs: number | null }

export interface JobSummary {
  key: string; displayName: string; description: string; enabled: boolean; isAvailable: boolean;
  intervalSeconds: number | null; nextDueUtc: string | null; overdue: boolean; runningCount: number; lastRun: LastRun | null;
}

export interface JobDetail extends Omit<JobSummary, "runningCount"> {
  timeoutSeconds: number; maxAttempts: number; retryBaseDelaySeconds: number;
  concurrencyPolicy: string; missedRunPolicy: string; timeZoneId: string;
  runningCount: number; lastSuccessUtc: string | null;
}

export interface Run {
  id: string; jobKey: string; trigger: TriggerSource; status: RunStatus; scheduledUtc: string; queuedUtc: string;
  startedUtc: string | null; endedUtc: string | null; durationMs: number | null;
  /** Sequence number of the latest attempt (includes attempts interrupted by a worker shutdown). */
  attempt: number;
  /** Attempts counted against maxAttempts. */
  attemptsUsed: number; maxAttempts: number;
  workerId: string | null; correlationId: string; requestedBy: string | null; error: string | null;
}

export interface Attempt { attempt: number; status: RunStatus; startedUtc: string; endedUtc: string | null; workerId: string; error: string | null }
export interface RunDetail { run: Run; attempts: Attempt[] }
export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number }

export interface Worker { workerId: string; host: string; startedUtc: string; lastSeenUtc: string; runningCount: number; ageSeconds: number; state: "online" | "stale" | "offline"; applicationName: string; workerName: string }

/** A registered service (job implementation) on one node. runningCount = runs executing right now. */
export interface NodeService { jobKey: string; displayName: string; enabled: boolean; runningCount: number }
export interface WorkerNode {
  workerId: string; workerName: string; host: string; state: "online" | "stale" | "offline"; startedUtc: string; lastSeenUtc: string; ageSeconds: number;
  registeredServiceCount: number; runningServices: number; runningRuns: number; services: NodeService[];
}
export interface PreviousInstance { workerId: string; workerName: string; host: string; state: string; lastSeenUtc: string; ageSeconds: number }
export interface ApplicationGroup {
  applicationName: string; onlineNodes: number; registeredServices: number; runningServices: number;
  nodes: WorkerNode[]; previousInstances: PreviousInstance[];
}
export interface WorkerApplications { generatedUtc: string; applications: ApplicationGroup[] }

export interface Summary {
  totalJobs: number; enabledJobs: number; unavailableJobs: number; running: number; queued: number; overdue: number;
  succeeded24h: number; failed24h: number; successRate24h: number | null;
  workersOnline: number; workersStale: number;
  trend: { hourUtc: string; succeeded: number; failed: number }[];
}

export interface RunFilters { jobKey?: string; status?: RunStatus | ""; trigger?: TriggerSource | ""; page: number; pageSize: number }
