/** @type {import('tailwindcss').Config} */

// Every colour resolves through an RGB-triplet CSS variable declared in
// src/theme/theme.css (generated from src/theme/palette.json). That is what
// lets a single class name render dark in the shell and light inside the
// `data-theme="light"` legacy wrapper — Tailwind only ever emits
// `rgb(var(--token) / <alpha>)`, and the variable decides.
const v = (name) => `rgb(var(--${name}) / <alpha-value>)`;

// Three-stop semantic accents (soft wash / DEFAULT / ink) — kept so the old
// page-personality classes (`bg-emerald-soft`, `text-rose-ink`, …) still
// compile. Numbered shades (`amber-700`, `rose-50`) stay Tailwind defaults.
const tri = (name) => ({ soft: v(`${name}-soft`), DEFAULT: v(name), ink: v(`${name}-ink`) });

export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  // `dark:` follows the [data-theme] attribute, not the OS. The :not() clause
  // keeps a nested light container (legacy pages) from inheriting dark
  // variants from the dark <html> above it.
  darkMode: ['variant', '&:is([data-theme="dark"] *):not([data-theme="light"] *)'],
  theme: {
    extend: {
      colors: {
        // ── Surfaces ─────────────────────────────────────────────────
        canvas: v('canvas'),
        raised: v('raised'),
        sunken: v('sunken'),
        codebg: v('codebg'),

        // ── Lines (`border` is the legacy alias of `line`) ───────────
        line: { subtle: v('line-subtle'), DEFAULT: v('line'), strong: v('line-strong') },
        border: { subtle: v('line-subtle'), DEFAULT: v('line'), strong: v('line-strong') },

        // ── Ink ──────────────────────────────────────────────────────
        ink: {
          primary: v('ink-primary'),
          secondary: v('ink-secondary'),
          tertiary: v('ink-tertiary'),
          inverse: v('ink-inverse'),
          link: v('ink-link'),
        },

        // ── The single accent ────────────────────────────────────────
        // volt is reserved for: agent-is-working, the primary CTA, focus.
        volt: v('volt'),
        'volt-ink': v('volt-ink'), // readable volt on light surfaces
        'on-volt': v('on-volt'), // near-black text on a volt fill

        sand: {
          100: v('sand-100'),
          200: v('sand-200'),
          300: v('sand-300'),
          400: v('sand-400'),
          500: v('sand-500'),
        },

        status: {
          ok: v('status-ok'),
          warn: v('status-warn'),
          fail: v('status-fail'),
          info: v('status-info'),
          // Legacy spec-state names, mapped onto the four above.
          draft: v('status-draft'),
          review: v('status-review'),
          signed: v('status-signed'),
          scaffolded: v('status-scaffolded'),
          failed: v('status-failed'),
          superseded: v('status-superseded'),
          untouched: v('status-untouched'),
        },

        persona: {
          engineer: v('persona-engineer'),
          sme: v('persona-sme'),
          observer: v('persona-observer'),
          admin: v('persona-admin'),
        },

        // 5-stop volt → sand ramp (migration plan + dependency graph).
        wave: { 1: v('wave-1'), 2: v('wave-2'), 3: v('wave-3'), 4: v('wave-4'), 5: v('wave-5') },

        // ── Legacy names, remapped ───────────────────────────────────
        // Dark: volt. Light (legacy wrapper): dark-ink CTA so the existing
        // `bg-accent text-white` / `bg-brand-500 text-white` stay readable.
        accent: { DEFAULT: v('accent'), hover: v('accent-hover'), muted: v('accent-muted') },
        brand: {
          50: v('brand-50'),
          100: v('brand-100'),
          300: v('brand-300'),
          500: v('brand-500'),
          600: v('brand-600'),
          700: v('brand-700'),
        },
        // ace-* was the indigo family (sidebar + "signed"). Now: signed → info
        // blue; ace-900 (the old sidebar) → raised.
        ace: {
          50: v('ace-50'),
          100: v('ace-100'),
          400: v('ace-400'),
          500: v('ace-500'),
          600: v('ace-600'),
          700: v('ace-700'),
          900: v('ace-900'),
        },
        indigo: tri('indigo'),
        violet: tri('violet'),
        emerald: tri('emerald'),
        teal: tri('teal'),
        amber: tri('amber'),
        rose: tri('rose'),
      },
      backgroundImage: {
        // Hero washes — start from the semantic soft tint and fade into the
        // raised surface, so they follow the theme instead of assuming white.
        'hero-indigo': 'linear-gradient(135deg, rgb(var(--indigo-soft)) 0%, rgb(var(--raised)) 70%)',
        'hero-emerald': 'linear-gradient(135deg, rgb(var(--emerald-soft)) 0%, rgb(var(--raised)) 70%)',
        'hero-amber': 'linear-gradient(135deg, rgb(var(--amber-soft)) 0%, rgb(var(--raised)) 70%)',
        'hero-violet': 'linear-gradient(135deg, rgb(var(--violet-soft)) 0%, rgb(var(--raised)) 70%)',
        'hero-teal': 'linear-gradient(135deg, rgb(var(--teal-soft)) 0%, rgb(var(--raised)) 70%)',
        'hero-orange': 'linear-gradient(135deg, rgb(var(--accent-muted)) 0%, rgb(var(--raised)) 70%)',
        // Brand mark — volt into amber.
        'brand-mark': 'linear-gradient(135deg, #FFDD00 0%, #F5A524 100%)',
        // Identity stripe — volt → sand ramp.
        'brand-stripe': 'linear-gradient(90deg, #FFDD00 0%, #D8CEC3 55%, #7B716D 100%)',
      },
      fontFamily: {
        sans: ['Inter Variable', 'Inter', 'ui-sans-serif', 'system-ui', '-apple-system', 'Segoe UI', 'Roboto', 'Helvetica Neue', 'Arial', 'sans-serif'],
        mono: ['JetBrains Mono', 'ui-monospace', 'SFMono-Regular', 'Menlo', 'Monaco', 'Consolas', 'Liberation Mono', 'Courier New', 'monospace'],
      },
      // Type scale, lifted one step across the board (Aug 2026 UX review).
      // The previous scale — 13px body, 11px captions — was IDE density and
      // was the single biggest reason the product read as a developer
      // console rather than a business application. Names are unchanged so
      // the lift applies everywhere at once without touching components.
      fontSize: {
        'micro':   ['11px', '16px'],   // badges, dense chips
        'caption': ['13px', '18px'],   // metadata, eyebrows
        'body':    ['15px', '22px'],   // default reading size
        'body-lg': ['17px', '26px'],   // lead paragraphs
        'h-sm':    ['15px', '22px'],
        'h-md':    ['18px', '26px'],
        'h-lg':    ['24px', '32px'],
        'display': ['30px', '38px'],   // page titles
      },
      borderRadius: {
        sm: '6px',
        md: '8px',
        lg: '12px',
        xl: '16px',
      },
      boxShadow: {
        card: '0 1px 2px rgba(0,0,0,.18), 0 1px 3px rgba(0,0,0,.24)',
        e1:   '0 1px 2px rgba(0,0,0,.18), 0 1px 3px rgba(0,0,0,.24)',
        e2:   '0 4px 12px rgba(0,0,0,.28)',
        e3:   '0 16px 42px rgba(0,0,0,.45)',
        // Volt halo — agent working / focused primary CTA.
        glow: '0 0 0 1px rgba(255,221,0,.35), 0 0 24px rgba(255,221,0,.18)',
      },
      transitionDuration: {
        fast:   '100ms',
        medium: '200ms',
        slow:   '320ms',
      },
    },
  },
  safelist: [
    'bg-wave-1', 'bg-wave-2', 'bg-wave-3', 'bg-wave-4', 'bg-wave-5',
    'text-wave-1', 'text-wave-2', 'text-wave-3', 'text-wave-4', 'text-wave-5',
    'border-wave-1', 'border-wave-2', 'border-wave-3', 'border-wave-4', 'border-wave-5',
    'bg-persona-engineer', 'bg-persona-sme', 'bg-persona-observer', 'bg-persona-admin',
    'text-persona-engineer', 'text-persona-sme', 'text-persona-observer', 'text-persona-admin',
    'bg-hero-indigo', 'bg-hero-emerald', 'bg-hero-amber', 'bg-hero-violet', 'bg-hero-teal', 'bg-hero-orange',
  ],
  plugins: [],
};
