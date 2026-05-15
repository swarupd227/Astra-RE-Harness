/**
 * Design tokens — TypeScript mirror of tailwind.config.js for components
 * that need raw values (e.g. inline styles, Monaco theme).
 *
 * Keep in sync with 02_UX/design-system.md.
 */
export const tokens = {
  surface: {
    canvas: '#F8F8F6',
    raised: '#FFFFFF',
    sunken: '#F1F1ED',
    code: '#0F1116',
  },
  ink: {
    primary: '#101728',
    secondary: '#475063',
    tertiary: '#7A8497',
    inverse: '#F8F8F6',
    link: '#1F4FA8',
  },
  accent: {
    primary: '#E5732C',
    primaryHover: '#CD5F1E',
    primaryMuted: '#FBE7D6',
  },
  status: {
    draft: '#B9520B',
    review: '#0E7C66',
    signed: '#1F4FA8',
    scaffolded: '#9A6E1A',
    failed: '#A8201A',
    superseded: '#7A8497',
    untouched: '#C9CFD8',
  },
  motion: {
    fast: 100,
    medium: 200,
    slow: 320,
    pulse: 1500,
  },
} as const;

export type Persona = 'engineer' | 'sme' | 'observer' | 'admin';
export const ALL_PERSONAS: Persona[] = ['engineer', 'sme', 'observer', 'admin'];
