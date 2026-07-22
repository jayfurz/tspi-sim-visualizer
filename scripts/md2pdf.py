#!/usr/bin/env python3
"""Render a repo markdown doc to PDF with PyMuPDF (import fitz) — no doc toolchain.

Built to ship docs/ICD-NN.md to SMEs as a PDF: a small converter covers exactly
the markdown these docs use (#..#### headings, pipe tables with \\| escapes,
fenced code, bullet/numbered lists with wrapped continuation lines, **bold**,
*italic*, `code`, [links](url)), fitz.Story lays it out on A4, and a second
pass stamps page numbers. Non-Latin glyphs the base-14 fonts lack (box-drawing
tree characters, combining hats, arrows) are transliterated to ASCII.

The PDF is a regenerable artifact and is git-ignored — never commit one:

    .venv/bin/python scripts/md2pdf.py                     # docs/ICD-NN.md -> docs/ICD-NN.pdf
    .venv/bin/python scripts/md2pdf.py other.md out.pdf
"""

import re
import sys
from pathlib import Path

import fitz

# Glyphs outside base-14 (Helvetica/Courier) coverage -> ASCII stand-ins.
TRANSLIT = str.maketrans({
    "│": "|", "├": "+", "└": "+", "─": "-",   # │ ├ └ ─ tree
    "̂": "^",                                                # combining hat: r̂ -> r^
    "ω": "omega", "θ": "theta", "σ": "sigma",
    "→": "->", "←": "<-", "↑": "up", "↓": "down", "↔": "<->", "⇄": "<->", "∣": "|",
    "≤": "<=", "≥": ">=", "≠": "!=", "−": "-",
    "✓": "ok", "ᵀ": "'",                                # ✓, superscript T
})

CSS = """
body { font-family: sans-serif; font-size: 10px; line-height: 1.4; }
h1 { font-size: 18px; }
h2 { font-size: 14px; margin-top: 16px; }
h3 { font-size: 12px; margin-top: 13px; }
h4 { font-size: 10.5px; margin-top: 11px; }
p { margin: 5px 0; }
li { margin: 3px 0 3px 14px; }
/* No background-color anywhere: fitz.Story replays background fill rects on
   later pages (phantom gray bars), so shading is done with borders only. */
pre { font-family: monospace; font-size: 8.6px; border: 0.5px solid #bbb;
      padding: 6px 8px; margin: 6px 0; }
code { font-family: monospace; font-size: 9px; color: #1d3a5f; }
table { border-collapse: collapse; margin: 7px 0; }
th, td { border: 0.5px solid #999; padding: 3px 6px; font-size: 8.8px;
         vertical-align: top; text-align: left; }
a { color: #1a56b0; }
"""


def esc(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def inline(s: str) -> str:
    """Inline markdown -> HTML; backtick spans are protected from */[ rewriting."""
    s = esc(s)
    spans: list[str] = []

    def stash(m):
        spans.append(m.group(1))
        return f"\x00{len(spans) - 1}\x00"

    s = re.sub(r"`([^`]+)`", stash, s)
    s = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", s)
    s = re.sub(r"(?<![\w*])\*([^*\n]+?)\*(?![\w*])", r"<i>\1</i>", s)
    s = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r'<a href="\2">\1</a>', s)
    return re.sub(r"\x00(\d+)\x00", lambda m: f"<code>{spans[int(m.group(1))]}</code>", s)


def cells(row: str) -> list[str]:
    row = row.strip()
    if row.startswith("|"):
        row = row[1:]
    if row.endswith("|") and not row.endswith("\\|"):
        row = row[:-1]
    return [c.strip().replace("\\|", "|") for c in re.split(r"(?<!\\)\|", row)]


def md_to_html(md: str) -> str:
    out: list[str] = []
    para: list[str] = []
    lines = md.translate(TRANSLIT).split("\n")

    def flush_para():
        if para:
            out.append("<p>" + inline(" ".join(para)) + "</p>")
            para.clear()

    i = 0
    while i < len(lines):
        line = lines[i]

        if line.startswith("```"):                     # fenced code
            flush_para()
            block = []
            i += 1
            while i < len(lines) and not lines[i].startswith("```"):
                block.append(lines[i])
                i += 1
            out.append("<pre>" + esc("\n".join(block)) + "</pre>")
        elif line.lstrip().startswith("|"):            # pipe table
            flush_para()
            rows = []
            while i < len(lines) and lines[i].lstrip().startswith("|"):
                rows.append(cells(lines[i]))
                i += 1
            i -= 1
            body = [r for r in rows[1:] if not all(re.fullmatch(r":?-+:?", c) for c in r)]
            out.append("<table><tr>" + "".join(f"<th>{inline(c)}</th>" for c in rows[0]) + "</tr>")
            out.extend("<tr>" + "".join(f"<td>{inline(c)}</td>" for c in r) + "</tr>" for r in body)
            out.append("</table>")
        elif m := re.match(r"(#{1,4})\s+(.*)", line):   # heading
            flush_para()
            n = len(m.group(1))
            out.append(f"<h{n}>{inline(m.group(2))}</h{n}>")
        elif re.match(r"[-\d]", line) and (m := re.match(r"(?:-|\d+\.)\s+(.*)", line)):
            flush_para()                                # list block (bullet or numbered)
            tag = "ul" if line.startswith("-") else "ol"
            out.append(f"<{tag}>")
            while i < len(lines) and (m := re.match(r"(?:-|\d+\.)\s+(.*)", lines[i])):
                item = [m.group(1)]
                while i + 1 < len(lines) and re.match(r"\s+\S", lines[i + 1]) \
                        and not re.match(r"\s*(?:-|\d+\.)\s", lines[i + 1]):
                    i += 1
                    item.append(lines[i].strip())       # wrapped continuation line
                out.append("<li>" + inline(" ".join(item)) + "</li>")
                i += 1
            i -= 1
            out.append(f"</{tag}>")
        elif not line.strip():
            flush_para()
        else:
            para.append(line.strip())
        i += 1

    flush_para()
    return "<body>" + "\n".join(out) + "</body>"


def render(md_path: Path, pdf_path: Path) -> int:
    html = md_to_html(md_path.read_text(encoding="utf-8"))
    story = fitz.Story(html=html, user_css=CSS)
    mediabox = fitz.paper_rect("a4")
    where = mediabox + (40, 36, -40, -44)               # margins; bottom row for footer
    writer = fitz.DocumentWriter(str(pdf_path))
    more = 1
    while more:
        dev = writer.begin_page(mediabox)
        more, _ = story.place(where)
        story.draw(dev)
        writer.end_page()
    writer.close()

    doc = fitz.open(str(pdf_path))                      # second pass: footer + metadata
    title = md_path.name
    for page in doc:
        page.insert_text(
            fitz.Point(40, mediabox.height - 18),
            f"{title}  -  page {page.number + 1} / {doc.page_count}",
            fontsize=7, color=(0.45, 0.45, 0.45))
    doc.set_metadata({"title": title, "producer": "scripts/md2pdf.py (PyMuPDF)"})
    doc.saveIncr()
    pages = doc.page_count
    doc.close()
    return pages


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    md = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "docs" / "ICD-NN.md"
    pdf = Path(sys.argv[2]) if len(sys.argv) > 2 else md.with_suffix(".pdf")
    pages = render(md, pdf)
    print(f"wrote {pdf} ({pages} pages) — regenerable artifact, do not commit")


if __name__ == "__main__":
    main()
