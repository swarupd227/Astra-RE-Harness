import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { buildStackOptions, recommendedStack, type StackOption } from '@/lib/targetStacks';

const STORAGE_KEY = 'astra.targetStack';

export type TargetStackState = {
  /** The stack that will actually be used for generation. */
  targetStack: string;
  setTargetStack: (next: string) => void;
  options: StackOption[];
  /** What the platform would pick for this source language; null while loading. */
  recommended: string | null;
  /** Set when the saved choice can't build this routine and we overrode it. */
  overriddenFrom: string | null;
  /** Set when the saved choice is viable but isn't the recommendation. */
  savedOverridesRecommended: string | null;
  isLoading: boolean;
};

/**
 * Engineer-chosen target stack, persisted across reloads and reconciled
 * against what is actually buildable for this routine's source language.
 *
 * The saved value used to win unconditionally, so one past click on .NET 8
 * pinned .NET 8 for every routine afterwards — silently, and even for schemas
 * with no .NET 8 archetype. Here the saved choice is honoured only while it
 * remains viable, and callers get `overriddenFrom` /
 * `savedOverridesRecommended` so the UI can say what happened instead of
 * quietly doing the wrong thing.
 */
export function useTargetStack(sourceLanguage: string | null | undefined): TargetStackState {
  const archetypes = useQuery({
    queryKey: ['archetypes'],
    queryFn: api.listArchetypes,
    staleTime: 5 * 60_000,
  });

  const [savedStack, setSavedStack] = useState<string | null>(() => {
    try { return localStorage.getItem(STORAGE_KEY); }
    catch { return null; }
  });

  const setTargetStack = (next: string) => {
    setSavedStack(next);
    try { localStorage.setItem(STORAGE_KEY, next); }
    catch { /* private mode etc. */ }
  };

  const options = useMemo(
    () => buildStackOptions(archetypes.data?.data ?? [], sourceLanguage),
    [archetypes.data, sourceLanguage],
  );
  const recommended = useMemo(
    () => recommendedStack(archetypes.data?.data ?? [], sourceLanguage),
    [archetypes.data, sourceLanguage],
  );

  const savedIsUsable = options.some((o) => o.stack === savedStack && o.selectable);
  const targetStack = savedIsUsable ? savedStack! : (recommended ?? savedStack ?? 'dotnet8');

  // `recommended` is null until the archetype query resolves, so neither
  // notice can fire on a loading frame.
  const overriddenFrom = savedStack && !savedIsUsable && recommended && savedStack !== recommended
    ? savedStack
    : null;
  const savedOverridesRecommended = savedIsUsable && recommended && savedStack !== recommended
    ? recommended
    : null;

  return {
    targetStack,
    setTargetStack,
    options,
    recommended,
    overriddenFrom,
    savedOverridesRecommended,
    isLoading: archetypes.isPending,
  };
}
