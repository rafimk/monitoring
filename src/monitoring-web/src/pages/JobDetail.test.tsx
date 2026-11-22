import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { AuthProvider } from "../auth";
import JobDetail from "./JobDetail";

function jwt(role: string) {
  const b64 = (o: object) => btoa(JSON.stringify(o)).replace(/=+$/, "");
  return `${b64({ alg: "none" })}.${b64({ role, name: "tester", exp: Date.now() / 1000 + 3600 })}.sig`;
}

const job = {
  key: "sample.job", displayName: "Sample job", description: "demo", enabled: true, isAvailable: true,
  intervalSeconds: 60, nextDueUtc: new Date(Date.now() + 30_000).toISOString(), overdue: false, runningCount: 0,
  lastRun: null, lastSuccessUtc: null, timeoutSeconds: 60, maxAttempts: 3, retryBaseDelaySeconds: 10,
  concurrencyPolicy: "SkipIfRunning", missedRunPolicy: "CoalesceOne", timeZoneId: "UTC",
};
const emptyRuns = { items: [], total: 0, page: 1, pageSize: 10 };

let calls: { url: string; method: string }[] = [];

function mockFetch(overrides: Record<string, () => Response> = {}) {
  calls = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    calls.push({ url, method });
    const key = `${method} ${new URL(url).pathname}`;
    if (overrides[key]) return overrides[key]();
    if (url.includes("/api/v1/runs")) return Response.json(emptyRuns);
    if (url.includes("/api/v1/jobs/sample.job") && method === "GET") return Response.json(job);
    return new Response(null, { status: 204 });
  }));
}

function renderAs(role: string) {
  sessionStorage.setItem("monitoring.token", jwt(role));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <AuthProvider>
        <MemoryRouter initialEntries={["/jobs/sample.job"]}>
          <Routes><Route path="/jobs/:key" element={<JobDetail />} /></Routes>
        </MemoryRouter>
      </AuthProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => sessionStorage.clear());
afterEach(() => vi.unstubAllGlobals());

describe("JobDetail role gating", () => {
  it("hides controls for viewers and explains why", async () => {
    mockFetch();
    renderAs("viewer");
    expect(await screen.findByRole("heading", { name: /Sample job/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Run now" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Disable" })).not.toBeInTheDocument();
    expect(screen.getByText(/Read-only access/)).toBeInTheDocument();
  });

  it("lets operators run now and shows confirmation", async () => {
    mockFetch({ "POST /api/v1/jobs/sample.job/run": () => Response.json({ runId: "abc" }, { status: 202 }) });
    renderAs("operator");
    await userEvent.click(await screen.findByRole("button", { name: "Run now" }));
    expect(await screen.findByText(/Run queued/)).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST" && c.url.endsWith("/jobs/sample.job/run"))).toBe(true);
  });

  it("shows the server's problem detail when an action is rejected", async () => {
    mockFetch({
      "POST /api/v1/jobs/sample.job/run": () => Response.json({ detail: "A manual run for this job is already queued." }, { status: 409 }),
    });
    renderAs("operator");
    await userEvent.click(await screen.findByRole("button", { name: "Run now" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("already queued");
  });

  it("toggles enabled state through the API", async () => {
    mockFetch();
    renderAs("operator");
    await userEvent.click(await screen.findByRole("button", { name: "Disable" }));
    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/jobs/sample.job/disable"))).toBe(true));
  });

  it("disables Run now when the implementation is unavailable", async () => {
    mockFetch({ "GET /api/v1/jobs/sample.job": () => Response.json({ ...job, isAvailable: false }) });
    renderAs("administrator");
    expect(await screen.findByRole("button", { name: "Run now" })).toBeDisabled();
    expect(screen.getByRole("alert")).toHaveTextContent(/cannot run/);
  });
});
