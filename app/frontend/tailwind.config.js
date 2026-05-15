/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // Surfaces
        canvas: '#F8F8F6',
        raised: '#FFFFFF',
        sunken: '#F1F1ED',
        codebg: '#0F1116',

        // Ink
        ink: {
          primary: '#101728',
          secondary: '#475063',
          tertiary: '#7A8497',
          inverse: '#F8F8F6',
          link: '#1F4FA8',
        },

        // Accent
        accent: {
          DEFAULT: '#E5732C',
          hover: '#CD5F1E',
          muted: '#FBE7D6',
        },

        // Status
        status: {
          draft: '#B9520B',
          review: '#0E7C66',
          signed: '#1F4FA8',
          scaffolded: '#9A6E1A',
          failed: '#A8201A',
          superseded: '#7A8497',
          untouched: '#C9CFD8',
        },

        // Borders
        border: {
          subtle: '#E4E6EB',
          DEFAULT: '#CFD3DA',
          strong: '#101728',
        },
      },
      fontFamily: {
        sans: [
          'Inter',
          'ui-sans-serif',
          'system-ui',
          '-apple-system',
          'Segoe UI',
          'Roboto',
          'Helvetica Neue',
          'Arial',
          'sans-serif',
        ],
        mono: [
          'JetBrains Mono',
          'ui-monospace',
          'SFMono-Regular',
          'Menlo',
          'Monaco',
          'Consolas',
          'Liberation Mono',
          'Courier New',
          'monospace',
        ],
      },
      fontSize: {
        // Pairs: [size, lineHeight]
        'caption': ['12px', '16px'],
        'body': ['14px', '20px'],
        'body-lg': ['16px', '24px'],
        'h-sm': ['14px', '20px'],
        'h-md': ['18px', '26px'],
        'h-lg': ['22px', '30px'],
        'display': ['28px', '36px'],
      },
      borderRadius: {
        sm: '4px',
        md: '6px',
        lg: '8px',
      },
      boxShadow: {
        e1: '0 1px 2px rgba(16,23,40,0.04), 0 1px 3px rgba(16,23,40,0.06)',
        e2: '0 4px 12px rgba(16,23,40,0.08)',
        e3: '0 12px 32px rgba(16,23,40,0.12)',
      },
      transitionDuration: {
        fast: '100ms',
        medium: '200ms',
        slow: '320ms',
      },
    },
  },
  plugins: [],
};
