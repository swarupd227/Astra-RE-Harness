import type { ArtefactUpload } from '@/lib/api';

export function formatBytes(n: number | null | undefined): string {
  if (n == null) return '—';
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(n < 10 * 1024 ? 1 : 0)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(n < 10 * 1024 * 1024 ? 1 : 0)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

/** Files from a file or folder picker. A folder pick keeps its folder structure. */
export function fromPicker(list: FileList | null): ArtefactUpload[] {
  if (!list) return [];
  return Array.from(list).map((file) => ({
    file,
    path: file.webkitRelativePath || file.name,
  }));
}

async function readEntry(entry: FileSystemEntry, out: ArtefactUpload[]): Promise<void> {
  if (entry.isFile) {
    const file = await new Promise<File>((resolve, reject) =>
      (entry as FileSystemFileEntry).file(resolve, reject),
    );
    out.push({ file, path: entry.fullPath.replace(/^\/+/, '') });
    return;
  }
  if (!entry.isDirectory) return;
  const reader = (entry as FileSystemDirectoryEntry).createReader();
  // readEntries hands back a batch at a time and signals the end with an empty one.
  for (;;) {
    const batch = await new Promise<FileSystemEntry[]>((resolve, reject) =>
      reader.readEntries(resolve, reject),
    );
    if (batch.length === 0) break;
    for (const child of batch) await readEntry(child, out);
  }
}

/**
 * Everything in a drop, folders included, each with its path inside the dropped folder.
 * The entries must be taken from the event synchronously, before the first await.
 */
export async function fromDrop(dt: DataTransfer): Promise<ArtefactUpload[]> {
  const entries: FileSystemEntry[] = [];
  const loose: File[] = [];
  for (const item of Array.from(dt.items)) {
    if (item.kind !== 'file') continue;
    const entry = item.webkitGetAsEntry?.();
    if (entry) entries.push(entry);
    else {
      const f = item.getAsFile();
      if (f) loose.push(f);
    }
  }
  const out: ArtefactUpload[] = loose.map((file) => ({ file, path: file.name }));
  for (const entry of entries) await readEntry(entry, out);
  return out;
}
