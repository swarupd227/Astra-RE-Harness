/**
 * The four dev personas the API recognises via `X-Dev-Persona`. Lives here
 * (not in the theme layer) because it is an API contract, not a design token.
 */
export type Persona = 'engineer' | 'sme' | 'observer' | 'admin';

export const ALL_PERSONAS: Persona[] = ['engineer', 'sme', 'observer', 'admin'];
