import { useCallback, useEffect, useState } from "react";
import { api, AuthError, type ExcludedPost, type ExclusionRow, type MatchType, type TestVerdict } from "../api";

function verdictText(v: TestVerdict): { text: string; tone: "ok" | "warn" | "no" } {
  if (v.isLead && v.needsReview) return { text: `Kept as a lead and flagged for review: ${v.reviewReason ?? ""}`, tone: "warn" };
  if (v.isLead) {
    const ignored = v.customMatches?.some((m) => m.negated);
    return { text: ignored ? "Accepted as a lead. An excluded word was ignored because it was negated in context." : "Accepted as a lead.", tone: "ok" };
  }
  if (v.exclusionSource === "custom") return { text: `Rejected by your rule "${v.matchedRule?.pattern}".`, tone: "no" };
  if (v.exclusionSource === "builtin") return { text: `Rejected by a built-in rule (${v.exclusionReason}).`, tone: "no" };
  return { text: `Not treated as a lead (${v.exclusionReason ?? "no clear request"}).`, tone: "no" };
}

function RuleRow({ rule, onSave, onDelete }: {
  rule: ExclusionRow; onSave: (r: ExclusionRow) => void; onDelete: (id: string) => void;
}) {
  const [draft, setDraft] = useState(rule);
  useEffect(() => setDraft(rule), [rule]);
  const dirty = draft.pattern !== rule.pattern || draft.matchType !== rule.matchType;

  return (
    <div className={`rule ${rule.isActive ? "" : "off"}`}>
      <input dir="auto" value={draft.pattern} maxLength={100} onChange={(e) => setDraft({ ...draft, pattern: e.target.value })} />
      <select value={draft.matchType} onChange={(e) => setDraft({ ...draft, matchType: e.target.value as MatchType })}>
        <option value="phrase">Exact phrase</option>
        <option value="keyword">Keyword(s)</option>
      </select>
      <label className="inline">
        <input type="checkbox" checked={rule.isActive} onChange={(e) => onSave({ ...rule, isActive: e.target.checked })} /> Active
      </label>
      {dirty && <button className="primary" onClick={() => onSave(draft)}>Save</button>}
      <button onClick={() => onDelete(rule.id)}>Delete</button>
    </div>
  );
}

export default function Exclusions({ profileId, onAuthLost }: { profileId: string; onAuthLost: () => void }) {
  const [rules, setRules] = useState<ExclusionRow[]>([]);
  const [excluded, setExcluded] = useState<ExcludedPost[]>([]);
  const [pattern, setPattern] = useState("");
  const [type, setType] = useState<MatchType>("phrase");
  const [sample, setSample] = useState("");
  const [verdict, setVerdict] = useState<TestVerdict | null>(null);
  const [msg, setMsg] = useState("");

  const fail = useCallback((e: unknown) => {
    if (e instanceof AuthError) onAuthLost(); else setMsg("Something went wrong. Check that the API is running.");
  }, [onAuthLost]);

  const load = useCallback(() => {
    Promise.all([api.exclusions(profileId), api.excluded()])
      .then(([r, x]) => { setRules(r); setExcluded(x); })
      .catch(fail);
  }, [profileId, fail]);
  useEffect(load, [load]);

  const add = async () => {
    if (!pattern.trim()) return;
    try { await api.addExclusion(profileId, pattern.trim(), type); setPattern(""); setMsg(""); load(); } catch (e) { fail(e); }
  };
  const save = async (r: ExclusionRow) => { try { await api.updateExclusion(profileId, r); load(); } catch (e) { fail(e); } };
  const remove = async (id: string) => { try { await api.deleteExclusion(profileId, id); load(); } catch (e) { fail(e); } };
  const test = async () => {
    if (!sample.trim()) return;
    try { setVerdict(await api.testExclusions(profileId, sample)); } catch (e) { fail(e); }
  };

  const v = verdict ? verdictText(verdict) : null;

  return (
    <div className="card form">
      <h3>4. Exclusion rules</h3>
      <p className="muted">
        Words or phrases that should reject a post (Arabic or English). They are checked before the built-in rules, and context
        counts: "not a job" does not reject a real request, and an excluded word inside a clear purchase request sends the lead
        to review instead of dropping it.
      </p>

      {rules.length === 0 && <p className="muted">No rules yet.</p>}
      {rules.map((r) => <RuleRow key={r.id} rule={r} onSave={save} onDelete={remove} />)}

      <div className="rule">
        <input dir="auto" placeholder="Add a word or phrase, e.g. full-time or دوام كامل" value={pattern} maxLength={100}
          onChange={(e) => setPattern(e.target.value)} onKeyDown={(e) => e.key === "Enter" && add()} />
        <select value={type} onChange={(e) => setType(e.target.value as MatchType)}>
          <option value="phrase">Exact phrase</option>
          <option value="keyword">Keyword(s)</option>
        </select>
        <button className="primary" onClick={add}>Add rule</button>
      </div>
      <p className="muted">Exact phrase: the words must appear together, in order. Keyword(s): every word must appear somewhere in the post.</p>

      <h3>Test a sample post</h3>
      <textarea dir="auto" rows={3} value={sample} placeholder="Paste a post to see what your rules would do with it"
        onChange={(e) => setSample(e.target.value)} />
      <div><button onClick={test}>Test</button></div>
      {v && <p className={`verdict ${v.tone}`}>{v.text}</p>}
      {msg && <p className="error">{msg}</p>}

      <details>
        <summary>Recently excluded posts ({excluded.length})</summary>
        {excluded.length === 0 && <p className="muted">Nothing excluded yet.</p>}
        {excluded.map((p) => (
          <div key={p.id} className="excluded">
            <p dir="auto">{p.content}</p>
            <span className="tag">{p.source === "custom" ? `Your rule: ${p.matchedPattern ?? ""}` : p.source === "builtin" ? `Built-in: ${p.reason}` : p.reason}</span>{" "}
            <a href={p.url} target="_blank" rel="noreferrer">Open post</a>
          </div>
        ))}
      </details>
    </div>
  );
}
