import type { CalculationInput, CalculationResult, SavedCalculation } from './types';

const BASE = '/api/calculations';

/** A refusal from the API, with the reason it gave. */
export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

async function send<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { headers: { 'Content-Type': 'application/json' }, ...init });
  if (!response.ok) throw new ApiError(response.status, await response.text());
  return (await response.json()) as T;
}

/** Calculate without recording. @SpecClaim INV-1 */
export const preview = (input: CalculationInput) =>
  send<CalculationResult>(`${BASE}/preview`, { method: 'POST', body: JSON.stringify(input) });

/** Calculate and record. @SpecClaim SE-1 */
export const record = (input: CalculationInput) =>
  send<SavedCalculation>(BASE, { method: 'POST', body: JSON.stringify(input) });

export const recorded = () => send<SavedCalculation[]>(BASE);
