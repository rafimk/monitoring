import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { api } from "../api";
import { Card, Empty, ErrorBox, Loading, StatusBadge, Time } from "../components";
import { useFallbackPoll } from "../realtime/RealtimeProvider";
import { formatDuration } from "../format";
import { WorkerApplicationsPanel } from "./WorkerApplications";
import type { Summary } from "../types";

function Trend({ data }: { data: Summary["trend"] }) {
  const max = Math.max(1, ...data.map((d) => d.succeeded + d.failed));
  const width = 720, height = 190, left = 32, top = 12, plotHeight = 140;
  const slot = (width - left - 6) / Math.max(data.length, 1);
  const bar = Math.min(slot - 5, 17);
  const total = data.reduce((n, d) => n + d.succeeded + d.failed, 0);
  return (
    <figure className="trend">
      <svg role="img" aria-label="Successful and failed runs per hour over the last 24 hours" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none">
        {[0, .5, 1].map((fraction) => {
          const y = top + plotHeight * (1 - fraction);
          return <g key={fraction}><line className="trend-grid" x1={left} x2={width} y1={y} y2={y} /><text className="trend-label" x={0} y={y + 4}>{Math.round(max * fraction)}</text></g>;
        })}
        {data.map((d, i) => {
          const successHeight = d.succeeded / max * plotHeight;
          const failedHeight = d.failed / max * plotHeight;
          const x = left + i * slot + (slot - bar) / 2;
          return <g key={d.hourUtc}>
            <title>{`${new Date(d.hourUtc).toLocaleString()}: ${d.succeeded} succeeded, ${d.failed} failed`}</title>
            <rect x={x} y={top + plotHeight - successHeight} width={bar} height={successHeight} rx={2} className="bar-ok" />
            <rect x={x} y={top + plotHeight - successHeight - failedHeight} width={bar} height={failedHeight} rx={2} className="bar-bad" />
            {i % 4 === 0 && <text className="trend-label" x={x} y={height - 8}>{new Date(d.hourUtc).getHours().toString().padStart(2, "0")}:00</text>}
          </g>;
        })}
      </svg>
      <figcaption><span>{total} runs in 24 hours</span><span className="legend-item"><span className="dot ok" /> Succeeded</span><span className="legend-item"><span className="dot bad" /> Failed</span></figcaption>
    </figure>
  );
}

export default function Overview() {
  const poll = useFallbackPoll();
  const summary = useQuery({ queryKey: ["summary"], queryFn: api.summary, refetchInterval: poll });
  const workers = useQuery({ queryKey: ["workers", "applications"], queryFn: api.workerApplications, refetchInterval: poll });
  const failures = useQuery({ queryKey: ["runs", "recent-failures"], queryFn: () => api.runs({ status: "Failed", page: 1, pageSize: 5 }), refetchInterval: poll });

  if (summary.isLoading) return <Loading what="overview" />;
  if (summary.isError) return <ErrorBox error={summary.error} retry={() => summary.refetch()} />;
  const s = summary.data!;
  const noWorker = s.workersOnline === 0;
  const health = noWorker ? "No worker online" : s.overdue > 0 ? "Schedules need attention" : "Systems operational";

  return <>
    <div className="overview-head"><div><p className="eyebrow">Operations center</p><h1>Job overview</h1><p className="overview-subtitle">Your scheduled work, execution health, and worker capacity.</p></div><span className={`health-pill ${noWorker ? "bad" : s.overdue ? "warn" : ""}`} role="status"><span className={`dot ${noWorker ? "bad" : "ok"}`} />{health}</span></div>
    {noWorker && <div role="alert" className="banner bad">No worker has reported a recent heartbeat. Scheduled jobs are not running.</div>}
    {!noWorker && s.workersStale > 0 && <div role="status" className="banner warn">{s.workersStale} previous or stale worker instance(s). Active workers remain online; review the worker list for details.</div>}
    {s.overdue > 0 && <div role="status" className="banner warn">{s.overdue} schedule(s) are overdue.</div>}

    <div className="cards">
      <Card label="Success rate · 24h" value={s.successRate24h === null ? "—" : `${s.successRate24h}%`} tone={s.successRate24h === null ? undefined : s.successRate24h >= 95 ? "ok" : "bad"} hint={`${s.succeeded24h} succeeded · ${s.failed24h} failed`} />
      <Card label="Running now" value={s.running} hint="Active executions" />
      <Card label="Queued" value={s.queued} hint="Waiting for a worker" />
      <Card label="Overdue" value={s.overdue} tone={s.overdue ? "warn" : undefined} hint="Schedules past due" />
      <Card label="Jobs" value={s.totalJobs} hint={`${s.enabledJobs} enabled · ${s.unavailableJobs} unavailable`} />
      <Card label="Workers online" value={s.workersOnline} tone={noWorker ? "bad" : "ok"} hint="Reporting heartbeats" />
    </div>

    <div className="overview-grid">
      <section className="panel" aria-labelledby="trend-heading"><div className="section-head"><div><h2 id="trend-heading">Execution activity</h2><p className="panel-subtitle">Hourly outcomes over the last 24 hours</p></div><Link to="/runs">View runs →</Link></div><Trend data={s.trend} /></section>
      {workers.isLoading ? <section className="panel"><Loading what="workers" /></section> : workers.isError ? <section className="panel"><ErrorBox error={workers.error} retry={() => workers.refetch()} /></section> : <WorkerApplicationsPanel data={workers.data!} />}
    </div>

    <section aria-labelledby="failures-heading"><div className="section-head"><div><h2 id="failures-heading">Recent failures</h2><p className="panel-subtitle">The latest jobs that need attention</p></div><Link to="/runs?status=Failed">View all →</Link></div>
      {failures.isLoading ? <Loading what="failures" /> : failures.isError ? <ErrorBox error={failures.error} retry={() => failures.refetch()} /> : failures.data!.items.length === 0 ? <Empty>No failed runs in the current history.</Empty> : <div className="table-wrap"><table><thead><tr><th>Job</th><th>Status</th><th>When</th><th>Duration</th><th>Error</th></tr></thead><tbody>{failures.data!.items.map((r) => <tr key={r.id}><td data-label="Job"><Link to={`/runs/${r.id}`}>{r.jobKey}</Link></td><td data-label="Status"><StatusBadge status={r.status} /></td><td data-label="When"><Time iso={r.startedUtc ?? r.queuedUtc} /></td><td data-label="Duration">{formatDuration(r.durationMs)}</td><td data-label="Error" className="err">{r.error}</td></tr>)}</tbody></table></div>}
    </section>
  </>;
}
