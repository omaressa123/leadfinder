export type Priority = "high" | "medium" | "low";

export interface LeadRow {
  id: string;
  score: number;
  priority: Priority;
  status: string;
  category: string | null;
  country: string | null;
  city: string | null;
  content: string;
  url: string;
  publishedAt: string;
  needsReview: boolean;
  reviewReason: string | null;
}

export interface LeadList {
  total: number;
  items: LeadRow[];
}

export interface LeadDetail {
  lead: { id: string; score: number; priority: Priority; status: string; category: string | null; country: string | null; city: string | null; needsReview: boolean; reviewReason: string | null };
  signal: { content: string; url: string; publishedAt: string };
  analysis: string | null;
  modelVersion: string | null;
  factors: { id: string; factor: string; score: number; weight: number }[];
  activities: { id: string; activityType: string; notes: string | null; createdAt: string }[];
}

export type ScheduleMode = "off" | "interval" | "daily";
export interface ScheduleInput { mode: ScheduleMode; intervalMinutes: number; dailyTime: string; timeZone: string }
export interface ScheduleView extends ScheduleInput {
  paused: boolean; nextRunAt: string | null; lastRunAt: string | null; lastRunStatus: string | null;
  lastRunError: string | null; running: boolean; retryAttempts: number;
}

export const STATUSES = ["new", "reviewed", "contacted", "responded", "negotiation", "converted", "rejected"] as const;

const TOKEN_KEY = "leadgen_token";
export const getToken = () => localStorage.getItem(TOKEN_KEY);
export const setToken = (t: string | null) =>
  t ? localStorage.setItem(TOKEN_KEY, t) : localStorage.removeItem(TOKEN_KEY);

export class AuthError extends Error {}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = getToken();
  const res = await fetch(path, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init.headers,
    },
  });
  if (res.status === 401) throw new AuthError("Unauthorized");
  if (!res.ok) {
    let detail = "";
    try { detail = ((await res.json()) as { error?: string }).error ?? ""; } catch { /* no body */ }
    throw new Error(detail || `Request failed (${res.status})`);
  }
  return res.json() as Promise<T>;
}

export const api = {
  login: (email: string, password: string) =>
    request<{ token: string; name: string }>("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ email, password }),
    }),
  leads: (params: Record<string, string>) => {
    const qs = new URLSearchParams(Object.entries(params).filter(([, v]) => v)).toString();
    return request<LeadList>(`/api/leads?${qs}`);
  },
  lead: (id: string) => request<LeadDetail>(`/api/leads/${id}`),
  setStatus: (id: string, status: string) =>
    request(`/api/leads/${id}/status`, { method: "PATCH", body: JSON.stringify({ status }) }),
  addNote: (id: string, notes: string) =>
    request(`/api/leads/${id}/activities`, {
      method: "POST",
      body: JSON.stringify({ activityType: "note", notes }),
    }),

  services: () => request<ServiceRow[]>("/api/services"),
  addService: (name: string, category: string) =>
    request<ServiceRow>("/api/services", { method: "POST", body: JSON.stringify({ name, category }) }),
  deleteService: (id: string) => request<{ ok: boolean }>(`/api/services/${id}`, { method: "DELETE" }),

  profiles: () => request<ProfileRow[]>("/api/search-profiles"),
  saveProfile: (id: string | null, body: ProfileInput) =>
    request<ProfileRow>(id ? `/api/search-profiles/${id}` : "/api/search-profiles", {
      method: id ? "PUT" : "POST",
      body: JSON.stringify(body),
    }),
  runProfile: (id: string) =>
    request<RunResult[]>(`/api/search-profiles/${id}/run`, { method: "POST" }),

  schedule: (id: string) => request<ScheduleView>(`/api/search-profiles/${id}/schedule`),
  saveSchedule: (id: string, body: ScheduleInput) =>
    request<ScheduleView>(`/api/search-profiles/${id}/schedule`, { method: "PUT", body: JSON.stringify(body) }),
  pauseSchedule: (id: string) => request<ScheduleView>(`/api/search-profiles/${id}/schedule/pause`, { method: "POST" }),
  resumeSchedule: (id: string) => request<ScheduleView>(`/api/search-profiles/${id}/schedule/resume`, { method: "POST" }),

  sources: () => request<SourceRow[]>("/api/sources"),
  toggleSource: (id: string) => request<SourceRow>(`/api/sources/${id}/toggle`, { method: "POST" }),
  testSource: (id: string) =>
    request<{ ok: boolean; error: string | null; sample: number }>(`/api/sources/${id}/test`, { method: "POST" }),
  jobs: () => request<JobRow[]>("/api/collection-jobs"),

  overview: () => request<Overview>("/api/reports/overview"),

  exclusions: (profileId: string) => request<ExclusionRow[]>(`/api/search-profiles/${profileId}/exclusions`),
  addExclusion: (profileId: string, pattern: string, matchType: MatchType) =>
    request<ExclusionRow>(`/api/search-profiles/${profileId}/exclusions`, {
      method: "POST", body: JSON.stringify({ pattern, matchType, active: true }),
    }),
  updateExclusion: (profileId: string, r: ExclusionRow) =>
    request<ExclusionRow>(`/api/search-profiles/${profileId}/exclusions/${r.id}`, {
      method: "PUT", body: JSON.stringify({ pattern: r.pattern, matchType: r.matchType, active: r.isActive }),
    }),
  deleteExclusion: (profileId: string, ruleId: string) =>
    request<{ ok: boolean }>(`/api/search-profiles/${profileId}/exclusions/${ruleId}`, { method: "DELETE" }),
  testExclusions: (profileId: string, content: string) =>
    request<TestVerdict>(`/api/search-profiles/${profileId}/exclusions/test`, {
      method: "POST", body: JSON.stringify({ content }),
    }),
  excluded: () => request<ExcludedPost[]>("/api/analysis/excluded"),
};

export interface ServiceRow { id: string; name: string; category: string }
export interface ProfileInput {
  name: string; countries: string[]; cities: string[]; languages: string[]; minScore: number;
  keywords: string[];
}
export interface ProfileRow {
  id: string; name: string; countries: string[]; cities: string[]; languages: string[]; minScore: number;
  keywords: { id: string; keyword: string }[];
}
export interface RunResult {
  sourceId: string; sourceType: string; status: "done" | "failed" | "skipped";
  newPosts: number; leads: number; excluded: number; needsReview: number; error: string | null; retriable: boolean;
}
export type MatchType = "phrase" | "keyword";
export interface ExclusionRow { id: string; pattern: string; matchType: MatchType; isActive: boolean }
export interface TestVerdict {
  isLead: boolean; needsReview: boolean; reviewReason: string | null;
  exclusionReason: string | null; exclusionSource: string | null;
  matchedRule: { id: string | null; pattern: string; matchType: string } | null;
  customMatches: { pattern: string; negated: boolean; outcome: string | null }[] | null;
  serviceCategory: string | null;
}
export interface ExcludedPost {
  id: string; content: string; url: string; publishedAt: string;
  reason: string | null; source: string | null; matchedPattern: string | null;
}
export interface SourceRow { id: string; sourceType: string; status: string; lastError: string | null; updatedAt: string }
export interface JobRow {
  id: string; sourceId: string; status: string; fetched: number; error: string | null;
  startedAt: string | null; finishedAt: string | null;
}
export interface Overview {
  total: number;
  needsReview: number;
  byPriority: { key: string | null; count: number }[];
  byStatus: { key: string | null; count: number }[];
  byCategory: { key: string | null; count: number }[];
  byCountry: { key: string | null; count: number }[];
}
