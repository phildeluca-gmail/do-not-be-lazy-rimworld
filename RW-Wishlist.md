<!-- Capture file for RimWorld mod ideas that are not yet architected. Created 2026-08-23. Not required session reading - see CLAUDE.md. -->

# RimWorld Wishlist

Ideas parked here rather than in an architecture document, because
nothing below has been designed, costed or agreed. **Nothing in this
file is a go-ahead.** An entry graduates by being written up in the
relevant architecture doc first.

Each entry records what was asked for verbatim, then what that appears
to mean against the code as it stands. Where the second part is a guess,
it says so - do not build from a guess.

---

## 1. ConfigureKeys - interface changes

**Asked for, verbatim:** "ConfigureKeys - interface changes".

**Status: captured, not understood. Needs the requester to expand it
before anything else happens.** Recorded now so it isn't lost.

There is no `ConfigureKeys` anywhere in this repo or in either
architecture document - grepped 2026-08-23 across `.md`, `.cs` and
`.xml`. So this is new, and the name is doing all the work.

Two readings, and they lead to completely different pieces of work:

1. **Key bindings for Do Not Be Lazy.** Every `*` sweep is a
   right-click float menu entry today; there is no keyboard path to any
   of them. RimWorld's own mechanism is a `KeyBindingDef` in XML plus a
   `KeyBindingDefOf` lookup, surfaced under Options > Key Bindings for
   free. A hotkey that repeats the last sweep, or one per common sweep
   type, would fit that. This is the reading the mod's shape suggests.
2. **A separate mod about configuring keys**, i.e. the interface for key
   binding itself rather than any of our features. That would be a third
   project alongside Do Not Be Lazy and Do Not Freak Out.

"interface changes" is equally open - it could mean the key-binding UI,
the mod settings window (`DoNotBeLazySettings.DoWindowContents` is a
bare `Listing_Standard` today), the float menu itself, or an overlay.

**Related things already known, so they don't get re-derived:**

- `showSweepOverlay` is a settings checkbox that **does nothing** -
  confirmed by grep, recorded in architecture doc 3.4. If "interface
  changes" includes drawing the sweep radius, that dead checkbox is
  where it starts, and it already implies a promise to the player that
  the mod doesn't keep.
- The float menu path is the most-touched interface surface in the mod
  and the least traced - see the "float-menu path is still mostly
  untraced" note in `NEXT_SESSION.md`.
- Open items 4 and 5 in `NEXT_SESSION.md` are both float-menu interface
  bugs (drafted right-click no longer moves pawns; nonsense entries on a
  workbench). Whatever "interface changes" turns out to mean, those two
  are probably ahead of it in the queue.

**Questions to settle before this becomes a plan:**

- Is this Do Not Be Lazy, or a new mod?
- Keyboard shortcuts, on-screen UI, or both?
- If shortcuts: what does a key press act on - the pawn's current
  selection and the cell under the cursor, the way a right-click does?

---

## 2. Do Not Be Lazy - focus from centre out

**Asked for, verbatim:** "DoNotBeLazy focus from center out".

**Status: understood well enough to describe, not designed.** This one
maps onto a specific line of existing code.

**What happens now.** `SweepManager.AssignNextTask` picks each pawn's
next target with `NearestTargetIndex(pawn.Position, order.SharedPool,
refused)` - nearest to **the pawn**, every time. The clicked cell is
kept on the order as `ScanCenter` and used only to build the pool and to
rescan; it never influences which target comes next.

**What "centre out" would mean.** Order the pool by distance from
`order.ScanCenter` instead, so the group clears the middle of the radius
first and works outward in rings, rather than each pawn greedily
grabbing whatever is under their feet.

**Why it is worth doing.** With nearest-to-pawn, a group spreads across
the whole radius immediately and the area finishes everywhere at once
and nowhere first. Centre-out finishes a growing, contiguous area, which
is what the player is usually picturing when they click a spot and say
"until done" - and it makes progress legible, which the mod currently
has no way to show.

**Why it isn't a one-line change:**

- **It fights walking distance.** Strict centre-out can send a pawn
  across the map to the next ring while a target sits beside them. The
  real design question is the *weighting* between distance-from-centre
  and distance-from-pawn, not which one wins. A cheap first cut: sort by
  distance from centre, but let a pawn take anything within a few tiles
  of itself first.
- **The pool is shared and mutated.** Since 2026-08-22 a refused target
  stays in the pool, so the ordering has to tolerate targets that are
  passed over and revisited. A pre-sorted list would need re-sorting
  after every rescan; computing the distance on demand (as
  `NearestTargetIndex` does now) is simpler and probably still cheap.
- **Rescans move the goalposts.** New work found by a rescan is scanned
  from `ScanCenter`, so it slots into the ring order naturally - that
  part is fine.
- **It interacts with `showSweepOverlay`.** If the radius is ever drawn,
  centre-out is the behaviour that makes the drawing worth looking at.
  Worth doing the two together.

**Where it would go:** `NearestTargetIndex` and its call site in
`AssignNextTask`, `SweepManager.cs`. Nothing else needs to know.

**Do not build this without a decision on the weighting.** Strict
centre-out is easy and probably wrong; the weighted version is the
actual feature, and picking the weight is the design work.
