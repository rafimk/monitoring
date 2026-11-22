import type { Attempt, JobDetail, JobSummary, Paged, Run, RunDetail, RunFilters, Summary, Worker, WorkerApplications } from "./types";

import { config } from "./config";
export const API_BASE: string = config.apiBaseUrl;

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

let tokenProvider: () => Promise<string | null> = async () => null;
let onUnauthorized: () => void = () => {};
export function configureApi(opts: { getToken: () => Promise<string | null>; onUnauthorized: () => void }) {
  tokenProvider = opts.getToken;
  onUnauthorized = opts.onUnauthorized;
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = await tokenProvider(); // refreshed first when close to expiry; concurrent requests share one refresh
  const res = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers: {
      Accept: "application/json",
      ...(init.body ? { "Content-Type": "application/json" } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
  });
  if (res.status === 401) onUnauthorized();
  if (!res.ok) {
    let detail = res.statusText;
    try {
      const p = await res.json();
      detail = p.detail ?? p.title ?? detail;
    } catch { /* non-JSON error body */ }
    throw new ApiError(res.status, res.status === 403 ? "You do not have permission to do that." : detail);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

const qs = (o: Record<string, string | number | undefined>) => {
  const p = new URLSearchParams();
  Object.entries(o).forEach(([k, v]) => { if (v !== undefined && v !== "") p.set(k, String(v)); });
  return p.toString();
};

export const api = {
  summary: () => request<Summary>("/api/v1/summary"),
  workers: () => request<Worker[]>("/api/v1/workers"),
  workerApplications: () => request<WorkerApplications>("/api/v1/workers/applications"),
  jobs: () => request<JobSummary[]>("/api/v1/jobs"),
  job: (key: string) => request<JobDetail>(`/api/v1/jobs/${encodeURIComponent(key)}`),
  runs: (f: RunFilters) => request<Paged<Run>>(`/api/v1/runs?${qs({ ...f })}`),
  run: (id: string) => request<RunDetail>(`/api/v1/runs/${id}`),
  runNow: (key: string) => request<{ runId: string }>(`/api/v1/jobs/${encodeURIComponent(key)}/run`, { method: "POST" }),
  setEnabled: (key: string, enabled: boolean) =>
    request<void>(`/api/v1/jobs/${encodeURIComponent(key)}/${enabled ? "enable" : "disable"}`, { method: "POST" }),
};

export type { Attempt };
