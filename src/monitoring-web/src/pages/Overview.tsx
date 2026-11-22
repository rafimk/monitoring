import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { api } from "../api";
import { Card, Empty, ErrorBox, Loading, StatusBadge, Time } from "../components";
import { POLL_MS } from "../config";
import { formatDuration } from "../format";
import type { Summary } from "../types";

function Trend({ data }: { data: Summary["trend"] }) {
  const max = Math.max(1, ...data.map((d) => d.succeeded + d.failed));
  const w = 12, gap = 4, h = 80;
  return (
    <figure className="trend">
      <svg role="img" aria-label="Runs per hour over the last 24 hours" viewBox={`0 0 ${data.length * (w + gap)} ${h}`} preserveAspectRatio="none">
        {data.map((d, i) => {
          const fh = (d.failed / max) * h, sh = (d.succeeded / max) * h;
          const x = i * (w + gap);
          return (
            <g key={d.hourUtc}>
              <title>{`${new Date(d.hourUtc).toLocaleString()}: ${d.succeeded} succeeded, ${d.failed} failed`}</title>
              <rect x={x} y={h - sh - fh} width={w} height={fh} className="bar-bad" />
              <rect x={x} y={h - sh} width={w} height={sh} className="bar-ok" />
            </g>
          );
        })}
      </svg>
      <figcaption className="muted small">Last 24 h · <span className="dot ok" /> succeeded <span className="dot bad" /> failed</figcaption>
    </figure>
  );
}

export default function Overview() {
  const summary = useQuery({ queryKey: ["summary"], queryFn: api.summary, refetchInterval: POLL_MS });
  const workers = useQuery({ queryKey: ["workers"], queryFn: api.workers, refetchInterval: POLL_MS });
  const failures = useQuery({
    queryKey: ["runs", "recent-failures"],
    queryFn: () => api.runs({ status: "Failed", page: 1, pageSize: 5 }),
    refetchInterval: POLL_MS,
  });

  if (summary.isLoading) return <Loading what="overview" />;
  if (summary.isError) return <ErrorBox error={summary.error} retry={() => summary.refetch()} />;
  const s = summary.data!;
  const noWorker = s.workersOnline === 0;

  return (
    <>
      <h1>Overview</h1>
      {noWorker && (
        <div role="alert" className="banner bad">
          ⚠ No worker has reported a heartbeat recently. Scheduled jobs are <strong>not</strong> running.
        </div>
      )}
      {!noWorker && s.workersStale > 0 && (
        <div role="status" className="banner warn">{s.workersStale} worker(s) are stale or offline.</div>
      )}
      {s.overdue > 0 && <div role="status" className="banner warn">{s.overdue} schedule(s) are overdue.</div>}

      <div className="cards">
        <Card label="Success rate (24 h)" value={s.successRate24h === null ? "—" : `${s.successRate24h}%`}
          tone={s.successRate24h === null ? undefined : s.successRate24h >= 95 ? "ok" : "bad"} hint={`${s.succeeded24h} ok · ${s.failed24h} failed`} />
        <Card label="Running now" value={s.running} />
        <Card label="Queued" value={s.queued} />
        <Card label="Overdue" value={s.overdue} tone={s.overdue ? "warn" : undefined} />
        <Card label="Jobs" value={s.totalJobs} hint={`${s.enabledJobs} enabled · ${s.unavailableJobs} unavailable`} />
        <Card label="Workers online" value={s.workersOnline} tone={noWorker ? "bad" : "ok"} />
      </div>

      <section><h2>Runs per hour</h2><Trend data={s.trend} /></section>

      <section>
        <h2>Workers</h2>
        {workers.isLoading ? <Loading what="workers" /> : workers.isError ? <ErrorBox error={workers.error} /> :
          workers.data!.length === 0 ? <Empty>No worker has registered yet.</Empty> : (
            <div className="table-wrap"><table>
              <thead><tr><th>Worker</th><th>State</th><th>Last seen</th><th>Running</th></tr></thead>
              <tbody>{workers.data!.map((w) => (
                <tr key={w.workerId}>
                  <td data-label="Worker">{w.workerId}</td>
                  <td data-label="State"><span className={`badge ${w.state === "online" ? "ok" : w.state === "stale" ? "warn" : "bad"}`}>{w.state}</span></td>
                  <td data-label="Last seen">{w.ageSeconds}s ago</td>
                  <td data-label="Running">{w.runningCount}</td>
                </tr>))}</tbody>
            </table></div>)}
      </section>

      <section>
        <h2>Recent failures <Link className="small" to="/runs?status=Failed">view all</Link></h2>
        {failures.isLoading ? <Loading what="failures" /> : failures.isError ? <ErrorBox error={failures.error} /> :
          failures.data!.items.length === 0 ? <Empty>No failed runs 🎉</Empty> : (
            <div className="table-wrap"><table>
              <thead><tr><th>Job</th><th>Status</th><th>When</th><th>Duration</th><th>Error</th></tr></thead>
              <tbody>{failures.data!.items.map((r) => (
                <tr key={r.id}>
                  <td data-label="Job"><Link to={`/runs/${r.id}`}>{r.jobKey}</Link></td>
                  <td data-label="Status"><StatusBadge status={r.status} /></td>
                  <td data-label="When"><Time iso={r.startedUtc ?? r.queuedUtc} /></td>
                  <td data-label="Duration">{formatDuration(r.durationMs)}</td>
                  <td data-label="Error" className="err">{r.error}</td>
                </tr>))}</tbody>
            </table></div>)}
      </section>
    </>
  );
}
