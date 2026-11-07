/**
 * Small real files for the specs: a playable WAV (the browser really loads and seeks it) and a Word
 * file built by hand (a stored zip with the three parts the adapter reads), so no fixture has to be
 * committed as a binary and nothing needs a library.
 */
import { crc32 } from 'node:zlib';

/** Silence: 8 kHz, 8-bit, mono PCM. `seconds` long, 8 KB a second. */
export function wavBytes(seconds: number, rate = 8000): Buffer {
  const n = Math.round(seconds * rate);
  const h = Buffer.alloc(44);
  h.write('RIFF', 0);
  h.writeUInt32LE(36 + n, 4);
  h.write('WAVE', 8);
  h.write('fmt ', 12);
  h.writeUInt32LE(16, 16);
  h.writeUInt16LE(1, 20); // PCM
  h.writeUInt16LE(1, 22); // mono
  h.writeUInt32LE(rate, 24);
  h.writeUInt32LE(rate, 28); // bytes per second
  h.writeUInt16LE(1, 32); // block align
  h.writeUInt16LE(8, 34); // bits per sample
  h.write('data', 36);
  h.writeUInt32LE(n, 40);
  return Buffer.concat([h, Buffer.alloc(n, 0x80)]);
}

type ZipEntry = { name: string; data: Buffer };

/** A zip with every entry stored (no compression). */
export function zipStored(entries: ZipEntry[]): Buffer {
  const parts: Buffer[] = [];
  const central: Buffer[] = [];
  let offset = 0;
  for (const e of entries) {
    const name = Buffer.from(e.name, 'utf8');
    const crc = crc32(e.data);

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6); // names are UTF-8
    local.writeUInt16LE(0, 8); // stored
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(0x21, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(e.data.length, 18);
    local.writeUInt32LE(e.data.length, 22);
    local.writeUInt16LE(name.length, 26);
    parts.push(local, name, e.data);

    const entry = Buffer.alloc(46);
    entry.writeUInt32LE(0x02014b50, 0);
    entry.writeUInt16LE(20, 4);
    entry.writeUInt16LE(20, 6);
    entry.writeUInt16LE(0x0800, 8);
    entry.writeUInt16LE(0, 10);
    entry.writeUInt16LE(0, 12);
    entry.writeUInt16LE(0x21, 14);
    entry.writeUInt32LE(crc, 16);
    entry.writeUInt32LE(e.data.length, 20);
    entry.writeUInt32LE(e.data.length, 24);
    entry.writeUInt16LE(name.length, 28);
    entry.writeUInt32LE(offset, 42);
    central.push(entry, name);

    offset += 30 + name.length + e.data.length;
  }
  const size = central.reduce((n, b) => n + b.length, 0);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(size, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...parts, ...central, end]);
}

const W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main';
const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

export type DocxBlock = { h?: 1 | 2 | 3; text: string };

/** A Word file with real Heading 1–3 styles: `{ h: 1, text }` is a heading, anything else a paragraph. */
export function docxBytes(blocks: DocxBlock[]): Buffer {
  const body = blocks
    .map((b) =>
      b.h
        ? `<w:p><w:pPr><w:pStyle w:val="Heading${b.h}"/></w:pPr><w:r><w:t>${esc(b.text)}</w:t></w:r></w:p>`
        : `<w:p><w:r><w:t>${esc(b.text)}</w:t></w:r></w:p>`,
    )
    .join('');
  const styles = [1, 2, 3]
    .map((n) => `<w:style w:type="paragraph" w:styleId="Heading${n}"><w:name w:val="heading ${n}"/></w:style>`)
    .join('');
  return zipStored([
    { name: '[Content_Types].xml', data: Buffer.from('<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>') },
    { name: 'word/document.xml', data: Buffer.from(`<w:document xmlns:w="${W}"><w:body>${body}</w:body></w:document>`) },
    { name: 'word/styles.xml', data: Buffer.from(`<w:styles xmlns:w="${W}">${styles}</w:styles>`) },
  ]);
}
