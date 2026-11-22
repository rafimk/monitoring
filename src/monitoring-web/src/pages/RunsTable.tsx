import { Link } from "react-router-dom";
import { Empty, StatusBadge, Time } from "../components";
import { formatDuration } from "../format";
import type { Paged, Run } from "../types";

export function RunsTable({ data, showJob = true }: { data: Paged<Run>; showJob?: boolean }) {
  if (data.items.length === 0) return <Empty>No runs match these filters.</Empty>;
  return (
    <div className="table-wrap"><table>
      <thead><tr>{showJob && <th>Job</th>}<th>Status</th><th>Trigger</th><th>Started</th><th>Duration</th><th>Attempt</th><th>Worker</th><th>Error</th></tr></thead>
      <tbody>{data.items.map((r) => (
        <tr key={r.id}>
          {showJob && <td data-label="Job"><Link to={`/runs/${r.id}`}>{r.jobKey}</Link></td>}
          <td data-label="Status">{showJob ? <StatusBadge status={r.status} /> : <Link to={`/runs/${r.id}`}><StatusBadge status={r.status} /></Link>}</td>
          <td data-label="Trigger">{r.trigger}{r.requestedBy ? ` (${r.requestedBy})` : ""}</td>
          <td data-label="Started"><Time iso={r.startedUtc ?? r.queuedUtc} /></td>
          <td data-label="Duration">{formatDuration(r.durationMs)}</td>
          <td data-label="Attempt">{r.attemptsUsed}/{r.maxAttempts}</td>
          <td data-label="Worker" className="small">{r.workerId ?? "—"}</td>
          <td data-label="Error" className="err">{r.error}</td>
        </tr>))}</tbody>
    </table></div>
  );
}

export function Pager({ data, onPage }: { data: Paged<Run>; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
  return (
    <nav className="pager" aria-label="Pagination">
      <button className="btn small" disabled={data.page <= 1} onClick={() => onPage(data.page - 1)}>← Prev</button>
      <span>Page {data.page} of {pages} · {data.total} runs</span>
      <button className="btn small" disabled={data.page >= pages} onClick={() => onPage(data.page + 1)}>Next →</button>
    </nav>
  );
}

export const STATUSES = ["Queued", "Running", "Succeeded", "Failed", "Cancelled", "TimedOut", "Abandoned"] as const;
