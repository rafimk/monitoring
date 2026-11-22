import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeBackend, realmClaims, renderWithAuth, RequireAuth } from "../test-utils";
import type { WorkerApplications, WorkerNode } from "../types";
import Overview from "./Overview";

const iso = (secondsAgo: number) => new Date(Date.now() - secondsAgo * 1000).toISOString();

const summary = {
  totalJobs: 6, enabledJobs: 6, unavailableJobs: 0, running: 1, queued: 0, overdue: 0, succeeded24h: 10, failed24h: 0, successRate24h: 100,
  workersOnline: 2, workersStale: 0, trend: [],
};

const svc = (jobKey: string, runningCount = 0, enabled = true) => ({ jobKey, displayName: `Name of ${jobKey}`, enabled, runningCount });
const node = (id: string, name: string, services: ReturnType<typeof svc>[], extra: Partial<WorkerNode> = {}): WorkerNode => ({
  workerId: id, workerName: name, host: "HOST1", state: "online", startedUtc: iso(600), lastSeenUtc: iso(3), ageSeconds: 3,
  registeredServiceCount: services.length, runningServices: services.filter((s) => s.runningCount > 0).length,
  runningRuns: services.reduce((n, s) => n + s.runningCount, 0), services, ...extra,
});

const billingServices = ["bill.invoice", "bill.reminder", "bill.export", "bill.sync", "bill.cleanup"].map((k, i) => svc(k, i === 0 ? 1 : 0));

const data: WorkerApplications = {
  generatedUtc: iso(0),
  applications: [
    {
      applicationName: "Billing", onlineNodes: 1, registeredServices: 5, runningServices: 1,
      nodes: [node("billing-node-aaaa1111", "billing-node", billingServices)],
      previousInstances: [
        { workerId: "billing-node-bbbb2222", workerName: "billing-node", host: "HOST1", state: "offline", lastSeenUtc: iso(7200), ageSeconds: 7200 },
        { workerId: "billing-node-cccc3333", workerName: "billing-node", host: "HOST1", state: "offline", lastSeenUtc: iso(9000), ageSeconds: 9000 },
      ],
    },
    {
      // Same configured worker name as Billing's node: the groups must not be merged.
      applicationName: "Reports", onlineNodes: 1, registeredServices: 2, runningServices: 0,
      nodes: [node("billing-node-dddd4444", "billing-node", [svc("rep.daily"), svc("rep.weekly", 0, false)])],
      previousInstances: [],
    },
  ],
};

let current: WorkerApplications = data;
let status = 200;

function mockFetch() {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/api/v1/workers/applications")) return status === 200 ? Response.json(current) : new Response(null, { status });
    if (url.includes("/api/v1/summary")) return Response.json(summary);
    if (url.includes("/api/v1/runs")) return Response.json({ items: [], total: 0, page: 1, pageSize: 5 });
    return new Response(null, { status: 204 });
  }));
}

function renderOverview() {
  return renderWithAuth(<RequireAuth><Overview /></RequireAuth>, fakeBackend({ claims: realmClaims(["monitoring-viewer"]) }));
}

beforeEach(() => { current = data; status = 200; mockFetch(); });
afterEach(() => vi.unstubAllGlobals());

describe("Worker nodes grouped by application", () => {
  it("groups nodes under their application with online-node and distinct-service totals", async () => {
    renderOverview();
    const billing = await screen.findByRole("region", { name: "Application Billing" });
    expect(within(billing).getByText(/1 node online · 5 distinct services · 1 running/)).toBeInTheDocument();
    const reports = screen.getByRole("region", { name: "Application Reports" });
    expect(within(reports).getByText(/1 node online · 2 distinct services · 0 running/)).toBeInTheDocument();
    expect(within(billing).getAllByRole("button", { name: /billing-node/ })).toHaveLength(1);
    expect(within(reports).getAllByRole("button", { name: /billing-node/ })).toHaveLength(1);
  });

  it("says '5 services registered · 1 running', not '5 services running'", async () => {
    renderOverview();
    expect(await screen.findByText("5 services registered · 1 running")).toBeInTheDocument();
    expect(screen.queryByText(/5 services running/)).not.toBeInTheDocument();
    expect(screen.getByText("2 services registered · 0 running")).toBeInTheDocument();
  });

  it("expands a node (keyboard accessible) to show each service, its enabled state and activity", async () => {
    renderOverview();
    const billing = await screen.findByRole("region", { name: "Application Billing" });
    const toggle = within(billing).getByRole("button", { name: /billing-node/ });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(within(billing).queryByText("bill.invoice")).not.toBeInTheDocument();

    toggle.focus();
    await userEvent.keyboard("{Enter}");
    expect(toggle).toHaveAttribute("aria-expanded", "true");
    const list = within(billing).getByRole("list", { name: "Services on billing-node" });
    const items = within(list).getAllByRole("listitem");
    expect(items).toHaveLength(5);
    expect(within(items[0]).getByText("bill.invoice")).toBeInTheDocument();
    expect(within(items[0]).getByText("1 running")).toBeInTheDocument();
    expect(within(items[1]).getByText("idle")).toBeInTheDocument();

    await userEvent.keyboard(" ");
    expect(toggle).toHaveAttribute("aria-expanded", "false");
  });

  it("shows disabled services as disabled", async () => {
    renderOverview();
    const reports = await screen.findByRole("region", { name: "Application Reports" });
    await userEvent.click(within(reports).getByRole("button", { name: /billing-node/ }));
    expect(within(reports).getByText("disabled")).toBeInTheDocument();
    expect(within(reports).getByText("enabled")).toBeInTheDocument();
  });

  it("keeps previous instances collapsed and labels them as historical, not running workers", async () => {
    renderOverview();
    const billing = await screen.findByRole("region", { name: "Application Billing" });
    const summaryEl = within(billing).getByText(/2 previous instances \(historical\)/);
    expect(summaryEl.closest("details")!.open).toBe(false);
    expect(within(billing).getByText(/not running workers/)).toBeInTheDocument();
    expect(within(billing).getAllByRole("button", { name: /billing-node/ })).toHaveLength(1); // only the live node has a card
  });

  it("shows an empty state when no worker has ever reported", async () => {
    current = { generatedUtc: iso(0), applications: [] };
    renderOverview();
    expect(await screen.findByText("No workers have reported yet.")).toBeInTheDocument();
  });

  it("labels a stale node and does not claim its services are available", async () => {
    current = {
      generatedUtc: iso(0), applications: [{
        applicationName: "Billing", onlineNodes: 0, registeredServices: 0, runningServices: 0,
        nodes: [node("billing-node-eeee5555", "billing-node", [], { state: "stale", ageSeconds: 90 })], previousInstances: [],
      }],
    };
    renderOverview();
    expect(await screen.findByText("stale")).toBeInTheDocument();
    expect(screen.getByText(/Capabilities not counted while the heartbeat is stale/)).toBeInTheDocument();
    expect(screen.getByText(/0 nodes online · 0 distinct services/)).toBeInTheDocument();
  });

  it("refreshes when the live path invalidates the workers query", async () => {
    const { qc } = renderOverview();
    expect(await screen.findByText("5 services registered · 1 running")).toBeInTheDocument();

    const busier = node("billing-node-aaaa1111", "billing-node", billingServices.map((s, i) => ({ ...s, runningCount: i < 2 ? 1 : 0 })));
    current = { ...data, applications: [{ ...data.applications[0], runningServices: 2, nodes: [busier] }, data.applications[1]] };
    await act(async () => { await qc.invalidateQueries({ queryKey: ["workers"] }); }); // what RunChanged / WorkersChanged do
    await waitFor(() => expect(screen.getByText("5 services registered · 2 running")).toBeInTheDocument());
  });

  it("shows an error with retry when the endpoint fails", async () => {
    status = 500;
    renderOverview();
    expect(await screen.findByRole("button", { name: /retry/i })).toBeInTheDocument();
  });
});
