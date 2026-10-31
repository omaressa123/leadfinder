import { useEffect, useState } from "react";
import { api, AuthError, type Overview } from "../api";
import { SERVICE_LABELS } from "../presets";

function Breakdown({ title, rows, labelOf }: {
  title: string; rows: { key: string | null; count: number }[]; labelOf?: (k: string) => string;
}) {
  const max = Math.max(1, ...rows.map((r) => r.count));
  return (
    <div className="card">
      <h3>{title}</h3>
      {rows.length === 0 && <p className="muted">No data yet.</p>}
      {[...rows].sort((a, b) => b.count - a.count).map((r) => (
        <div key={r.key ?? "none"} className="factor">
          <span>{r.key ? (labelOf ? labelOf(r.key) : r.key) : "Unknown"}</span>
          <span>{r.count}</span>
          <div className="bar"><div style={{ width: `${(r.count / max) * 100}%` }} /></div>
        </div>
      ))}
    </div>
  );
}

export default function Reports({ onAuthLost }: { onAuthLost: () => void }) {
  const [o, setO] = useState<Overview | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    api.overview().then(setO).catch((e) => {
      if (e instanceof AuthError) onAuthLost(); else setError("Could not load the report.");
    });
  }, [onAuthLost]);

  if (error) return <p className="error">{error}</p>;
  if (!o) return <p className="muted">Loading…</p>;

  const converted = o.byStatus.find((s) => s.key === "converted")?.count ?? 0;
  return (
    <>
      <div className="card"><h3>{o.total} leads</h3><p className="muted">{converted} converted · {o.needsReview} need review</p></div>
      <div className="grid2">
        <Breakdown title="By priority" rows={o.byPriority} />
        <Breakdown title="By status" rows={o.byStatus} />
        <Breakdown title="By service" rows={o.byCategory} labelOf={(k) => SERVICE_LABELS[k] ?? k} />
        <Breakdown title="By country" rows={o.byCountry} />
      </div>
    </>
  );
}
