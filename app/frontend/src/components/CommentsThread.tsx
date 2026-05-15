import { useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, Circle, CornerDownRight, Loader2, MessageSquare, Pencil, RotateCcw, Trash2 } from 'lucide-react';
import { ApiError, commentsApi, getPersona, type CommentItem } from '@/lib/api';
import { Button } from '@/components/Button';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import type { Persona } from '@/tokens/tokens';

const PERSONAS: Persona[] = ['engineer', 'sme', 'observer', 'admin'];

/**
 * Phase C.7 — reusable threaded comments for a spec or a claim.
 *
 * If `claimPath` is provided we only fetch + post for that claim path.
 * Otherwise the thread is spec-level. Nesting is one-level (a reply
 * shows under its parent; replies-to-replies still nest under the
 * top-level parent for simplicity).
 */
export function CommentsThread({
  specId,
  claimPath,
  emptyHint,
}: {
  specId: string;
  claimPath?: string;
  emptyHint?: string;
}) {
  const qc = useQueryClient();
  const queryKey = ['comments', specId, claimPath ?? null] as const;
  const persona = getPersona();

  const thread = useQuery({
    queryKey,
    queryFn: () => commentsApi.list(specId, claimPath),
  });

  const post = useMutation<CommentItem, ApiError, { body: string; parentCommentId?: string }>({
    mutationFn: (vars) =>
      commentsApi.post(specId, { body: vars.body, claimPath, parentCommentId: vars.parentCommentId }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey });
      qc.invalidateQueries({ queryKey: ['notifications-unread'] });
    },
  });

  // Build tree: top-level comments at the root; children listed under their parent.
  // Replies to replies are flattened under the same top-level parent — keeps UX legible.
  const tree = useMemo(() => buildTree(thread.data?.data ?? []), [thread.data]);

  return (
    <div className="space-y-4" data-testid={claimPath ? `thread-${claimPath}` : `thread-spec-${specId}`}>
      <header className="flex items-center gap-2">
        <MessageSquare className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
        <h3 className="text-body font-semibold text-ink-primary">
          Comments
          {thread.data && (
            <span className="ml-2 font-mono text-caption text-ink-tertiary">
              {thread.data.data.length}
            </span>
          )}
        </h3>
      </header>

      {thread.isPending ? (
        <p className="text-caption text-ink-tertiary">Loading…</p>
      ) : thread.isError ? (
        <ErrorBlock title="Could not load comments" message={thread.error.message} onRetry={() => thread.refetch()} />
      ) : tree.length === 0 ? (
        <p className="text-caption text-ink-tertiary italic">
          {emptyHint ?? 'No comments yet. Start the thread below.'}
        </p>
      ) : (
        <ul className="space-y-3">
          {tree.map((node) => (
            <li key={node.comment.id}>
              <CommentBubble
                comment={node.comment}
                persona={persona}
                onReply={(body) => post.mutate({ body, parentCommentId: node.comment.id })}
              />
              {node.replies.length > 0 && (
                <ul className="mt-2 space-y-2 border-l border-border-subtle pl-4">
                  {node.replies.map((reply) => (
                    <li key={reply.id}>
                      <CommentBubble
                        comment={reply}
                        persona={persona}
                        isReply
                        onReply={(body) => post.mutate({ body, parentCommentId: node.comment.id })}
                      />
                    </li>
                  ))}
                </ul>
              )}
            </li>
          ))}
        </ul>
      )}

      <CommentComposer
        placeholder="Comment on this claim. Type @engineer or @sme to dispatch a notification."
        onSubmit={(body) => post.mutateAsync({ body })}
        busy={post.isPending}
        error={post.error?.message ?? null}
        autoFocus={false}
        testid="thread-composer"
      />
    </div>
  );
}

// ─── Comment bubble ──────────────────────────────────────────────────

function CommentBubble({
  comment,
  persona,
  isReply,
  onReply,
}: {
  comment: CommentItem;
  persona: Persona;
  isReply?: boolean;
  onReply: (body: string) => void;
}) {
  const qc = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [replying, setReplying] = useState(false);

  const edit = useMutation({
    mutationFn: (newBody: string) => commentsApi.edit(comment.id, newBody),
    onSuccess: () => {
      setEditing(false);
      qc.invalidateQueries({ queryKey: ['comments', comment.specId] });
    },
  });

  const toggleResolve = useMutation({
    mutationFn: () => commentsApi.resolve(comment.id, comment.resolvedAt !== null),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['comments', comment.specId] }),
  });

  const remove = useMutation({
    mutationFn: () => commentsApi.delete(comment.id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['comments', comment.specId] }),
  });

  const isAuthor = persona === comment.authorPersona;
  const resolved = comment.resolvedAt !== null;

  return (
    <article
      className={
        'rounded-md border bg-raised p-3 ' +
        (resolved ? 'border-status-review/40 bg-[#DAEFE9]/20' : 'border-border-subtle')
      }
      data-testid={`comment-${comment.id}`}
    >
      <header className="mb-2 flex flex-wrap items-center gap-2 text-caption">
        {isReply && <CornerDownRight className="h-3 w-3 text-ink-tertiary" aria-hidden="true" />}
        <PersonaPill persona={comment.authorPersona} display={comment.authorDisplay} />
        <span className="font-mono text-ink-tertiary">
          {new Date(comment.createdAt).toLocaleString()}
        </span>
        {comment.editedAt && (
          <span className="font-mono text-ink-tertiary italic">(edited)</span>
        )}
        {resolved && (
          <Badge tone="signed" className="text-[10px]">
            <CheckCircle2 className="mr-1 h-3 w-3" aria-hidden="true" /> resolved
          </Badge>
        )}
        <span className="ml-auto flex items-center gap-1">
          <button
            type="button"
            onClick={() => toggleResolve.mutate()}
            disabled={toggleResolve.isPending}
            title={resolved ? 'Mark unresolved' : 'Mark resolved'}
            className="rounded p-1 text-ink-tertiary hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
            data-testid="resolve"
          >
            {resolved ? <RotateCcw className="h-3.5 w-3.5" /> : <Circle className="h-3.5 w-3.5" />}
          </button>
          {!isReply && (
            <button
              type="button"
              onClick={() => setReplying((r) => !r)}
              className="rounded p-1 text-ink-tertiary hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
              data-testid="reply-btn"
            >
              Reply
            </button>
          )}
          {isAuthor && !comment.deleted && (
            <>
              <button
                type="button"
                onClick={() => setEditing((e) => !e)}
                className="rounded p-1 text-ink-tertiary hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
                title="Edit"
                data-testid="edit"
              >
                <Pencil className="h-3.5 w-3.5" />
              </button>
              <button
                type="button"
                onClick={() => remove.mutate()}
                disabled={remove.isPending}
                className="rounded p-1 text-ink-tertiary hover:bg-sunken hover:text-status-failed focus-visible:outline-2 focus-visible:outline-ink-primary"
                title="Delete"
                data-testid="delete"
              >
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            </>
          )}
        </span>
      </header>

      {editing ? (
        <CommentComposer
          initialBody={comment.body}
          onSubmit={async (newBody) => { await edit.mutateAsync(newBody); }}
          onCancel={() => setEditing(false)}
          busy={edit.isPending}
          error={edit.error?.message ?? null}
          autoFocus
          submitLabel="Save"
        />
      ) : (
        <BodyWithMentions body={comment.body} muted={comment.deleted} />
      )}

      {replying && (
        <div className="mt-3 border-l border-border-subtle pl-3">
          <CommentComposer
            placeholder="Reply…"
            onSubmit={async (body) => { onReply(body); setReplying(false); }}
            onCancel={() => setReplying(false)}
            autoFocus
            submitLabel="Reply"
          />
        </div>
      )}
    </article>
  );
}

// ─── Composer with @-autocomplete ────────────────────────────────────

export function CommentComposer({
  placeholder,
  initialBody,
  onSubmit,
  onCancel,
  busy,
  error,
  autoFocus,
  submitLabel = 'Post',
  testid,
}: {
  placeholder?: string;
  initialBody?: string;
  onSubmit: (body: string) => void | Promise<void>;
  onCancel?: () => void;
  busy?: boolean;
  error?: string | null;
  autoFocus?: boolean;
  submitLabel?: string;
  testid?: string;
}) {
  const [value, setValue] = useState(initialBody ?? '');
  const [showSuggest, setShowSuggest] = useState(false);
  const taRef = useRef<HTMLTextAreaElement | null>(null);

  // Detect the @-token under the cursor; suggest the matching personas.
  const suggestion = useMemo(() => {
    if (!showSuggest) return null;
    const caret = taRef.current?.selectionStart ?? value.length;
    const head = value.slice(0, caret);
    const m = /@([a-z]*)$/i.exec(head);
    if (!m) return null;
    const partial = m[1].toLowerCase();
    const matches = PERSONAS.filter((p) => p.startsWith(partial));
    return matches.length === 0 ? null : { partial, matches, start: m.index };
  }, [value, showSuggest]);

  const pick = (p: Persona) => {
    if (!suggestion) return;
    const ta = taRef.current;
    const caret = ta?.selectionStart ?? value.length;
    const head = value.slice(0, suggestion.start);
    const tail = value.slice(caret);
    const next = `${head}@${p} ${tail}`;
    setValue(next);
    setShowSuggest(false);
    requestAnimationFrame(() => {
      const pos = head.length + p.length + 2; // "@<p> "
      ta?.focus();
      ta?.setSelectionRange(pos, pos);
    });
  };

  const submit = async () => {
    const trimmed = value.trim();
    if (!trimmed || busy) return;
    await onSubmit(trimmed);
    if (!initialBody) setValue('');  // reset on a fresh composer
  };

  return (
    <div className="space-y-2" data-testid={testid}>
      <div className="relative">
        <textarea
          ref={taRef}
          value={value}
          onChange={(e) => { setValue(e.target.value); setShowSuggest(true); }}
          onKeyDown={(e) => {
            if (e.key === 'Escape') { setShowSuggest(false); onCancel?.(); }
            if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); void submit(); }
          }}
          onBlur={() => setTimeout(() => setShowSuggest(false), 150)}
          rows={3}
          placeholder={placeholder ?? 'Add a comment…'}
          autoFocus={autoFocus}
          className="block w-full resize-y rounded-md border border-border bg-raised px-3 py-2 text-body text-ink-primary placeholder:text-ink-tertiary focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/20"
          data-testid="comment-body"
        />
        {suggestion && (
          <ul
            role="listbox"
            className="absolute left-3 z-20 mt-1 max-h-40 w-48 overflow-auto rounded-md border border-border bg-raised shadow-e2"
            data-testid="mention-suggest"
          >
            {suggestion.matches.map((p) => (
              <li key={p}>
                <button
                  type="button"
                  onMouseDown={(e) => { e.preventDefault(); pick(p); }}
                  className="block w-full px-3 py-1.5 text-left text-body text-ink-primary hover:bg-sunken focus-visible:outline-2 focus-visible:outline-ink-primary"
                  data-testid={`mention-${p}`}
                >
                  <span className="font-mono text-accent">@{p}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      {error && <p className="text-caption text-status-failed">{error}</p>}

      <div className="flex items-center justify-end gap-2">
        {onCancel && (
          <Button variant="ghost" size="sm" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
        )}
        <Button
          variant="primary"
          size="sm"
          onClick={submit}
          disabled={!value.trim() || busy}
          data-testid="comment-submit"
        >
          {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : null}
          {submitLabel}
        </Button>
      </div>
    </div>
  );
}

// ─── Bits ────────────────────────────────────────────────────────────

function PersonaPill({ persona, display }: { persona: string; display: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-sm bg-sunken px-1.5 py-0.5">
      <span className="font-mono text-[10px] uppercase tracking-wider text-ink-tertiary">{persona}</span>
      <span className="font-medium text-ink-primary">{display}</span>
    </span>
  );
}

const MENTION_RE = /(@(?:engineer|sme|observer|admin))\b/gi;

function BodyWithMentions({ body, muted }: { body: string; muted?: boolean }) {
  const parts = body.split(MENTION_RE);
  return (
    <p className={'whitespace-pre-wrap text-body ' + (muted ? 'italic text-ink-tertiary' : 'text-ink-primary')}>
      {parts.map((part, i) =>
        MENTION_RE.test(part)
          ? <span key={i} className="font-mono font-medium text-accent">{part}</span>
          : <span key={i}>{part}</span>,
      )}
    </p>
  );
}

type Node = { comment: CommentItem; replies: CommentItem[] };

function buildTree(items: CommentItem[]): Node[] {
  // Top-level = no parent. Children get attached to their nearest ancestor that IS top-level.
  const topLevel: CommentItem[] = [];
  const byId = new Map<string, CommentItem>();
  for (const c of items) byId.set(c.id, c);
  for (const c of items) {
    if (!c.parentCommentId) topLevel.push(c);
  }
  const nodes: Node[] = topLevel.map((c) => ({ comment: c, replies: [] }));
  const nodeById = new Map(nodes.map((n) => [n.comment.id, n]));
  for (const c of items) {
    if (!c.parentCommentId) continue;
    // Walk up to find a top-level ancestor.
    let pid: string | null = c.parentCommentId;
    let safety = 8;
    while (pid && safety-- > 0) {
      const node = nodeById.get(pid);
      if (node) { node.replies.push(c); break; }
      pid = byId.get(pid)?.parentCommentId ?? null;
    }
  }
  // Sort replies by createdAt asc.
  for (const n of nodes) n.replies.sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  return nodes;
}
