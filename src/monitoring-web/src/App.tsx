import { useState } from "react";
import { NavLink, Navigate, Route, Routes } from "react-router-dom";
import { useAuth } from "./auth";
import { useLiveConnection } from "./realtime/RealtimeProvider";
import { TzContext, type TzChoice } from "./format";
import JobDetail from "./pages/JobDetail";
import Jobs from "./pages/Jobs";
import Login from "./pages/Login";
import Overview from "./pages/Overview";
import RunDetail from "./pages/RunDetail";
import Runs from "./pages/Runs";

function readTz(): TzChoice {
  try { return localStorage.getItem("monitoring.tz") === "UTC" ? "UTC" : "local"; } catch { return "local"; }
}

export default function App() {
  const { status, session, logout } = useAuth();
  const live = useLiveConnection();
  const [tz, setTz] = useState<TzChoice>(readTz);
  const changeTz = (v: TzChoice) => { setTz(v); try { localStorage.setItem("monitoring.tz", v); } catch { /* storage unavailable */ } };

  if (status !== "authenticated" || !session) return <Login />;

  return (
    <TzContext.Provider value={tz}>
      <a href="#main" className="skip">Skip to content</a>
      <header className="topbar">
        <div className="topbar-inner">
          <strong className="brand">Job Monitoring</strong>
          <nav aria-label="Main">
            <NavLink to="/" end>Overview</NavLink>
            <NavLink to="/jobs">Jobs</NavLink>
            <NavLink to="/runs">Runs</NavLink>
          </nav>
          <div className="topbar-actions">
            <label className="tz">Times
              <select value={tz} onChange={(e) => changeTz(e.target.value as TzChoice)}>
                <option value="local">Local</option><option value="UTC">UTC</option>
              </select>
            </label>
            <span className={`live ${live ? "on" : "off"}`} role="status" title={live ? "Live updates connected" : "Live updates offline: refreshing every 30 seconds"}>
              {live ? "Live" : "Polling"}
            </span>
            <span className="who">{session.name} <span className="badge neutral">{session.role}</span></span>
            <button className="btn small" onClick={logout}>Sign out</button>
          </div>
        </div>
      </header>
      <main id="main">
        <Routes>
          <Route path="/" element={<Overview />} />
          <Route path="/jobs" element={<Jobs />} />
          <Route path="/jobs/:key" element={<JobDetail />} />
          <Route path="/runs" element={<Runs />} />
          <Route path="/runs/:id" element={<RunDetail />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </TzContext.Provider>
  );
}
