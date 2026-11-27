/**
 * Sign-in (the API's Auth:Mode=password). The session token lives in this browser's storage and goes with every
 * request to the API: as an Authorization header on fetch, and as an access_token query parameter where a request
 * cannot carry headers (EventSource streams, links to downloads and media). In dev-header mode no token exists and
 * nothing changes.
 */
const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '');
const TOKEN_KEY = 'astra.session';

export type SessionUser = { id: string; username: string; displayName: string; personas: string[] };

export function getToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

export function setToken(token: string | null) {
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token);
    else localStorage.removeItem(TOKEN_KEY);
  } catch {
    /* private mode: the session lasts for this page only */
  }
}

const isApiUrl = (url: string) => url.startsWith(API_BASE + '/');

/** A link the browser opens itself (download, audio): carries the token when signed in. */
export function withToken(url: string): string {
  const token = getToken();
  if (!token || !isApiUrl(url)) return url;
  return url + (url.includes('?') ? '&' : '?') + 'access_token=' + encodeURIComponent(token);
}

function toLogin() {
  if (window.location.pathname === '/login') return;
  const next = window.location.pathname + window.location.search;
  window.location.assign('/login?next=' + encodeURIComponent(next));
}

let installed = false;

/** Adds the session to every API request made by this page, and sends the user to sign in when a session ends. */
export function installAuth() {
  if (installed) return;
  installed = true;

  const realFetch = window.fetch.bind(window);
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    if (!isApiUrl(url)) return realFetch(input, init);
    const token = getToken();
    const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
    if (token && !headers.has('Authorization')) headers.set('Authorization', `Bearer ${token}`);
    const res = await realFetch(input, { ...init, headers });
    if (res.status === 401 && !url.includes('/api/v1/auth/login')) {
      const body = await res.clone().json().catch(() => null);
      if (body?.error?.code === 'auth.required') {
        setToken(null);
        toLogin();
      }
    }
    return res;
  };

  const RealEventSource = window.EventSource;
  class AuthEventSource extends RealEventSource {
    constructor(url: string | URL, config?: EventSourceInit) {
      super(withToken(typeof url === 'string' ? url : url.href), config);
    }
  }
  window.EventSource = AuthEventSource as unknown as typeof EventSource;
}

export async function fetchAuthMode(): Promise<'password' | 'dev-header'> {
  try {
    const res = await fetch(`${API_BASE}/api/v1/auth/config`);
    const body = await res.json();
    return body?.mode === 'password' ? 'password' : 'dev-header';
  } catch {
    return 'dev-header';
  }
}

export async function signIn(username: string, password: string): Promise<SessionUser> {
  const res = await fetch(`${API_BASE}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  });
  const body = await res.json().catch(() => null);
  if (!res.ok || !body?.token) throw new Error(body?.error?.message ?? `Sign-in failed (${res.status})`);
  setToken(body.token);
  return body.user as SessionUser;
}

export async function signOut() {
  try {
    await fetch(`${API_BASE}/api/v1/auth/logout`, { method: 'POST' });
  } finally {
    setToken(null);
    window.location.assign('/login');
  }
}
