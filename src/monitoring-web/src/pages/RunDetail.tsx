import { useQuery } from "@tanstack/react-query";
import { Link, useParams } from "react-router-dom";
import { api } from "../api";
import { ErrorBox, Loading, StatusBadge, Time } from "../components";
import { POLL_MS } from "../config";
import { formatDuration } from "../format";

export default function RunDetail() {
  const { id = "" } = useParams();
  const q = useQuery({
    queryKey: ["run", id], queryFn: () => api.run(id),
    refetchInterval: (query) => (["Queued", "Running"].includes(query.state.data?.run.status ?? "") ? 3000 : POLL_MS * 3),
  });
  if (q.isLoading) return <Loading what="run" />;
  if (q.isError) return <ErrorBox error={q.error} retry={() => q.refetch()} />;
  const { run, attempts } = q.data!;

  return (
    <>
      <p className="small"><Link to={`/jobs/${encodeURIComponent(run.jobKey)}`}>← {run.jobKey}</Link></p>
      <h1>Run <StatusBadge status={run.status} /></h1>
      <dl className="facts">
        <div><dt>Trigger</dt><dd>{run.trigger}{run.requestedBy ? ` by ${run.requestedBy}` : ""}</dd></div>
        <div><dt>Scheduled for</dt><dd><Time iso={run.scheduledUtc} /></dd></div>
        <div><dt>Started</dt><dd><Time iso={run.startedUtc} /></dd></div>
        <div><dt>Ended</dt><dd><Time iso={run.endedUtc} /></dd></div>
        <div><dt>Duration</dt><dd>{formatDuration(run.durationMs)}</dd></div>
        <div><dt>Attempts</dt><dd>{run.attemptsUsed} of {run.maxAttempts} used · {attempts.length} started</dd></div>
        <div><dt>Worker</dt><dd>{run.workerId ?? "—"}</dd></div>
        <div><dt>Correlation ID</dt><dd className="mono">{run.correlationId}</dd></div>
      </dl>
      {run.error && <div role="alert" className="banner bad"><strong>Error: </strong>{run.error}</div>}

      <h2>Attempts</h2>
      <div className="table-wrap"><table>
        <thead><tr><th>#</th><th>Status</th><th>Started</th><th>Ended</th><th>Worker</th><th>Error</th></tr></thead>
        <tbody>{attempts.map((a) => (
          <tr key={a.attempt}>
            <td data-label="#">{a.attempt}</td><td data-label="Status"><StatusBadge status={a.status} /></td>
            <td data-label="Started"><Time iso={a.startedUtc} /></td><td data-label="Ended"><Time iso={a.endedUtc} /></td>
            <td data-label="Worker" className="small">{a.workerId}</td><td data-label="Error" className="err">{a.error}</td>
          </tr>))}</tbody>
      </table></div>
    </>
  );
}
