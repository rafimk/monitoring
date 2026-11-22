import { useId, useState } from "react";
import { Empty, Time } from "../components";
import type { ApplicationGroup, WorkerApplications as Data, WorkerNode } from "../types";

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

/** "5 services registered · 1 running": registered is what the node can run, running is what executes right now. */
export function serviceSummary(registered: number, running: number) {
  return `${plural(registered, "service")} registered · ${running} running`;
}

function NodeCard({ node }: { node: WorkerNode }) {
  const [open, setOpen] = useState(false);
  const panelId = useId();
  const online = node.state === "online";
  return <li className="node-card">
    <button type="button" className="node-head" aria-expanded={open} aria-controls={panelId} onClick={() => setOpen(!open)}>
      <span className="node-main">
        <span className="node-caret" aria-hidden="true">{open ? "▾" : "▸"}</span>
        <span><span className="worker-id">{node.workerName}</span>
          <span className="worker-detail">{online ? serviceSummary(node.registeredServiceCount, node.runningServices) : "Capabilities not counted while the heartbeat is stale"}</span></span>
      </span>
      <span className="worker-right"><span className={`badge ${online ? "ok" : "warn"}`}>{node.state}</span>
        <span className="worker-detail">{node.ageSeconds}s ago</span></span>
    </button>
    {open && <div id={panelId} className="node-body">
      <p className="worker-detail">Instance <span className="mono">{node.workerId}</span> on {node.host} · started <Time iso={node.startedUtc} /> · last heartbeat <Time iso={node.lastSeenUtc} /></p>
      {node.services.length === 0 ? <Empty>{online ? "This node has not registered any services." : "No services are reported for a node that is not online."}</Empty> :
        <ul className="service-list" aria-label={`Services on ${node.workerName}`}>{node.services.map((s) => <li key={s.jobKey} className="service-item">
          <div><div className="worker-id">{s.displayName}</div><div className="worker-detail mono">{s.jobKey}</div></div>
          <div className="worker-right">
            <span className={`badge ${s.enabled ? "ok" : "warn"}`}>{s.enabled ? "enabled" : "disabled"}</span>
            <span className="worker-detail">{s.runningCount > 0 ? `${s.runningCount} running` : "idle"}</span>
          </div>
        </li>)}</ul>}
    </div>}
  </li>;
}

function Application({ app }: { app: ApplicationGroup }) {
  return <section className="app-group" aria-label={`Application ${app.applicationName}`}>
    <div className="app-head">
      <h3>{app.applicationName}</h3>
      <span className="worker-detail">{plural(app.onlineNodes, "node")} online · {plural(app.registeredServices, "distinct service")} · {app.runningServices} running</span>
    </div>
    {app.nodes.length === 0 ? <p className="worker-detail">No node of this application is reporting.</p> :
      <ul className="worker-list">{app.nodes.map((n) => <NodeCard key={n.workerId} node={n} />)}</ul>}
    {app.previousInstances.length > 0 && <details className="worker-archive">
      <summary>{plural(app.previousInstances.length, "previous instance")} (historical)</summary>
      <p className="worker-detail">Restarts create new instances. These are not running workers.</p>
      <div className="table-wrap"><table><thead><tr><th>Worker</th><th>Instance</th><th>Last seen</th></tr></thead><tbody>
        {app.previousInstances.map((p) => <tr key={p.workerId}><td data-label="Worker">{p.workerName}</td><td data-label="Instance" className="mono">{p.workerId}</td><td data-label="Last seen"><Time iso={p.lastSeenUtc} /></td></tr>)}
      </tbody></table></div>
    </details>}
  </section>;
}

export function WorkerApplicationsPanel({ data }: { data: Data }) {
  const onlineNodes = data.applications.reduce((n, a) => n + a.onlineNodes, 0);
  return <section className="panel" aria-labelledby="worker-heading">
    <div className="section-head"><div><h2 id="worker-heading">Worker nodes</h2>
      <p className="panel-subtitle">Grouped by application. Only the active node reports; a standby registers nothing.</p></div>
      <span className="badge info">{onlineNodes} online</span></div>
    {data.applications.length === 0 ? <Empty>No workers have reported yet.</Empty> : data.applications.map((a) => <Application key={a.applicationName} app={a} />)}
  </section>;
}
