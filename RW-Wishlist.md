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

## 2. Do Not Be Lazy - focus from centre out - **GRADUATED 2026-08-27**

**Asked for, verbatim:** "DoNotBeLazy focus from center out".

**Status: graduated and implemented.** Ordered on 2026-08-27 as "the
work should emanate from my mouse click", written up as TOP PRIORITY
item A in `DoNotBeLazy_Architecture.md` section 0, and implemented the
same day in `SweepManager.NextTargetIndex`. It is no longer wishlist and
this entry is kept only so the reasoning below isn't re-derived.

**How the open design question was settled.** This entry said the
weighting between distance-from-centre and distance-from-pawn was the
real design work, and that "strict centre-out is easy and probably
wrong". It was built strict anyway, on the user's explicit instruction:
the click is an order, and a blend makes the result less predictable
rather than more. Distance from `ScanCenter` ranks; distance from the
pawn only breaks ties.

**The objection this entry raised still stands and was accepted, not
answered:** strict centre-out can send a pawn past a target beside them
to reach one nearer the click. It is bounded by `sweepRadius`, so it is
seconds at the default 16 and visible at the maximum 50. The cheap
mitigation this entry proposed - let a pawn take anything within a few
tiles of itself first - is still the right first move if that turns out
to matter in play. Do not apply it without asking; it is a deliberate
softening of an explicit instruction.

**Still unbuilt, still paired with this:** `showSweepOverlay` remains a
settings checkbox that draws nothing. Now that the sweep genuinely
radiates from the click, drawing the radius would be showing something
real.
