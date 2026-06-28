import { useEffect, useRef } from 'react';

interface Props {
  source: string;
}

let mermaidInitialised = false;

export function MermaidBlock({ source }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const idRef = useRef(`mermaid-${Math.random().toString(36).slice(2)}`);

  useEffect(() => {
    if (!source?.trim() || !ref.current) return;
    let cancelled = false;
    const id = idRef.current;

    import('mermaid').then(({ default: mermaid }) => {
      if (cancelled) return;
      if (!mermaidInitialised) {
        mermaid.initialize({ startOnLoad: false, theme: 'neutral', fontFamily: 'ui-monospace, monospace' });
        mermaidInitialised = true;
      }
      mermaid.render(id, source).then(({ svg }) => {
        if (!cancelled && ref.current) ref.current.innerHTML = svg;
      }).catch((err: Error) => {
        if (!cancelled && ref.current)
          ref.current.textContent = `Diagram error: ${err.message}`;
      });
    });

    return () => { cancelled = true; };
  }, [source]);

  return <div ref={ref} className="overflow-x-auto py-2 [&_svg]:max-w-full" />;
}
