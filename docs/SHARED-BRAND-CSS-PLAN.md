# Shared brand CSS — single source of truth (plan + decision needed)

_Status: Conduit-side single-source DONE. Cross-repo single-source = needs Jacob's call._

## Goal (Jacob)
> "Using global css that everyone can see, both [IdentityCenter and Conduit] use the
> same css ... so we can change things in a single place for my entire brand. I want
> all of Conduit to look like that."

One brand design system. Change a colour in one place; both products update.

## What was found (2026-06-20)

| | IdentityCenter | Conduit |
|---|---|---|
| Design-system file | `WebPortal/wwwroot/css/ic-design-system.css` (2357 ln) — tokens + reset + components, scoped to `.ic-shell` | `wwwroot/css/conduit.css` (3582 ln) + `conduit-design.css` (453 ln, cc-* components) |
| Token names | `--sec-identity`, `--bg`, `--surface`, `--text-primary/secondary/tertiary` | `--accent`, `--bg`, `--panel`, `--text / --text-2 / --text-3` |
| Token VALUES | the source of truth | **mostly identical** to IC (`--bg #0B1424`, text `#E4EDFB / #9DB0CC`) |
| Section-accent ramp | `--sec-*` | was hand-MIRRORED into `conduit-design.css` and had **drifted** (`--sec-governance`, `--sec-analytics` no longer matched IC) |
| Shared RCL | — | `Conduit.Shared.SyncUI` exists, but carries only the ScopeTree component; **no shared CSS**, and IC references it via a brittle sibling path (already flagged in memory) |

**Conclusion:** same brand, two variable vocabularies, and a mirror that already drifted. The hand-copy approach is exactly what's failing.

## DONE now (Conduit-side, low-risk, shipped)
- Extracted every brand variable into **`wwwroot/css/brand-tokens.css`** — the single source of truth for Conduit's brand.
- Linked it FIRST in `_Layout.cshtml` (before Bootstrap / conduit-design.css / conduit.css).
- Removed the duplicate `:root` blocks from `conduit.css` and `conduit-design.css` (they now only consume).
- Re-aligned the drifted section-accent ramp to IC's values, and exposed BOTH name sets: `--sec-*` (IC names — so IC-ported markup just works) and `--cc-sec-*` aliases (back-compat).
- Non-breaking: same values, just relocated + load-ordered. Verified by build (no CS/RZ errors).

Result: **within Conduit, the brand is now changeable in one file.** `brand-tokens.css` is authored so it can be lifted verbatim into a shared location with no edits.

## NOT done — needs Jacob's decision (cross-repo single-source)

To make **IdentityCenter consume the exact same file**, the token file must live where BOTH repos reference it at build/publish time. Three options, with trade-offs:

### Option A — ship `brand-tokens.css` from the shared RCL `wwwroot`
- Move the file to `Conduit.Shared.SyncUI/wwwroot/brand-tokens.css`; both apps `<link>` `_content/Conduit.Shared.SyncUI/brand-tokens.css`.
- **Pro:** true single file; real build-time sharing; matches the existing Option-A RCL pilot.
- **Con:** makes **IC depend on a Conduit project via the brittle sibling-path** that has already burned us. If Conduit moves on disk, IC's build breaks. Cross-repo build coupling.

### Option B — standalone shared brand package (own repo or NuGet)
- A tiny `Brand.Css` repo/package both consume.
- **Pro:** cleanest long-term; no sibling-path; versioned; decouples the two products.
- **Con:** real infrastructure — a third repo or a package feed, and it changes how both products build/ship. Most setup cost.

### Option C — documented sync of `brand-tokens.css` (one file, copied)
- `brand-tokens.css` is the source of truth; a documented step copies it into IC.
- **Pro:** zero build coupling; trivial.
- **Con:** "single source" by discipline only — this is the hand-copy that already drifted. Lowest integrity.

### Plus: the name-vocabulary reconciliation (applies to A and B)
The shared file must expose BOTH name sets so neither product's existing CSS breaks:
- IC names: `--sec-*`, `--text-primary/secondary/tertiary`, `--surface*`
- Conduit names: `--accent`, `--text / --text-2 / --text-3`, `--panel*`
`brand-tokens.css` already aliases the section-accent ramp both ways; extending it to the full surface/text vocabulary is ~30 alias lines, but should be done as part of whichever option is chosen so IC adopts it cleanly.

## Recommendation
**Option B** for the end state (clean, decoupled, versioned), but it's the most setup. If Jacob wants the win sooner and accepts the sibling-path fragility he's already flagged, **Option A** delivers true single-source today using the RCL that already exists. **Option C is not recommended** — it's the drift we just removed.

**Decision Jacob must make:** A, B, or C — i.e. how much build coupling between IC and Conduit he'll accept in exchange for one-file brand control. Nothing further should be built cross-repo until he picks.
