import { useCallback, useEffect, useState } from "react";
import { api, AuthError, type ScheduleMode, type ScheduleView } from "../api";

const ZONES = [
  "Africa/Cairo", "Asia/Riyadh", "Asia/Dubai", "Asia/Kuwait", "Asia/Qatar", "Asia/Bahrain", "Asia/Muscat",
  "Asia/Amman", "Asia/Beirut", "Africa/Casablanca", "Europe/London", "Europe/Berlin", "America/New_York", "UTC",
];

type Preset = "off" | "6h" | "daily" | "custom";

const presetOf = (s: ScheduleView): Preset =>
  s.mode === "off" ? "off" : s.mode === "daily" ? "daily" : s.intervalMinutes === 360 ? "6h" : "custom";

const fmt = (iso: string | null, tz: string) =>
  iso ? new Date(iso).toLocaleString([], { timeZone: tz, dateStyle: "medium", timeStyle: "short" }) : "—";

export default function Schedule({ profileId, onAuthLost }: { profileId: string; onAuthLost: () => void }) {
  const [view, setView] = useState<ScheduleView | null>(null);
  const [preset, setPreset] = useState<Preset>("off");
  const [hours, setHours] = useState(12);
  const [dailyTime, setDailyTime] = useState("09:00");
  const [zone, setZone] = useState("Africa/Cairo");
  const [msg, setMsg] = useState("");

  const apply = useCallback((v: ScheduleView) => {
    setView(v);
    setPreset(presetOf(v));
    setDailyTime(v.dailyTime);
    setZone(v.timeZone === "UTC" && v.mode === "off" ? "Africa/Cairo" : v.timeZone);
    if (v.mode === "interval" && v.intervalMinutes !== 360) setHours(Math.max(1, Math.round(v.intervalMinutes / 60)));
  }, []);

  const fail = useCallback((e: unknown) => {
    if (e instanceof AuthError) onAuthLost(); else setMsg(e instanceof Error ? e.message : "Something went wrong");
  }, [onAuthLost]);

  const load = useCallback(() => api.schedule(profileId).then(apply).catch(fail), [profileId, apply, fail]);
  useEffect(() => { void load(); const t = setInterval(() => void load(), 30000); return () => clearInterval(t); }, [load]);

  async function save() {
    setMsg("");
    const mode: ScheduleMode = preset === "off" ? "off" : preset === "daily" ? "daily" : "interval";
    const intervalMinutes = preset === "6h" ? 360 : preset === "custom" ? Math.round(hours * 60) : 360;
    try {
      apply(await api.saveSchedule(profileId, { mode, intervalMinutes, dailyTime, timeZone: zone }));
      setMsg("Schedule saved.");
    } catch (e) { fail(e); }
  }

  async function toggle() {
    if (!view) return;
    setMsg("");
    try { apply(view.paused ? await api.resumeSchedule(profileId) : await api.pauseSchedule(profileId)); } catch (e) { fail(e); }
  }

  if (!view) return <div className="card"><h3>3. Automatic schedule</h3><p className="muted">Loading…</p></div>;
  const tz = view.timeZone;

  return (
    <div className="card form">
      <h3>3. Automatic schedule</h3>
      <p className="muted">Run this search by itself, so you do not have to press Run. Saving never starts a run immediately.</p>
      <div className="statuses">
        {([["off", "Off"], ["6h", "Every 6 hours"], ["daily", "Daily"], ["custom", "Custom"]] as [Preset, string][]).map(([k, label]) => (
          <button key={k} className={preset === k ? "primary" : ""} onClick={() => setPreset(k)}>{label}</button>
        ))}
      </div>
      {preset === "custom" && (
        <label>Every how many hours? (0.25 to 168)
          <input type="number" min={0.25} max={168} step={0.25} value={hours} onChange={(e) => setHours(Number(e.target.value))} />
        </label>
      )}
      {preset === "daily" && (
        <label>Time of day<input type="time" value={dailyTime} onChange={(e) => setDailyTime(e.target.value)} /></label>
      )}
      {preset !== "off" && (
        <label>Time zone
          <select value={zone} onChange={(e) => setZone(e.target.value)}>
            {ZONES.includes(zone) ? null : <option>{zone}</option>}
            {ZONES.map((z) => <option key={z}>{z}</option>)}
          </select>
        </label>
      )}
      <div className="statuses">
        <button className="primary" onClick={save}>Save schedule</button>
        {view.mode !== "off" && <button onClick={toggle}>{view.paused ? "Resume" : "Pause"}</button>}
      </div>
      {msg && <p className="muted">{msg}</p>}

      <table>
        <tbody>
          <tr><td>Status</td><td>{view.mode === "off" ? "Not scheduled" : view.paused ? "Paused" : view.running ? "Running now" : "Active"}</td></tr>
          <tr><td>Last run</td><td>{fmt(view.lastRunAt, tz)}{view.lastRunStatus ? ` (${view.lastRunStatus})` : ""}</td></tr>
          <tr><td>Next run</td><td>{view.mode === "off" || view.paused ? "—" : fmt(view.nextRunAt, tz)}{view.retryAttempts > 0 ? ` (retry ${view.retryAttempts} after a failure)` : ""}</td></tr>
        </tbody>
      </table>
      {view.lastRunError && <p className="error">Last error: {view.lastRunError}</p>}
    </div>
  );
}
