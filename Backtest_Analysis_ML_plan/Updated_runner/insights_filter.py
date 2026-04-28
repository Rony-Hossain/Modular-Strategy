"""
Relevance-scored insight filtering.

Replaces the chronological "include everything until budget" approach
with a per-phase relevance filter. Each phase only sees insights that
matter for it.

Selection priority:
  1. Always: anomalies and explicit FOLLOW-UP markers (high-signal)
  2. Always: insights from explicit phase dependencies
  3. Then: keyword-scored matches against the current phase's spec
  4. Backfill: most recent untagged insights, until budget hit

Saves tokens AND improves planning by removing noise. By Phase 16 the
old approach was dumping 50+ findings into context, most irrelevant.
"""

import re
from typing import List, Set


def filter_insights(
    accumulated_insights: List[str],
    current_phase_id: str,
    phase_dependencies: List[str],
    phase_spec_text: str = "",
    max_chars: int = 3000,
) -> str:
    """
    Return a curated insights block for the current phase's prompt.

    Args:
        accumulated_insights: list of insight lines, e.g.
            ["[Phase 11] Replay PnL matches backtest within 0.3%",
             "[Phase 12 ANOMALY] SMF_Impulse score correlation reversed",
             ...]
        current_phase_id: the phase being planned (e.g. "13")
        phase_dependencies: phase_ids this phase depends on
        phase_spec_text: the plan doc for this phase, used for keyword scoring
        max_chars: budget for the returned block
    """
    if not accumulated_insights:
        return ""

    # Tier 1: high-signal lines (anomalies, follow-ups) — always include
    tier1 = [
        ln for ln in accumulated_insights
        if "ANOMALY" in ln or "FOLLOW-UP" in ln
    ]

    # Tier 2: insights from this phase's dependencies
    dep_pattern = re.compile(
        r"\[Phase (" + "|".join(re.escape(d) for d in phase_dependencies) + r")\b"
    ) if phase_dependencies else None
    tier2 = []
    if dep_pattern:
        tier2 = [
            ln for ln in accumulated_insights
            if dep_pattern.search(ln) and ln not in tier1
        ]

    # Tier 3: keyword-scored relevance for the rest
    keywords = _extract_keywords(phase_spec_text)
    rest = [ln for ln in accumulated_insights
            if ln not in tier1 and ln not in tier2]

    scored = []
    for ln in rest:
        score = sum(1 for kw in keywords if kw in ln.lower())
        if score > 0:
            scored.append((score, ln))
    scored.sort(key=lambda x: -x[0])
    tier3 = [ln for _, ln in scored]

    # Tier 4: chronologically recent untagged insights as backfill
    tier4 = [ln for ln in reversed(rest) if ln not in tier3]

    # Build the output, respecting budget
    selected: List[str] = []
    used = 0
    for tier_name, tier in [
        ("ANOMALIES & FOLLOW-UPS", tier1),
        ("FROM DEPENDENCIES", tier2),
        ("KEYWORD-MATCHED", tier3),
        ("RECENT", tier4),
    ]:
        if not tier:
            continue
        # Spend up to half remaining budget on each tier
        budget_remaining = max_chars - used
        if budget_remaining <= 0:
            break
        for ln in tier:
            ln_len = len(ln) + 1
            if used + ln_len > max_chars:
                break
            selected.append(ln)
            used += ln_len

    if not selected:
        return ""

    dropped = len(accumulated_insights) - len(selected)
    header = (
        f"# Relevant insights from prior phases "
        f"({len(selected)} kept, {dropped} dropped as not relevant to phase "
        f"{current_phase_id} — anomalies always retained)\n"
    )
    return header + "\n".join(selected)


def _extract_keywords(text: str) -> Set[str]:
    """
    Pull distinctive nouns from a phase's spec. Lowercase. Skips stopwords.
    Heuristic — doesn't have to be perfect, just better than nothing.
    """
    if not text:
        return set()
    # Take the first 2k chars to keep it cheap
    sample = text[:2000].lower()
    # Word tokens of length >= 4, alphabetic
    tokens = re.findall(r"\b[a-z][a-z_]{3,}\b", sample)
    stopwords = {
        "this", "that", "with", "from", "have", "been", "will", "must",
        "should", "would", "could", "into", "than", "then", "they", "them",
        "their", "there", "where", "when", "what", "which", "while", "also",
        "only", "more", "most", "many", "such", "some", "any", "all", "for",
        "and", "but", "not", "are", "is", "the", "a", "an", "an", "of", "to",
        "in", "on", "by", "at", "as", "or", "if", "be", "do", "does",
        "phase", "script", "data", "file", "files", "input", "output",
        "save", "saved", "result", "check", "print", "plan", "section",
        "value", "values", "use", "used", "using", "make", "find",
    }
    return {t for t in tokens if t not in stopwords}
