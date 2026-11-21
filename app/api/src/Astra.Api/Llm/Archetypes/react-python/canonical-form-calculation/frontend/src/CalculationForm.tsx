import { useState, type FormEvent } from 'react';
import { preview, record } from './api';
import { CATEGORIES, type CalculationInput, type CalculationResult, type Category } from './types';

/**
 * The form the user fills in — the inputs, their rules, and the two actions the sheet's buttons had
 * (calculate, record). Rules are checked here for a quick answer and again by the API.
 * @SpecClaim INV-1
 */
export function CalculationForm() {
  const [input, setInput] = useState<CalculationInput>({ category: 'A', term: 10, amount: 10000 });
  const [result, setResult] = useState<CalculationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const problem =
    input.term < 1 || input.term > 40 ? 'Term must be between 1 and 40 years.'
    : !(input.amount > 0) ? 'Amount must be more than 0.'
    : null;

  async function run(action: 'calculate' | 'record', e?: FormEvent) {
    e?.preventDefault();
    if (problem) return;
    setError(null);
    try {
      const r = action === 'calculate' ? await preview(input) : await record(input);
      setResult(r);
      setSaved(action === 'record');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <form onSubmit={(e) => run('calculate', e)} aria-label="Calculation">
      <label>
        Category
        <select value={input.category} onChange={(e) => setInput({ ...input, category: e.target.value as Category })}>
          {CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
      </label>
      <label>
        Term (years)
        <input type="number" value={input.term} onChange={(e) => setInput({ ...input, term: Number(e.target.value) })} />
      </label>
      <label>
        Amount
        <input type="number" value={input.amount} onChange={(e) => setInput({ ...input, amount: Number(e.target.value) })} />
      </label>
      {problem && <p role="alert">{problem}</p>}
      <button type="submit" disabled={!!problem}>Calculate</button>
      <button type="button" disabled={!!problem} onClick={() => run('record')}>Record</button>
      {error && <p role="alert">{error}</p>}
      {result && (
        <p data-testid="total">
          Total {result.total.toFixed(2)} at rate {result.rate}{saved ? ' — recorded' : ''}
        </p>
      )}
    </form>
  );
}
