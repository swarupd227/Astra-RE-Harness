import { Link } from 'react-router-dom';
import { Button } from '@/components/Button';

export function NotFoundPage() {
  return (
    <div className="mx-auto max-w-2xl p-12 text-center">
      <h1 className="text-display font-semibold text-ink-primary">Not yet built</h1>
      <p className="mt-3 text-body-lg text-ink-secondary">
        This screen lands in a later phase of the development plan.
      </p>
      <div className="mt-6">
        <Button variant="secondary" asChild={false}>
          <Link to="/">Back to Health</Link>
        </Button>
      </div>
    </div>
  );
}
