import { useState, type FormEvent } from "react";
import { api } from "../api";
import { useAuth } from "../auth";

const DEV_LOGIN = import.meta.env.VITE_DEV_LOGIN === "true";

export default function Login() {
  const { login } = useAuth();
  const [username, setUsername] = useState("dev");
  const [role, setRole] = useState("viewer");
  const [pasted, setPasted] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const devLogin = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setError(null);
    try {
      const { token } = await api.devToken(username, role);
      if (!login(token)) setError("The API returned a token this app cannot read.");
    } catch (err) { setError(err instanceof Error ? err.message : "Sign-in failed."); }
    finally { setBusy(false); }
  };

  const tokenLogin = (e: FormEvent) => {
    e.preventDefault();
    setError(login(pasted) ? null : "That token is invalid, expired, or has no supported role.");
  };

  return (
    <main className="login">
      <h1>Job Monitoring</h1>
      <p className="muted">Sign in to view and manage background jobs.</p>
      {error && <div role="alert" className="banner bad">{error}</div>}

      {DEV_LOGIN && (
        <form onSubmit={devLogin} aria-label="Development sign-in">
          <h2>Development sign-in</h2>
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
      )}

      <form onSubmit={tokenLogin} aria-label="Token sign-in">
        <h2>Bearer token</h2>
        <label>Paste a token issued by your identity provider
          <textarea rows={3} value={pasted} onChange={(e) => setPasted(e.target.value)} spellCheck={false} />
        </label>
        <button className="btn" disabled={!pasted.trim()}>Use token</button>
      </form>
    </main>
  );
}
