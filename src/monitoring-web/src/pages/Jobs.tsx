import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { api } from "../api";
import { Empty, ErrorBox, Loading, StatusBadge, Time } from "../components";
import { useFallbackPoll } from "../realtime/RealtimeProvider";
import { formatDuration, formatInterval, relative } from "../format";

export default function Jobs() {
  const poll = useFallbackPoll();
  const q = useQuery({ queryKey: ["jobs"], queryFn: api.jobs, refetchInterval: poll });
  if (q.isLoading) return <Loading what="jobs" />;
  if (q.isError) return <ErrorBox error={q.error} retry={() => q.refetch()} />;
  const jobs = q.data!;

  return (
    <>
      <h1>Jobs</h1>
      {jobs.length === 0 ? <Empty>No jobs registered yet. Start a worker that registers job implementations.</Empty> : (
        <div className="table-wrap"><table>
          <thead><tr><th>Job</th><th>Schedule</th><th>Next run</th><th>Last run</th><th>State</th></tr></thead>
          <tbody>{jobs.map((j) => (
            <tr key={j.key}>
              <td data-label="Job"><Link to={`/jobs/${encodeURIComponent(j.key)}`}><strong>{j.displayName}</strong></Link><div className="muted small">{j.key}</div></td>
              <td data-label="Schedule">{formatInterval(j.intervalSeconds)}</td>
              <td data-label="Next run">{j.nextDueUtc ? <>{relative(j.nextDueUtc)} {j.overdue && <span className="badge warn">overdue</span>}</> : "—"}</td>
              <td data-label="Last run">{j.lastRun ? <><StatusBadge status={j.lastRun.status} /> <span className="muted small"><Time iso={j.lastRun.startedUtc} /> · {formatDuration(j.lastRun.durationMs)}</span></> : "never"}</td>
              <td data-label="State">
                {!j.isAvailable ? <span className="badge bad">unavailable</span> : j.enabled ? <span className="badge ok">enabled</span> : <span className="badge neutral">disabled</span>}
                {j.runningCount > 0 && <span className="badge info">{j.runningCount} running</span>}
              </td>
            </tr>))}</tbody>
        </table></div>)}
    </>
  );
}
