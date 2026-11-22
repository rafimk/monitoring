import { useQuery, keepPreviousData } from "@tanstack/react-query";
import { useSearchParams } from "react-router-dom";
import { api } from "../api";
import { ErrorBox, Loading } from "../components";
import { useFallbackPoll } from "../realtime/RealtimeProvider";
import type { RunStatus, TriggerSource } from "../types";
import { Pager, RunsTable, STATUSES } from "./RunsTable";

export default function Runs() {
  const [params, setParams] = useSearchParams();
  const jobKey = params.get("jobKey") ?? "";
  const status = (params.get("status") ?? "") as RunStatus | "";
  const trigger = (params.get("trigger") ?? "") as TriggerSource | "";
  const page = Math.max(1, Number(params.get("page") ?? 1) || 1);

  const poll = useFallbackPoll();
  const jobs = useQuery({ queryKey: ["jobs"], queryFn: api.jobs });
  const q = useQuery({
    queryKey: ["runs", { jobKey, status, trigger, page }],
    queryFn: () => api.runs({ jobKey, status, trigger, page, pageSize: 25 }),
    placeholderData: keepPreviousData,
    refetchInterval: poll,
  });

  const set = (k: string, v: string) => {
    const next = new URLSearchParams(params);
    if (v) next.set(k, v); else next.delete(k);
    if (k !== "page") next.delete("page"); // filter change resets paging
    setParams(next);
  };

  return (
    <>
      <h1>Run history</h1>
      <form className="filters" onSubmit={(e) => e.preventDefault()} aria-label="Run filters">
        <label>Job
          <select value={jobKey} onChange={(e) => set("jobKey", e.target.value)}>
            <option value="">All jobs</option>
            {jobs.data?.map((j) => <option key={j.key} value={j.key}>{j.displayName}</option>)}
          </select>
        </label>
        <label>Status
          <select value={status} onChange={(e) => set("status", e.target.value)}>
            <option value="">Any</option>
            {STATUSES.map((s) => <option key={s}>{s}</option>)}
          </select>
        </label>
        <label>Trigger
          <select value={trigger} onChange={(e) => set("trigger", e.target.value)}>
            <option value="">Any</option><option>Schedule</option><option>Manual</option><option>Api</option>
          </select>
        </label>
      </form>
      {q.isLoading ? <Loading what="runs" /> : q.isError ? <ErrorBox error={q.error} retry={() => q.refetch()} /> : (
        <>
          <RunsTable data={q.data!} />
          <Pager data={q.data!} onPage={(p) => set("page", String(p))} />
        </>
      )}
    </>
  );
}
