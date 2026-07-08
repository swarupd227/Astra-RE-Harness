/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // ── ACE-style two-tone palette ──────────────────────────────
        // The CONTENT area is light slate-100 / white cards (Linear /
        // Vercel / Stripe pattern). The SIDEBAR stays dark indigo so
        // navigation reads as a distinct surface.
        canvas: '#f1f5f9',  // slate-100  — body background
        raised: '#ffffff',  // white      — card surface
        sunken: '#f8fafc',  // slate-50   — sub-surface / table headers
        codebg: '#0f172a',  // slate-900  — code blocks (dark even in light theme)

        // Ink scale — for the LIGHT content area.
        ink: {
          primary:   '#0f172a', // slate-900 — body
          secondary: '#475569', // slate-600 — supporting text
          tertiary:  '#64748b', // slate-500 — captions, meta (4.8:1 on white — WCAG AA)
          inverse:   '#f1f5f9', // for use on dark sidebar
          link:      '#4f46e5', // indigo-600
        },

        // Vee/ACE-style brand orange — primary actions, customer-mark.
        accent: {
          DEFAULT: '#f26722', // brand-500
          hover:   '#e2520f', // brand-600
          muted:   '#fff4ed', // brand-50
        },

        // Brand orange scale (`brand-50`..`brand-700`) for fine control.
        brand: {
          50:  '#fff4ed',
          100: '#ffe6d5',
          300: '#ffb088',
          500: '#f26722',
          600: '#e2520f',
          700: '#bb3e0f',
        },

        // ACE indigo — used on the dark sidebar + as the in-app focus
        // colour. `ace-900` is the sidebar surface.
        ace: {
          50:  '#eef2ff',
          100: '#e0e7ff',
          400: '#818cf8',
          500: '#6366f1',
          600: '#4f46e5',
          700: '#4338ca',
          900: '#1e1b4b',
        },

        // ── Semantic accents (kept from earlier; remain useful) ─────
        indigo: {
          soft: '#eef2ff',
          DEFAULT: '#4f46e5',
          ink:  '#1e1b4b',
        },
        violet: {
          soft: '#f5f3ff',
          DEFAULT: '#7c3aed',
          ink:  '#3b0764',
        },
        emerald: {
          soft: '#ecfdf5',
          DEFAULT: '#059669',
          ink:  '#064e3b',
        },
        teal: {
          soft: '#f0fdfa',
          DEFAULT: '#0d9488',
          ink:  '#134e4a',
        },
        amber: {
          soft: '#fffbeb',
          DEFAULT: '#d97706',
          ink:  '#78350f',
        },
        rose: {
          soft: '#fff1f2',
          DEFAULT: '#e11d48',
          ink:  '#881337',
        },

        // Persona accents.
        persona: {
          engineer: '#4f46e5',
          sme:      '#059669',
          observer: '#94a3b8',
          admin:    '#d97706',
        },

        // 5-stop wave gradient — used on the migration plan + dep graph.
        wave: {
          1: '#059669', // emerald
          2: '#0d9488', // teal
          3: '#4f46e5', // indigo
          4: '#7c3aed', // violet
          5: '#d97706', // amber
        },

        // Status colours — light-theme appropriate.
        status: {
          draft:      '#d97706', // amber-600
          review:     '#059669', // emerald-600
          signed:     '#4f46e5', // indigo-600
          scaffolded: '#d97706', // amber-600
          failed:     '#e11d48', // rose-600
          superseded: '#94a3b8',
          untouched:  '#cbd5e1',
        },

        // Borders.
        border: {
          subtle:  '#e2e8f0', // slate-200
          DEFAULT: '#cbd5e1', // slate-300
          strong:  '#0f172a',
        },
      },
      backgroundImage: {
        // Light hero washes — keep the page-personality story but on light.
        'hero-indigo':  'linear-gradient(135deg, #eef2ff 0%, #ffffff 70%)',
        'hero-emerald': 'linear-gradient(135deg, #ecfdf5 0%, #ffffff 70%)',
        'hero-amber':   'linear-gradient(135deg, #fffbeb 0%, #ffffff 70%)',
        'hero-violet':  'linear-gradient(135deg, #f5f3ff 0%, #ffffff 70%)',
        'hero-teal':    'linear-gradient(135deg, #f0fdfa 0%, #ffffff 70%)',
        'hero-orange':  'linear-gradient(135deg, #fff4ed 0%, #ffffff 70%)',
        // Brand-mark gradient — orange square in the logo lockup.
        'brand-mark':   'linear-gradient(135deg, #f26722 0%, #bb3e0f 100%)',
        // Identity stripe along the top of the shell.
        'brand-stripe': 'linear-gradient(90deg, #f26722 0%, #d97706 40%, #4f46e5 100%)',
      },
      fontFamily: {
        sans: ['Inter', 'ui-sans-serif', 'system-ui', '-apple-system', 'Segoe UI', 'Roboto', 'Helvetica Neue', 'Arial', 'sans-serif'],
        mono: ['JetBrains Mono', 'ui-monospace', 'SFMono-Regular', 'Menlo', 'Monaco', 'Consolas', 'Liberation Mono', 'Courier New', 'monospace'],
      },
      fontSize: {
        'micro':   ['10px', '14px'],
        'caption': ['11px', '16px'],
        'body':    ['13px', '18px'],
        'body-lg': ['15px', '22px'],
        'h-sm':    ['13px', '18px'],
        'h-md':    ['16px', '24px'],
        'h-lg':    ['20px', '28px'],
        'display': ['24px', '32px'],
      },
      borderRadius: {
        sm: '6px',
        md: '8px',
        lg: '12px',
        xl: '16px',
      },
      boxShadow: {
        card: '0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.1)',
        e1:   '0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.1)',
        e2:   '0 4px 12px rgba(16,24,40,.10)',
        e3:   '0 16px 42px rgba(16,24,40,.18)',
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
