import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { CalculationForm } from './CalculationForm';

describe('CalculationForm', () => {
  afterEach(() => jest.restoreAllMocks());

  it('shows the total the API calculates', async () => {
    globalThis.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ category: 'A', term: 10, amount: 10000, rate: 1.25, total: 125 }),
      text: async () => '',
    }) as unknown as typeof fetch;
    render(<CalculationForm />);
    fireEvent.click(screen.getByRole('button', { name: 'Calculate' }));
    await waitFor(() => expect(screen.getByTestId('total').textContent).toContain('125.00'));
  });

  it('refuses a term outside its rule before calling the API', () => {
    const fetchMock = jest.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    render(<CalculationForm />);
    fireEvent.change(screen.getByLabelText('Term (years)'), { target: { value: '0' } });
    expect(screen.getByRole('alert').textContent).toContain('between 1 and 40');
    expect(screen.getByRole('button', { name: 'Calculate' })).toHaveProperty('disabled', true);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
