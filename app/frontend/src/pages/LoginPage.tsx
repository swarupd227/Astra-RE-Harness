import { useState } from 'react';
import { Loader2, LogIn } from 'lucide-react';
import { signIn } from '@/lib/auth';

const inputClass =
  'mt-1 w-full rounded-md border border-line bg-raised px-3 py-2 text-body text-ink-primary placeholder:text-ink-tertiary focus:border-volt focus:outline-none focus:ring-2 focus:ring-volt/20';

/** Sign in to a deployed platform (the API runs with Auth:Mode=password). */
export function LoginPage() {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const user = await signIn(username, password);
      // Start in a persona the user holds; the switcher stays within them.
      const persona = user.personas.includes('engineer') ? 'engineer' : user.personas[0];
      if (persona) {
        try { localStorage.setItem('astra.devPersona', persona); } catch { /* ignore */ }
      }
      const next = new URLSearchParams(window.location.search).get('next');
      window.location.assign(next && next.startsWith('/') && !next.startsWith('//') ? next : '/');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Sign-in failed.');
      setBusy(false);
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-canvas p-4 text-ink-primary" data-testid="login-page">
      <form onSubmit={submit} className="w-full max-w-sm space-y-4 rounded-lg border border-line bg-raised p-6 shadow-sm">
        <div>
          <p className="label">Astra</p>
          <h1 className="mt-1 text-h-lg font-semibold tracking-tight">Sign in</h1>
          <p className="mt-1 text-caption text-ink-secondary">This platform is private. Sign in with the account your administrator gave you.</p>
        </div>
        <label className="block text-body font-medium">
          Username
          <input
            className={inputClass}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            autoFocus
            required
            data-testid="login-username"
          />
        </label>
        <label className="block text-body font-medium">
          Password
          <input
            className={inputClass}
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
            data-testid="login-password"
          />
        </label>
        {error && (
          <p role="alert" className="rounded-md border border-status-fail/40 bg-status-fail/10 px-3 py-2 text-caption text-status-fail" data-testid="login-error">
            {error}
          </p>
        )}
        <button
          type="submit"
          disabled={busy}
          className="flex w-full items-center justify-center gap-2 rounded-md bg-volt px-4 py-2 text-body font-medium text-on-volt hover:opacity-90 focus:outline-none focus:ring-2 focus:ring-volt disabled:opacity-60"
          data-testid="login-submit"
        >
          {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <LogIn className="h-4 w-4" />}
          Sign in
        </button>
      </form>
    </div>
  );
}
