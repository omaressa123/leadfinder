import { useCallback, useEffect, useState } from "react";
import { api, AuthError, getToken, setToken, STATUSES, type LeadDetail, type LeadRow, type Priority } from "./api";
import Config from "./pages/Config";
import Reports from "./pages/Reports";
import Sources from "./pages/Sources";

const CATEGORIES = [
  "WEB_DEVELOPMENT", "ERP_DEVELOPMENT", "MOBILE_DEVELOPMENT", "ECOMMERCE", "CRM_DEVELOPMENT",
  "CUSTOM_SOFTWARE", "AI_DEVELOPMENT", "API_INTEGRATION", "DESKTOP_DEVELOPMENT", "MAINTENANCE",
];

const label = (s: string | null) => (s ? s.replace(/_/g, " ").toLowerCase().replace(/^\w/, (c) => c.toUpperCase()) : "—");
const place = (l: { country: string | null; city: string | null }) =>
  [l.city, l.country].filter(Boolean).join(", ") || "Location unknown";
const when = (iso: string) => new Date(iso).toLocaleString();

function PriorityBadge({ p, score }: { p: Priority; score: number }) {
  return <span className={`badge ${p}`}>{score} · {p}</span>;
}

function Login({ onDone }: { onDone: () => void }) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    try {
      const r = await api.login(email, password);
      setToken(r.token);
      onDone();
    } catch {
      setError("Incorrect email or password.");
    }
  };

  return (
    <div className="center">
      <form className="card login" onSubmit={submit}>
        <h1>Lead Inbox</h1>
        <label>Email<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required /></label>
        <label>Password<input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required /></label>
        {error && <p className="error">{error}</p>}
        <button className="primary" type="submit">Sign in</button>
      </form>
    </div>
  );
}

function Inbox({ onOpen, onAuthLost }: { onOpen: (id: string) => void; onAuthLost: () => void }) {
  const [rows, setRows] = useState<LeadRow[]>([]);
  const [total, setTotal] = useState(0);
  const [filters, setFilters] = useState({ status: "", priority: "", category: "", country: "", needsReview: "" });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    api.leads(filters)
      .then((r) => { if (!cancelled) { setRows(r.items); setTotal(r.total); setError(""); } })
      .catch((e) => { if (e instanceof AuthError) onAuthLost(); else if (!cancelled) setError("Could not load leads."); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [filters, onAuthLost]);

  const set = (k: keyof typeof filters) => (e: React.ChangeEvent<HTMLSelectElement>) =>
    setFilters((f) => ({ ...f, [k]: e.target.value }));

  return (
    <>
      <div className="filters card">
        <select value={filters.status} onChange={set("status")}>
          <option value="">All statuses</option>
          {STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}
        </select>
        <select value={filters.priority} onChange={set("priority")}>
          <option value="">All priorities</option>
          <option value="high">High</option><option value="medium">Medium</option><option value="low">Low</option>
        </select>
        <select value={filters.category} onChange={set("category")}>
          <option value="">All services</option>
          {CATEGORIES.map((c) => <option key={c} value={c}>{label(c)}</option>)}
        </select>
        <select value={filters.country} onChange={set("country")}>
          <option value="">All countries</option>
          {["Egypt", "Saudi Arabia", "UAE", "Kuwait", "Qatar", "Bahrain", "Oman"].map((c) => <option key={c}>{c}</option>)}
        </select>
        <select value={filters.needsReview} onChange={set("needsReview")}>
          <option value="">All leads</option>
          <option value="true">Needs review</option>
        </select>
        <span className="muted">{total} leads</span>
      </div>

      {error && <p className="error">{error}</p>}
      {loading && <p className="muted">Loading…</p>}
      {!loading && !error && rows.length === 0 && (
        <div className="card empty">No leads match. Enable a source and run a search profile to collect posts.</div>
      )}

      {rows.map((l) => (
        <button key={l.id} className="card row" onClick={() => onOpen(l.id)}>
          <div className="row-top">
            <PriorityBadge p={l.priority} score={l.score} />
            <span className="tag">{label(l.category)}</span>
            <span className="tag">{place(l)}</span>
            <span className="tag status">{label(l.status)}</span>
            {l.needsReview && <span className="tag review">Needs review</span>}
            <span className="muted right">{when(l.publishedAt)}</span>
          </div>
          <p className="content" dir="auto">{l.content}</p>
        </button>
      ))}
    </>
  );
}

function Details({ id, onBack, onAuthLost }: { id: string; onBack: () => void; onAuthLost: () => void }) {
  const [d, setD] = useState<LeadDetail | null>(null);
  const [note, setNote] = useState("");
  const [error, setError] = useState("");

  const load = useCallback(() => {
    api.lead(id).then(setD).catch((e) => { if (e instanceof AuthError) onAuthLost(); else setError("Could not load this lead."); });
  }, [id, onAuthLost]);
  useEffect(load, [load]);

  if (error) return <p className="error">{error}</p>;
  if (!d) return <p className="muted">Loading…</p>;

  const analysis = d.analysis ? JSON.parse(d.analysis) : null;
  const setStatus = async (s: string) => { await api.setStatus(id, s); load(); };
  const addNote = async () => { if (note.trim()) { await api.addNote(id, note.trim()); setNote(""); load(); } };

  return (
    <>
      <button className="link" onClick={onBack}>← Back to inbox</button>
      <div className="grid">
        <div>
          <div className="card">
            <div className="row-top">
              <PriorityBadge p={d.lead.priority} score={d.lead.score} />
              <span className="tag">{label(d.lead.category)}</span>
              <span className="tag">{place(d.lead)}</span>
            </div>
            {d.lead.needsReview && (
              <p className="notice">Needs your review: {d.lead.reviewReason}. Moving the lead to any other status clears this flag.</p>
            )}
            <p className="content full" dir="auto">{d.signal.content}</p>
            <p className="muted">Posted {when(d.signal.publishedAt)} · <a href={d.signal.url} target="_blank" rel="noreferrer">Open original post</a></p>
          </div>

          <div className="card">
            <h3>Status</h3>
            <p className="muted">You move this yourself after real contact; nothing is sent automatically.</p>
            <div className="statuses">
              {STATUSES.map((s) => (
                <button key={s} className={s === d.lead.status ? "primary" : ""} onClick={() => setStatus(s)}>{label(s)}</button>
              ))}
            </div>
          </div>

          <div className="card">
            <h3>Notes &amp; follow-ups</h3>
            <textarea value={note} onChange={(e) => setNote(e.target.value)} placeholder="What happened? e.g. sent a message on X, asked for details" />
            <button className="primary" onClick={addNote}>Add note</button>
            <ul className="timeline">
              {d.activities.map((a) => (
                <li key={a.id}><span className="muted">{when(a.createdAt)} · {a.activityType}</span><br />{a.notes}</li>
              ))}
            </ul>
          </div>
        </div>

        <div>
          <div className="card">
            <h3>Why this score</h3>
            {d.factors.map((f) => (
              <div key={f.id} className="factor">
                <span>{label(f.factor)}</span>
                <span>{f.score} / {f.weight}</span>
                <div className="bar"><div style={{ width: `${(f.score / f.weight) * 100}%` }} /></div>
              </div>
            ))}
          </div>
          {analysis && (
            <div className="card">
              <h3>AI analysis</h3>
              <dl>
                <dt>Intent</dt><dd>{label(analysis.intent_type)} ({analysis.intent_strength})</dd>
                <dt>Industry</dt><dd>{analysis.service_details.industry ?? "—"}</dd>
                <dt>Technology</dt><dd>{analysis.service_details.technology ?? "—"}</dd>
                <dt>Requirements</dt><dd>{analysis.service_details.requirements.join(", ") || "—"}</dd>
                <dt>Deadline</dt><dd>{analysis.service_details.deadline ?? "—"}</dd>
                <dt>Location</dt><dd>{analysis.geography.status}</dd>
                <dt>Evidence</dt><dd>{analysis.evidence.join(" · ") || "—"}</dd>
              </dl>
              <p className="muted">Model {d.modelVersion}. Scores reflect request clarity, not the buyer's budget or readiness.</p>
            </div>
          )}
        </div>
      </div>
    </>
  );
}

type Tab = "inbox" | "search" | "sources" | "reports";
const TABS: { id: Tab; text: string }[] = [
  { id: "inbox", text: "Lead Inbox" },
  { id: "search", text: "Search setup" },
  { id: "sources", text: "Data sources" },
  { id: "reports", text: "Reports" },
];

export default function App() {
  const [authed, setAuthed] = useState(!!getToken());
  const [tab, setTab] = useState<Tab>("inbox");
  const [openId, setOpenId] = useState<string | null>(null);
  const logout = useCallback(() => { setToken(null); setAuthed(false); setOpenId(null); setTab("inbox"); }, []);

  if (!authed) return <Login onDone={() => setAuthed(true)} />;

  const go = (t: Tab) => { setTab(t); setOpenId(null); };

  return (
    <div className="shell">
      <header className="desk-header">
        <div className="brand">
          <h1>Lead Inbox</h1>
          <span className="muted" style={{ fontSize: "0.75rem", letterSpacing: "0.5px", textTransform: "uppercase" }}>AI lead scoring &bull; Egypt &amp; Gulf</span>
        </div>
        <nav className="tabs">
          {TABS.map((t) => (
            <button key={t.id} className={tab === t.id ? "primary" : ""} onClick={() => go(t.id)}>{t.text}</button>
          ))}
        </nav>
        <button className="link" onClick={logout}>Sign out</button>
      </header>
      <main>
        {tab === "inbox" && (openId
          ? <Details id={openId} onBack={() => setOpenId(null)} onAuthLost={logout} />
          : <Inbox onOpen={setOpenId} onAuthLost={logout} />)}
        {tab === "search" && <Config onAuthLost={logout} />}
        {tab === "sources" && <Sources onAuthLost={logout} />}
        {tab === "reports" && <Reports onAuthLost={logout} />}
      </main>
    </div>
  );
}
