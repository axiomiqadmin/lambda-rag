"""Assemble the chapter markdown files into one policy PDF (and a combined .md).

Usage: python scripts/build_policy_pdf.py <src-dir> <out-pdf> [--title "..."]

Deterministic: chapters are ordered by file name, no timestamps are embedded
(reportlab invariant mode), so identical inputs give identical bytes.
"""
import argparse
import glob
import os
import re
from xml.sax.saxutils import escape

from reportlab.lib.pagesizes import LETTER
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import inch
from reportlab.platypus import ListFlowable, ListItem, PageBreak, Paragraph, SimpleDocTemplate, Spacer


def build(src_dir, out_pdf, title):
    files = sorted(glob.glob(os.path.join(src_dir, "[0-9][0-9]-*.md")))
    if not files:
        raise SystemExit(f"no chapter files in {src_dir}")
    ss = getSampleStyleSheet()
    h0 = ParagraphStyle("h0", parent=ss["Title"], fontSize=24, spaceAfter=18)
    h1 = ParagraphStyle("h1", parent=ss["Heading1"], fontSize=18, spaceBefore=6, spaceAfter=10)
    h2 = ParagraphStyle("h2", parent=ss["Heading2"], fontSize=14, spaceBefore=10, spaceAfter=6)
    h3 = ParagraphStyle("h3", parent=ss["Heading3"], fontSize=12, spaceBefore=8, spaceAfter=4)
    body = ParagraphStyle("body", parent=ss["BodyText"], fontSize=10.5, leading=14, spaceAfter=6)

    story = [Spacer(1, 2 * inch), Paragraph(escape(title), h0),
             Paragraph("Distilled from Microsoft Azure Well-Architected Framework, Cloud Adoption "
                       "Framework and Azure Architecture Center guidance. Requirements use SHALL / MUST / "
                       "SHOULD to state obligations; each chapter lists its sources.", body),
             PageBreak()]
    combined = [f"# {title}\n"]

    for path in files:
        with open(path, encoding="utf-8") as fh:
            text = fh.read().replace("\r\n", "\n")
        combined.append(text.rstrip() + "\n")
        bullets = []

        def flush():
            if bullets:
                story.append(ListFlowable([ListItem(Paragraph(escape(b), body)) for b in bullets],
                                          bulletType="bullet", leftIndent=14))
                bullets.clear()

        for line in text.split("\n"):
            if line.startswith("- "):
                bullets.append(line[2:].strip())
                continue
            flush()
            if not line.strip():
                continue
            m = re.match(r"(#{1,3})\s+(.*)", line)
            if m:
                level = len(m.group(1))
                if level == 1 and story and not isinstance(story[-1], PageBreak):
                    story.append(PageBreak())
                story.append(Paragraph(escape(m.group(2)), {1: h1, 2: h2, 3: h3}[level]))
            else:
                story.append(Paragraph(escape(line.strip()), body))
        flush()

    doc = SimpleDocTemplate(out_pdf, pagesize=LETTER, title=title, author="LambdaRag",
                            invariant=1, leftMargin=inch, rightMargin=inch,
                            topMargin=inch, bottomMargin=inch)
    doc.build(story)
    with open(os.path.splitext(out_pdf)[0] + ".md", "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(combined))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("src_dir")
    ap.add_argument("out_pdf")
    ap.add_argument("--title", default="Azure Recommended Practices Policy")
    a = ap.parse_args()
    build(a.src_dir, a.out_pdf, a.title)
