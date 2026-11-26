import { ShieldCheck } from 'lucide-react';
import { Link } from 'react-router-dom';

/**
 * Shown instead of a page only an Admin can use. It is not an error: it says what the page holds, who can open it,
 * and where to go instead — calmly, in the page's own space. (The rail no longer links non-admins here; this is for
 * a typed URL or a shared link.)
 */
export function AdminOnlyNote({ what, instead }: { what: string; instead?: { to: string; label: string } }) {
  return (
    <div className="mx-auto max-w-[720px] p-4 lg:p-10">
      <div className="rounded-xl border border-line-subtle bg-raised px-6 py-6" data-testid="admin-only-note">
        <p className="flex items-center gap-2 text-body font-semibold text-ink-primary">
          <ShieldCheck className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
          Admin only
        </p>
        <p className="mt-2 text-body text-ink-secondary">{what}</p>
        <p className="mt-2 text-caption text-ink-secondary">
          Ask an Admin, or switch persona from the menu at the top right if you have the Admin role.
        </p>
        {instead && (
          <Link to={instead.to} className="mt-3 inline-block text-caption font-medium text-ink-link hover:underline">
            {instead.label}
          </Link>
        )}
      </div>
    </div>
  );
}
