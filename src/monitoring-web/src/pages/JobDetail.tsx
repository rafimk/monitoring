import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api } from "../api";
import { hasRole, useAuth } from "../auth";
import { ErrorBox, Loading, StatusBadge, Time } from "../components";
import { useFallbackPoll } from "../realtime/RealtimeProvider";
import { formatInterval } from "../format";
import type { RunStatus } from "../types";
import { Pager, RunsTable, STATUSES } from "./RunsTable";

export default function JobDetail() {
  const { key = "" } = useParams();
  const { session } = useAuth();
  const qc = useQueryClient();
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<RunStatus | "">("");
  const [notice, setNotice] = useState<{ kind: "ok" | "bad"; text: string } | null>(null);

  const poll = useFallbackPoll();
  const job = useQuery({ queryKey: ["job", key], queryFn: () => api.job(key), refetchInterval: poll });
  const runs = useQuery({
    queryKey: ["runs", "job", key, status, page],
    queryFn: () => api.runs({ jobKey: key, status, page, pageSize: 10 }),
    placeholderData: keepPreviousData, refetchInterval: poll,
  });

  const refresh = () => { void qc.invalidateQueries({ queryKey: ["job", key] }); void qc.invalidateQueries({ queryKey: ["runs"] }); void qc.invalidateQueries({ queryKey: ["jobs"] }); };
  const onError = (e: unknown) => setNotice({ kind: "bad", text: e instanceof Error ? e.message : "Request failed." });
  const runNow = useMutation({ mutationFn: () => api.runNow(key), onSuccess: () => { setNotice({ kind: "ok", text: "Run queued. A worker will pick it up shortly." }); refresh(); }, onError });
  const toggle = useMutation({
    mutationFn: (enable: boolean) => api.setEnabled(key, enable),
    onSuccess: (_d, enable) => { setNotice({ kind: "ok", text: enable ? "Job enabled." : "Job disabled." }); refresh(); }, onError,
  });

  if (job.isLoading) return <Loading what="job" />;
  if (job.isError) return <ErrorBox error={job.error} retry={() => job.refetch()} />;
  const j = job.data!;
  const canOperate = hasRole(session, "operator");

  return (
    <>
      <p className="small"><Link to="/jobs">← All jobs</Link></p>
      <h1>{j.displayName} <span className="muted small">{j.key}</span></h1>
      <p>{j.description}</p>

      {!j.isAvailable && <div role="alert" className="banner bad">No worker has this job's implementation registered. It cannot run.</div>}
      {notice && <div role={notice.kind === "bad" ? "alert" : "status"} className={`banner ${notice.kind}`}>{notice.text}</div>}

      <div className="actions">
        {canOperate ? (
          <>
            <button className="btn primary" disabled={runNow.isPending || !j.enabled || !j.isAvailable} onClick={() => runNow.mutate()}>
              {runNow.isPending ? "Queuing…" : "Run now"}
            </button>
            <button className="btn" disabled={toggle.isPending} onClick={() => toggle.mutate(!j.enabled)}>
              {j.enabled ? "Disable" : "Enable"}
            </button>
          </>
        ) : <span className="muted small">Read-only access (viewer). Operators can run and disable jobs.</span>}
      </div>

      <dl className="facts">
        <div><dt>State</dt><dd>{j.enabled ? "Enabled" : "Disabled"}{j.runningCount > 0 && ` · ${j.runningCount} running`}</dd></div>
        <div><dt>Schedule</dt><dd>{formatInterval(j.intervalSeconds)}</dd></div>
        <div><dt>Next run</dt><dd><Time iso={j.nextDueUtc} />{j.overdue && " (overdue)"}</dd></div>
        <div><dt>Last run</dt><dd>{j.lastRun ? <><StatusBadge status={j.lastRun.status} /> <Time iso={j.lastRun.startedUtc} /></> : "never"}</dd></div>
        <div><dt>Last success</dt><dd><Time iso={j.lastSuccessUtc} /></dd></div>
        <div><dt>Timeout</dt><dd>{j.timeoutSeconds}s</dd></div>
        <div><dt>Retries</dt><dd>up to {j.maxAttempts} attempts, {j.retryBaseDelaySeconds}s base backoff</dd></div>
        <div><dt>Overlap policy</dt><dd>{j.concurrencyPolicy}</dd></div>
        <div><dt>Missed runs</dt><dd>{j.missedRunPolicy}</dd></div>
      </dl>

      <h2>History</h2>
      <form className="filters" onSubmit={(e) => e.preventDefault()}>
        <label>Status
          <select value={status} onChange={(e) => { setStatus(e.target.value as RunStatus | ""); setPage(1); }}>
            <option value="">Any</option>{STATUSES.map((s) => <option key={s}>{s}</option>)}
          </select>
        </label>
      </form>
      {runs.isLoading ? <Loading what="runs" /> : runs.isError ? <ErrorBox error={runs.error} retry={() => runs.refetch()} /> : (
        <><RunsTable data={runs.data!} showJob={false} /><Pager data={runs.data!} onPage={setPage} /></>
      )}
    </>
  );
}
