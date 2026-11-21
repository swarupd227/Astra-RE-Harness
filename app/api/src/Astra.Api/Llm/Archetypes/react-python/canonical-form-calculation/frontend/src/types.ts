/** The shapes the API takes and returns (mirrors backend/app/models.py). @SpecClaim INV-1 */
export type Category = 'A' | 'B' | 'C';

export const CATEGORIES: Category[] = ['A', 'B', 'C'];

export interface CalculationInput {
  category: Category;
  term: number;
  amount: number;
}

export interface CalculationResult extends CalculationInput {
  rate: number;
  total: number;
}

export interface SavedCalculation extends CalculationResult {
  id: number;
  recorded_at: string;
}
