import { useCallback, useEffect, useState } from "react";
import { api, AuthError, type JobRow, type SourceRow } from "../api";

const NAMES: Record<string, string> = { x: "X (Twitter)" };
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : "—");

export default function Sources({ onAuthLost }: { onAuthLost: () => void }) {
  const [sources, setSources] = useState<SourceRow[]>([]);
  const [jobs, setJobs] = useState<JobRow[]>([]);
  const [note, setNote] = useState<Record<string, string>>({});
  const [error, setError] = useState("");

  const load = useCallback(() => {
    Promise.all([api.sources(), api.jobs()])
      .then(([s, j]) => { setSources(s); setJobs(j); setError(""); })
      .catch((e) => { if (e instanceof AuthError) onAuthLost(); else setError("Could not load sources."); });
  }, [onAuthLost]);
  useEffect(load, [load]);

  const toggle = async (id: string) => { await api.toggleSource(id).catch(() => setError("Toggle failed.")); load(); };
  const test = async (id: string) => {
    setNote((n) => ({ ...n, [id]: "Testing…" }));
    try {
      const r = await api.testSource(id);
      setNote((n) => ({ ...n, [id]: r.ok ? `Connected (${r.sample} sample posts)` : `Not working: ${r.error}` }));
    } catch (e) {
      if (e instanceof AuthError) onAuthLost();
      else setNote((n) => ({ ...n, [id]: "No connector exists for this source yet." }));
    }
  };

  const nameOf = (id: string) => NAMES[sources.find((s) => s.id === id)?.sourceType ?? ""] ?? "Source";

  return (
    <>
      {error && <p className="error">{error}</p>}
      <div className="card">
        <h3>Sources</h3>
        <p className="muted">Only official APIs are used. X search needs a paid API tier and a bearer token set on the server.</p>
        {sources.map((s) => (
          <div key={s.id} className="source">
            <div>
              <strong>{NAMES[s.sourceType] ?? s.sourceType}</strong>{" "}
              <span className={`badge ${s.status === "enabled" ? "high" : "low"}`}>{s.status}</span>
              {s.lastError && <div className="error">{s.lastError}</div>}
              {note[s.id] && <div className="muted">{note[s.id]}</div>}

            </div>
            <div className="statuses">
              <button onClick={() => test(s.id)}>Test</button>
              <button className={s.status === "enabled" ? "" : "primary"} onClick={() => toggle(s.id)}>
                {s.status === "enabled" ? "Turn off" : "Turn on"}
              </button>
            </div>
          </div>
        ))}
      </div>

      <div className="card">
        <h3>Recent collection runs</h3>
        {jobs.length === 0 ? <p className="muted">No runs yet.</p> : (
          <table>
            <thead><tr><th>Source</th><th>Status</th><th>New posts</th><th>Started</th><th>Error</th></tr></thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.id}>
                  <td>{nameOf(j.sourceId)}</td><td>{j.status}</td><td>{j.fetched}</td>
                  <td>{when(j.startedAt)}</td><td className="error">{j.error ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}
