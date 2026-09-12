import { useEffect, useRef } from 'react';
import { themeHex, useTheme, type ThemeName } from '@/theme';

interface Props {
  source: string;
}

let initialisedFor: ThemeName | null = null;

/**
 * Mermaid config for the active theme. Dark uses mermaid's own `dark` base
 * with every colour overridden by a token so the diagram sits on the raised
 * surface; light does the same on the `neutral` base.
 */
function mermaidConfig(theme: ThemeName) {
  const t = (name: Parameters<typeof themeHex>[0]) => themeHex(name, theme);
  return {
    startOnLoad: false,
    theme: theme === 'dark' ? ('dark' as const) : ('neutral' as const),
    fontFamily: 'JetBrains Mono, ui-monospace, monospace',
    themeVariables: {
      background: t('raised'),
      primaryColor: t('sunken'),
      primaryTextColor: t('ink-primary'),
      primaryBorderColor: t('line-strong'),
      secondaryColor: t('sunken'),
      secondaryTextColor: t('ink-secondary'),
      secondaryBorderColor: t('line'),
      tertiaryColor: t('canvas'),
      tertiaryTextColor: t('ink-secondary'),
      tertiaryBorderColor: t('line-subtle'),
      lineColor: t('ink-tertiary'),
      textColor: t('ink-primary'),
      mainBkg: t('sunken'),
      nodeBorder: t('line-strong'),
      clusterBkg: t('canvas'),
      clusterBorder: t('line'),
      titleColor: t('ink-primary'),
      edgeLabelBackground: t('raised'),
      // sequence diagrams
      actorBkg: t('sunken'),
      actorBorder: t('line-strong'),
      actorTextColor: t('ink-primary'),
      actorLineColor: t('line'),
      signalColor: t('ink-primary'),
      signalTextColor: t('ink-primary'),
      labelBoxBkgColor: t('raised'),
      labelBoxBorderColor: t('line'),
      labelTextColor: t('ink-primary'),
      loopTextColor: t('ink-primary'),
      noteBkgColor: t('volt'),
      noteTextColor: t('on-volt'),
      noteBorderColor: t('volt'),
      activationBkgColor: t('raised'),
      activationBorderColor: t('line-strong'),
    },
  };
}

export function MermaidBlock({ source }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const idRef = useRef(`mermaid-${Math.random().toString(36).slice(2)}`);
  const { theme } = useTheme();

  useEffect(() => {
    if (!source?.trim() || !ref.current) return;
    let cancelled = false;
    const id = idRef.current;

    import('mermaid').then(({ default: mermaid }) => {
      if (cancelled) return;
      if (initialisedFor !== theme) {
        mermaid.initialize(mermaidConfig(theme));
        initialisedFor = theme;
      }
      mermaid.render(id, source).then(({ svg }) => {
        if (!cancelled && ref.current) ref.current.innerHTML = svg;
      }).catch((err: Error) => {
        if (!cancelled && ref.current)
          ref.current.textContent = `Diagram error: ${err.message}`;
      });
    });

    return () => { cancelled = true; };
  }, [source, theme]);

  return <div ref={ref} className="overflow-x-auto py-2 [&_svg]:max-w-full" />;
}
