import { ApiError, preview, record } from './api';

function respond(status: number, body: unknown) {
  return jest.fn().mockResolvedValue({
    ok: status < 400,
    status,
    json: async () => body,
    text: async () => JSON.stringify(body),
  });
}

describe('calculation api', () => {
  afterEach(() => jest.restoreAllMocks());

  it('posts the inputs to the preview route', async () => {
    const fetchMock = respond(200, { category: 'A', term: 10, amount: 1000, rate: 2, total: 20 });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const result = await preview({ category: 'A', term: 10, amount: 1000 });
    expect(fetchMock).toHaveBeenCalledWith('/api/calculations/preview', expect.objectContaining({ method: 'POST' }));
    expect(result.total).toBe(20);
  });

  it('records through the collection route', async () => {
    const fetchMock = respond(201, { id: 1, category: 'A', term: 10, amount: 1000, rate: 2, total: 20, recorded_at: 'now' });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const saved = await record({ category: 'A', term: 10, amount: 1000 });
    expect(fetchMock).toHaveBeenCalledWith('/api/calculations', expect.objectContaining({ method: 'POST' }));
    expect(saved.id).toBe(1);
  });

  it('turns a refusal into an ApiError with the reason', async () => {
    globalThis.fetch = respond(422, { detail: 'term' }) as unknown as typeof fetch;
    await expect(preview({ category: 'A', term: 99, amount: 1000 })).rejects.toBeInstanceOf(ApiError);
  });
});
