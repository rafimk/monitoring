import { useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { config } from "../config";

/** Everything shown to a person who is not (or no longer) signed in. */
export default function Login() {
  const { status, error, login, logout, retry, devLogin } = useAuth();

  if (status === "loading") return <main className="login"><p role="status">Signing you in…</p></main>;

  if (status === "forbidden") {
    return (
      <main className="login">
        <h1>No access</h1>
        <div role="alert" className="banner bad">
          You are signed in, but your account has no Job Monitoring role. Ask an administrator to grant you the viewer,
          operator or administrator role.
        </div>
        <button className="btn" onClick={logout}>Sign out</button>
      </main>
    );
  }

  return (
    <main className="login">
      <h1>Job Monitoring</h1>
      <p className="muted">Sign in to view and manage background jobs.</p>

      {status === "expired" && (
        <div role="alert" className="banner warn">Your session has expired. Please sign in again.</div>
      )}
      {status === "error" && (
        <div role="alert" className="banner bad">
          {error}
          {config.keycloak && <> <button className="btn small" onClick={retry}>Try again</button></>}
        </div>
      )}

      {config.keycloak && status !== "error" && (
        <button className="btn primary" onClick={login}>Sign in with Keycloak</button>
      )}

      {/* Development builds only: `import.meta.env.DEV` is false in production, so this whole form is removed. */}
      {import.meta.env.DEV && devLogin && <DevSignIn onSignIn={devLogin} />}
    </main>
  );
}

function DevSignIn({ onSignIn }: { onSignIn: (username: string, role: string) => Promise<void> }) {
  const [username, setUsername] = useState("dev");
  const [role, setRole] = useState("viewer");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setError(null);
    try { await onSignIn(username, role); }
    catch (err) { setError(err instanceof Error ? err.message : "Sign-in failed."); }
    finally { setBusy(false); }
  };

  return (
    <form onSubmit={submit} aria-label="Development sign-in">
      <h2>Development sign-in</h2>
      <p className="muted">Local testing only. Not part of production builds.</p>
      {error && <div role="alert" className="banner bad">{error}</div>}
      <label>Name<input value={username} onChange={(e) => setUsername(e.target.value)} required maxLength={100} /></label>
      <label>Role
        <select value={role} onChange={(e) => setRole(e.target.value)}>
          <option value="viewer">viewer — read only</option>
          <option value="operator">operator — run / enable / disable</option>
          <option value="administrator">administrator — everything</option>
        </select>
      </label>
      <button className="btn primary" disabled={busy}>{busy ? "Signing in…" : "Sign in"}</button>
    </form>
  );
}
