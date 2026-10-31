import { useCallback, useEffect, useState } from "react";
import { api, AuthError, type ProfileRow, type ServiceRow } from "../api";
import { COUNTRIES, PRESET_KEYWORDS, SERVICE_LABELS } from "../presets";
import Exclusions from "./Exclusions";
import Schedule from "./Schedule";

const lines = (s: string) => s.split("\n").map((x) => x.trim()).filter(Boolean);

export default function Config({ onAuthLost }: { onAuthLost: () => void }) {
  const [services, setServices] = useState<ServiceRow[]>([]);
  const [profile, setProfile] = useState<ProfileRow | null>(null);
  const [name, setName] = useState("Main search");
  const [countries, setCountries] = useState<string[]>(["Egypt", "Saudi Arabia"]);
  const [minScore, setMinScore] = useState(60);
  const [keywords, setKeywords] = useState("");
  const [msg, setMsg] = useState("");
  const [busy, setBusy] = useState(false);

  const fail = useCallback((e: unknown) => {
    if (e instanceof AuthError) onAuthLost();
    else setMsg("Something went wrong. Check that the API is running.");
  }, [onAuthLost]);

  useEffect(() => {
    Promise.all([api.services(), api.profiles()]).then(([s, p]) => {
      setServices(s);
      const first = p[0];
      if (first) {
        setProfile(first);
        setName(first.name);
        setCountries(first.countries);
        setMinScore(first.minScore);
        setKeywords(first.keywords.map((k) => k.keyword).join("\n"));
      }
    }).catch(fail);
  }, [fail]);

  const mine = new Set(services.map((s) => s.category));

  const toggleService = async (category: string) => {
    try {
      const existing = services.find((s) => s.category === category);
      if (existing) {
        await api.deleteService(existing.id);
        setServices((v) => v.filter((s) => s.id !== existing.id));
      } else {
        const created = await api.addService(SERVICE_LABELS[category], category);
        setServices((v) => [...v, created]);
      }
    } catch (e) { fail(e); }
  };

  const addSuggested = () => {
    const have = new Set(lines(keywords).map((k) => k.toLowerCase()));
    const add = [...mine].flatMap((c) => PRESET_KEYWORDS[c] ?? []).filter((k) => !have.has(k.toLowerCase()));
    if (add.length === 0) { setMsg("Pick at least one service first, or all suggestions are already added."); return; }
    setKeywords((k) => [...lines(k), ...add].join("\n"));
    setMsg(`Added ${add.length} suggested phrases. Review them, then save.`);
  };

  const save = async (): Promise<ProfileRow | null> => {
    try {
      const saved = await api.saveProfile(profile?.id ?? null, {
        name, countries, cities: [], languages: ["ar", "en"], minScore, keywords: lines(keywords),
      });
      const refreshed = (await api.profiles()).find((p) => p.id === saved.id) ?? saved;
      setProfile(refreshed);
      setMsg("Saved.");
      return refreshed;
    } catch (e) { fail(e); return null; }
  };

  const run = async () => {
    setBusy(true); setMsg("");
    try {
      const p = await save();
      if (!p) return;
      const results = await api.runProfile(p.id);
      if (results.length === 0) setMsg("No source is enabled. Turn one on in Data sources first.");
      else setMsg(results.map((r) =>
        r.status === "failed" ? `${r.sourceType}: failed (${r.error})`
          : r.status === "skipped" ? `${r.sourceType}: skipped (${r.error})`
            : `${r.sourceType}: ${r.newPosts} new posts, ${r.leads} leads` + (r.needsReview ? `, ${r.needsReview} to review` : "")).join(" · "));
    } catch (e) { fail(e); } finally { setBusy(false); }
  };

  return (
    <>
      <div className="card">
        <h3>1. Services you offer</h3>
        <p className="muted">Leads matching these score higher. With none selected, every service gets half credit.</p>
        <div className="statuses">
          {Object.entries(SERVICE_LABELS).map(([cat, text]) => (
            <button key={cat} className={mine.has(cat) ? "primary" : ""} onClick={() => toggleService(cat)}>{text}</button>
          ))}
        </div>
      </div>

      <div className="card form">
        <h3>2. Search profile</h3>
        <label>Name<input value={name} onChange={(e) => setName(e.target.value)} /></label>
        <div>
          <span className="muted">Target countries (a lead outside these gets no location points)</span>
          <div className="statuses">
            {COUNTRIES.map((c) => (
              <button key={c} className={countries.includes(c) ? "primary" : ""}
                onClick={() => setCountries((v) => v.includes(c) ? v.filter((x) => x !== c) : [...v, c])}>{c}</button>
            ))}
          </div>
        </div>
        <label>Minimum score to save as a lead: {minScore}
          <input type="range" min={30} max={90} step={5} value={minScore} onChange={(e) => setMinScore(Number(e.target.value))} />
        </label>
        <label>Search phrases (one per line)
          <textarea dir="auto" rows={8} value={keywords} onChange={(e) => setKeywords(e.target.value)} />
        </label>
        <button onClick={addSuggested}>Add suggested phrases for my services</button>
        <div className="statuses">
          <button onClick={save}>Save</button>
          <button className="primary" disabled={busy} onClick={run}>{busy ? "Searching…" : "Save and run search"}</button>
        </div>
        {msg && <p className="muted">{msg}</p>}
      </div>

      {profile && <Schedule profileId={profile.id} onAuthLost={onAuthLost} />}

      {profile
        ? <Exclusions profileId={profile.id} onAuthLost={onAuthLost} />
        : <div className="card"><h3>4. Exclusion rules</h3><p className="muted">Save the search profile once, then you can add exclusion rules here.</p></div>}
    </>
  );
}
