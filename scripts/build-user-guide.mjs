// Builds user-guide.pdf from user-guide.md.
//
// Renders the Markdown to a styled HTML document (with GitHub-style heading
// anchors so the in-document Table of Contents links resolve), then prints it
// to PDF with headless Chrome — matching how the original guide was produced
// (the committed PDF reports Creator "Chromium" / Producer "Skia/PDF").
//
// Usage:  node scripts/build-user-guide.mjs
//
// Requires: `marked` (resolved from web/node_modules) and Google Chrome.

import { readFileSync, writeFileSync, unlinkSync, existsSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';

const repoRoot = dirname(dirname(fileURLToPath(import.meta.url)));
const mdPath = join(repoRoot, 'user-guide.md');
const pdfPath = join(repoRoot, 'user-guide.pdf');
const tmpHtmlPath = join(repoRoot, 'user-guide.tmp.html');

// marked 18 is ESM-only; resolve it from the web workspace where it's installed.
const { marked } = await import(
  pathToFileURL(join(repoRoot, 'web', 'node_modules', 'marked', 'lib', 'marked.esm.js')).href
);

// --- GitHub-style heading slugs ----------------------------------------------
function slug(text) {
  return text
    .replace(/<[^>]+>/g, '')          // strip any inline HTML tags
    // decode the entities marked emits in text, so e.g. User&#39;s -> User's
    .replace(/&#39;/g, "'").replace(/&amp;/g, '&')
    .replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .trim()
    .toLowerCase()
    .replace(/[^\w\s-]/g, '')         // drop punctuation (., (), —, ', etc.)
    .replace(/\s/g, '-');             // each space -> a hyphen (GitHub: no collapsing)
}

const md = readFileSync(mdPath, 'utf8');
let html = marked.parse(md, { gfm: true, breaks: false });

// Inject id attributes on headings so #anchor TOC links work in the PDF.
const seen = new Map();
html = html.replace(/<h([1-6])>([\s\S]*?)<\/h\1>/g, (_m, level, inner) => {
  let id = slug(inner);
  if (seen.has(id)) {
    const n = seen.get(id) + 1;
    seen.set(id, n);
    id = `${id}-${n}`;
  } else {
    seen.set(id, 0);
  }
  return `<h${level} id="${id}">${inner}</h${level}>`;
});

const css = `
  @page { size: A4; margin: 18mm 16mm; }
  * { box-sizing: border-box; }
  body {
    font-family: -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    font-size: 11pt; line-height: 1.55; color: #1f2328;
    max-width: 100%; margin: 0; padding: 0;
    -webkit-print-color-adjust: exact; print-color-adjust: exact;
  }
  h1, h2, h3, h4 { line-height: 1.25; font-weight: 600; margin: 1.4em 0 0.5em; }
  h1 { font-size: 22pt; }
  h2 { font-size: 16pt; border-bottom: 1px solid #d0d7de; padding-bottom: 0.25em; page-break-before: auto; }
  h3 { font-size: 13pt; }
  h4 { font-size: 11.5pt; }
  h2, h3, h4 { page-break-after: avoid; }
  p, ul, ol, table, pre, blockquote { margin: 0 0 0.85em; }
  a { color: #0969da; text-decoration: none; }
  code { font-family: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
         font-size: 85%; background: #eff1f3; padding: 0.15em 0.35em; border-radius: 4px; }
  pre { background: #f6f8fa; padding: 12px 14px; border-radius: 6px; overflow: auto;
        font-size: 9pt; line-height: 1.45; }
  pre code { background: none; padding: 0; font-size: inherit; }
  table { border-collapse: collapse; width: 100%; font-size: 10pt; }
  th, td { border: 1px solid #d0d7de; padding: 6px 10px; text-align: left; vertical-align: top; }
  th { background: #f6f8fa; font-weight: 600; }
  tr:nth-child(even) td { background: #fbfcfd; }
  blockquote { border-left: 4px solid #d0d7de; color: #57606a; padding: 0.2em 1em; margin-left: 0; }
  blockquote > :last-child { margin-bottom: 0; }
  img { max-width: 100%; height: auto; border: 1px solid #d0d7de; border-radius: 6px;
        display: block; margin: 0.5em 0; page-break-inside: avoid; }
  hr { border: none; border-top: 1px solid #d0d7de; margin: 1.6em 0; }
  table, blockquote, pre { page-break-inside: avoid; }
`;

// <base href> lets relative image paths (docs/screenshots/...) resolve to the repo root.
const baseHref = pathToFileURL(repoRoot + '/').href;
const doc = `<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<base href="${baseHref}">
<title>FormForge User Guide</title>
<style>${css}</style>
</head><body>${html}</body></html>`;

writeFileSync(tmpHtmlPath, doc, 'utf8');

// --- Locate Chrome -----------------------------------------------------------
const candidates = [
  process.env.CHROME_PATH,
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'Google/Chrome/Application/chrome.exe'),
].filter(Boolean);
const chrome = candidates.find((p) => existsSync(p));
if (!chrome) {
  throw new Error('Chrome not found. Set CHROME_PATH to your chrome.exe.');
}

const args = [
  '--headless=new',
  '--disable-gpu',
  '--no-pdf-header-footer',
  '--no-margins',
  `--print-to-pdf=${pdfPath}`,
  pathToFileURL(tmpHtmlPath).href,
];

const res = spawnSync(chrome, args, { stdio: 'inherit' });
unlinkSync(tmpHtmlPath);

if (res.status !== 0) {
  throw new Error(`Chrome exited with status ${res.status}`);
}
console.log(`Wrote ${pdfPath}`);
