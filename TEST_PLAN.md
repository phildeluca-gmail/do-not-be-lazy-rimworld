<!-- Converted from the published artifact to markdown 2026-08-20 so it can be read without a browser. Three entries carry CORRECTION notes where the plan had gone stale against shipped code - see T0.1, T3.5, T3.6. Phase 6 added 2026-08-22 evening for the pool-discard and rescan fixes. Reordered 2026-08-26: passed tests now live in a Completed tests section at the bottom, so the top of the file is only work outstanding. -->

# Sow Fix Shakedown

Playtest plan for **Do Not Be Lazy**, originally written against commit
`cc502c9`. Everything in that commit was static analysis plus a clean
build; nothing had run in a real game. This is the pass that changes
that - ordered so each phase only runs once the one above it has proven
its own instruments.

- **Target:** RimWorld 1.5.4409, no DLC, ~60 mods
- **Entries:** 44, across phases 0-8 (`T0.1` through `T8.3`)
- **Status:** 7 passed - Phase 0 (2026-08-17), T6.1, and T7.1 + T7.2
  (all 2026-08-27). T6.4 and T6.6 were also *observed* in the 08-27 log
  (no sweep ended on its own, and none was meant to) but neither was run
  as written.
- **Run Phase 7 first.** Added 2026-08-27 for the two ordered changes
  (work emanates from the click; a right-click meant to move pawns must
  move pawns). Both outrank everything else in this file. **The drafted
  half is done:** T7.1 and T7.2 passed 2026-08-27 and have moved to
  Completed tests. **T7.3 through T7.6 are still outstanding** - T7.3 is
  the undrafted half of the same report, T7.5 the centre-out change.
- **Phase 0 detail**, since the count above doesn't carry it: T0.3
  passed again 2026-08-21 on a fresh save (sow trace carried
  `plant=Plant_Rice`, no reflection warning). **Everything from Phase 1
  down is outstanding, and Phase 1 has never been run.**
- **Phase 8 added 2026-08-29** for the sweep-ordering radio group
  (`centerOutOrder`). Three entries, none run. T8.1 shares a setup with
  T7.5, so run the two together.
- **Reading order, changed 2026-08-26.** Passed tests were moved to
  **Completed tests** at the bottom of the file. Phases run in the order
  they appear here; the only thing below that still bears on a new
  session is T0.1, which is a per-build precondition rather than a
  result.
- **Then Phase 6 if time is short.** It covers the standing-still bug
  reported from play and answered on 2026-08-22 - Phase 1 is still the
  sow work, which nobody has complained about since.

> **Read before starting.** Every pass/fail signature below is read off
> `[DoNotBeLazy]` log lines. Those lines were a no-op from Phase 1 of
> the project until commit `cc502c9` - past playtests showed zero of
> them and were misread as "nothing fired". **Phase 0 exists to prove
> the logger is live**, and it has passed - it now sits under
> **Completed tests** at the bottom. Its conclusion still governs
> everything here: unless T0.2's checkbox is on for the run you are
> extracting, an absence of log lines proves nothing at all, and no
> result from any later phase is worth recording.

> **Coverage gap, narrowed 2026-08-22 evening.** This plan predates the
> fire sweeps, the need pause/resume fix, the menu-feedback entries (all
> `9dc8717`) and the vehicle work. **Phase 6 now covers the pool-discard
> and rescan fixes.** The need pause/resume loop was confirmed working
> in a real game on 2026-08-22 (four pause/resume pairs, all resumed),
> so T2.7 is the lower priority it looks. Still no tests for the menu
> feedback entries or anything vehicle-related.

---

## Phase 0 - Prove the instruments · **PASSED, moved to the bottom**

All four checks passed 2026-08-17, and T0.3 passed again 2026-08-21 on a
fresh save. The tests themselves are at the end of this file under
**Completed tests**. Two of them are still worth a glance before a
session rather than a full re-run:

1. **T0.1 (ship the build)** is a precondition, not a one-time result.
   Re-check it whenever the repo has been rebuilt. Verified again
   2026-08-26: the installed DLL and the repo build are byte-identical
   (md5 `da1ead6142e46c0912381357f6cd434c`, both dated 2026-08-23
   04:03), and no source file is newer than the build.
2. **T0.2 (verbose logging on)** depends on a settings checkbox that
   lives in the save-independent settings file. It has stayed on across
   sessions, but a zero-line extraction means check it first.

---

## Key - Reading the trace

Six line shapes. The `plant=` field is the decisive one for everything
in Phase 1.

| Line | Means |
| --- | --- |
| `scan <def> r=N at <cell> for <pawn>: N targets` | The radial scan finished. **The count is a measurement, not decoration** - for sow it should track the number of empty sowable zone cells, not the ~800 cells a radius-16 scan touches. |
| `BeginSweep <def>: N targets, M pawns` | Pool accepted, pawns assigned. Logged just after the scan line. |
| `<pawn>: <JobDef> on <target> plant=<def> (N left)` | A task was handed out. `plant=` only appears when the job carries a `plantDefToSow` - this is the field the entire root-cause bug turned on. |
| `<pawn>: no job for <target> (<def>), left in pool, N total` | Target survived revalidation but the WorkGiver declined it *for this pawn*. **Since 2026-08-22 evening it stays in the pool** for another pawn to try; before that it was destroyed for the whole group, which was the standing-still bug. |
| `<pawn>: skipping <target> (<def>) - <reason>` | Revalidation refused it. Reasons: `reserved`, `forbidden`, `outside allowed area`, `unreachable`, `burning`, `sow settings`, `out of bounds`, `gone`. Only `gone` removes the target from the pool. New 2026-08-22 evening. |
| `<pawn>: nothing left within N of <cell>, ending sweep (<def>)` | The sweep finished cleanly for this pawn, after a rescan also came up empty. New 2026-08-22 evening - before this, a finished sweep was silent and looked identical to a pawn wandering off. |
| `BeginSweep <def> at <bench>: <pawn> of N ranked, first job <JobDef>` | A workstation order started. New 2026-08-22 evening; this path emitted nothing at all before, which is why bill orders had no start trace. If `first job` is not `DoBill`, the WorkGiver wants a haul-off or refuel first. |
| `<pawn>: job ended <condition> (<def>)` | The condition decides continue-vs-stop. Almost every "the pawn wandered off" report is answered by this one line. |
| `<pawn>: N sweep tasks failed in a row` | The per-pawn bound of 8 tripped and the sweep was ended deliberately. |

> **One counting quirk that is not a bug.** `(N left)` is logged
> *before* a preparatory target gets re-queued. So when a blocker job
> fires, the counter can stall or repeat between consecutive lines
> instead of strictly decreasing. That happens at most once per target,
> by design - see T2.4.

> **A second counting change, 2026-08-22 evening.** `(N left)` now only
> decreases when a job is actually handed out or a target is found to be
> gone. Refusals leave the count alone. It also **goes up** when a
> rescan finds new work. Treat it as "size of the shared pool", not
> "work remaining".

---

## Phase 1 - The two reported bugs

Both symptoms - "sow does nothing, pawns get new jobs" and "sow assigns
unzoned terrain" - trace to one stale `protected static`. These four
tests are the reason the commit exists.

### T1.1 - One zone, right crop, job survives the walk · **CORE**

**Setup.** A growing zone set to rice with at least 10 empty sowable
cells. One colonist with Growing enabled, standing well outside the zone
so there's a real walk to fail on.

**Do.** Select the pawn, right-click an empty cell inside the zone,
choose **"\* Sow crops until done"**.

**Pass.** Pawn walks over and actually plants. Log shows
`plant=Plant_Rice` matching the zone's crop, then `job ended Succeeded`,
then the next assignment.

**Fail.** `plant=` naming a crop that isn't this zone's, or an
`Incompletable` end mid-walk with no work done. That is the original bug
intact: the stale def is being baked into the job and
`JobDriver_PlantSow`'s goto toil is failing it on arrival.

### T1.2 - Bare dirt stays bare · **CORE**

**Setup.** A growing zone bordered by open, sowable soil that has *no*
zone on it. Click near the boundary so the sweep radius clearly covers
unzoned ground.

**Do.** Run the sow sweep. Then count.

**Pass.** The `scan GrowerSow` target count is roughly the number of
empty cells *inside* the zone. Every `plant=` line points at a cell
within the zone. No pawn walks onto bare dirt.

**Fail.** A target count running into the hundreds - a radius-16 scan
touches about 800 cells, so a number in that neighbourhood means the
zone gate never ran and the whole radius was accepted.

### T1.3 - Two zones, two crops · **DECISIVE**

**Why.** The sharpest available test of the actual root cause. The
static is shared, so if the reset isn't working, whichever crop got
computed first bleeds into every subsequent cell in the same sweep.

**Setup.** A rice zone and a corn zone, both empty, both inside one
sweep radius of a single click point.

**Do.** One sow sweep covering both.

**Pass.** Within the same sweep, `plant=Plant_Rice` on rice-zone cells
and `plant=Plant_Corn` on corn-zone cells, interleaved as the pawn works
nearest-first.

**Fail.** Every line naming one crop. The reset isn't reaching the
field, or it's being called somewhere other than immediately before
`JobOnCell`.

### T1.4 - The sweep finishes on its own

**Do.** Let T1.1's sweep run to completion without touching the pawn.

**Pass.** A clean run of `Succeeded` ends with `(N left)` counting down
to zero, then the pawn returns to normal work with no further sweep
lines.

**Fail.** A chain of `Incompletable`, or the trace stopping abruptly
while sowable cells remain.

---

## Phase 2 - The six supporting fixes

Each shipped in the same commit and each is independently unverified.
Several need dev mode to set up cleanly.

### T2.1 - Allow-sow off hides the option entirely

**Setup.** Select a growing zone, untick **"Allow sow"** in its inspect
pane.

**Do.** Right-click an empty cell in that zone with a Growing-capable
pawn selected.

**Pass.** No `* Sow crops until done` appears at all. The gate runs in
the float-menu path, not just the scan, so the option should never be
offered.

**Fail.** Option appears. `SowSettingsAllow` isn't reaching
`Zone_Growing.allowSow`.

### T2.2 - Unpowered hydroponics

**Setup.** A hydroponics basin with its power cut.

**Pass.** No sow option - this is the `CanAcceptSowNow()` half of the
same gate, and it's the half that has nothing to do with zones.

**Fail.** Option offered, or offered and then the sweep finds zero
targets. The second case means the float-menu path and the scan path
disagree.

### T2.3 - Unreachable cells are dropped, not failed

**Why.** `WorkGiver_Grower.AllowUnreachable` is true - vanilla
deliberately skips its usual reachability filter here and expects the
WorkGiver's own scan to do it. This mod never calls that scan.

**Setup.** Split a growing zone with a wall or water so part of it is
genuinely unpathable from the pawn, keeping both halves inside the
radius.

**Pass.** Scan count covers only the reachable half. Sweep runs to
completion.

**Fail.** `job ended ErroredPather` lines, or a count that includes the
walled-off cells.

### T2.4 - Blocker chaining: cut, then sow the same cell

**Setup.** Let a few wild plants grow inside an empty growing zone, or
drop loose items on some of its cells.

**Do.** Run a sow sweep and follow one specific blocked cell through the
trace.

**Pass.** A `CutPlant` or haul job on that cell, then *later in the same
sweep* a `Sow` job on the same cell. The `(N left)` counter stalls once
where the re-queue happened.

**Fail.** The cell is cleared and never sown - the re-queue isn't
firing. Or the same cell cycles more than twice, meaning the once-only
`Requeued` guard isn't holding.

### T2.5 - A failed target is not a failed sweep · **CORE**

**Why.** This is what made the root-cause bug present as "sowing does
nothing" rather than "one cell got skipped". Worth verifying on its own
even after Phase 1 passes.

**Setup.** Start a haul sweep across a dozen or so loose items.

**Do.** Mid-sweep, forbid one of the not-yet-hauled items, or destroy it
with dev mode.

**Pass.** `job ended Incompletable` (or `QueuedNoLongerValid`) followed
by another assignment line for the same pawn. The sweep survives.

**Fail.** The trace ends there and the pawn picks up unrelated work.

### T2.6 - The failure bound actually bounds · **RECURSION RISK**

**Why.** Each retry re-enters `AssignNextTask` from inside
`EndCurrentJob`. If every target fails on the tick it's issued, an
unbounded retry is unbounded recursion, not just a slow loop. The cap of
8 is load-bearing.

**Do.** Hardest case to stage deliberately. Best approximation: a large
sweep where most targets are made invalid at once - forbid a whole
stockpile mid-haul-sweep, or wall off the pawn from the work area.

**Pass.** `8 sweep tasks failed in a row`, sweep ends cleanly, no stack
overflow, no frame hitch.

**Fail.** A hang, a stack-overflow exception, or the line never
appearing while failures obviously continue.

### T2.7 - Pause for need, then resume the same sweep

**Setup.** Raise **need interrupt threshold** to its 20% maximum so it
triggers on a merely hungry pawn rather than a starving one. Pick a pawn
already low on food or rest.

**Do.** Start a long sweep - cleaning or hauling with plenty of targets
- and let the need trip.

**Pass.** Pawn breaks off, eats or sleeps under normal AI, then goes
back to the *same* sweep afterwards. No repeated pause lines while
they're asleep.

**Fail.** Sweep is dropped instead of paused, or the pawn ping-pongs
between resuming and re-interrupting.

**After.** Put the threshold back to 5%.

> **Note (2026-08-20).** This is now the direct regression test for the
> `9dc8717` pause/resume fix. The two lines to grep are
> `<pawn>: needs satisfied, resuming sweep (<def>)` and
> `<pawn>: still under threshold after N ticks paused, ending sweep.`
> The old bug looked like a pause line after *every* task with no resume
> line between them.

### T2.8 - Fire filters per target, not per zone

**Setup.** Dev mode. Start a fire on part of a harvestable field or a
hauling area.

**Do.** Two passes: right-click and sweep *with the fire already
burning*, then start a second sweep and light a new fire on a target the
pool already holds.

**Pass.** Burning targets excluded from the scan count; a fire started
mid-sweep causes that one target to be skipped, not the sweep to end.
**The unburnt part of the field stays workable** - this deliberately
diverges from vanilla, which skips an entire grow zone containing static
fire.

**Fail.** Pawns walking into fire, or the whole sweep dying when one
target ignites.

---

## Phase 3 - Regressions

These worked before. The commit touched shared paths - both
`TaskScanner` branches, `Notify_JobEnded`, and `TargetStillValid` - so
all of them are now downstream of the sow fix. Quick confirmations, not
deep runs.

### T3.1 - Haul fans out across pawns

**Do.** Three pawns selected, sweep a scattered pile of haulables.

**Pass.** Each pawn claims a *different* item - the shared pool is doing
its job. Note that Pick Up And Haul adds its own multi-item hauling
variant; that option showing up alongside vanilla haul is expected and
correct.

### T3.2 - Mine, and clean

**Do.** One sweep each: a block of mineable cells, and a dirty room.

**Pass.** Pawns fan out to distinct cells and work to completion. Both
are cell-adjacent paths that the sow work sits next to.

### T3.3 - Deliver resources appears exactly once · **KNOWN REGRESSION SITE**

**Do.** Right-click an unfinished frame or blueprint.

**Pass.** **One** `* Deliver resources... until done`, not two. The
Hauling-tagged duplicates are excluded by name.

**Note.** `* Construct placed frames` being absent is expected while the
frame is still short on materials - same as vanilla. It should appear
once resourcing completes.

### T3.4 - All workstation bill types survive · **PAST REGRESSION**

**Why.** A previous dedup-by-`giverClass` fix silently collapsed all ~19
bill types down to one, because every workstation in the game shares
that single giverClass. It was reverted - this confirms the revert held.

**Do.** Right-click four different stations with bills queued: cooking
stove, smithy, tailoring bench, stonecutter's table.

**Pass.** Each offers its own distinct `*` option.

**Fail.** Only one station type offers anything.

### T3.5 - Consume: food, drug, teetotaler

**Do.** Three right-clicks - a meal stack, a drug stack (smokeleaf or
wake-up), and a drug stack with a Teetotaler among the selected pawns.

**Pass.** Drugs give `* Smoke...` / `* Take...` using vanilla's own
wording. The Teetotaler is skipped while everyone else still gets a
dose.

**Note.** Drafted pawns are deliberately *included* here, unlike the
sweeps - dosing a squad before a fight is the point.

> **CORRECTION (2026-08-18).** The original said food yields
> `* Eat meal`. Wrong. Only drugs set `ingestCommandString` in Core, so
> food and corpses fall through to our hardcoded `"Consume " +
> LabelShort` and read **`* Consume fine meal`**. That matches vanilla,
> which also says "Consume human corpse" - not a divergence.

### T3.6 - A burning tile offers the fire sweep and nothing else

**Do.** Right-click directly on a burning tile.

**Pass.** Exactly one entry, `* Fight fires until done`, and no other
`*` option - fire suppresses every other def and `* Consume` on that
cell, deliberately.

**Fail.** Other `*` options offered alongside it (pawns would be sent
onto a burning tile), or **no float menu appears at all**.

> **CORRECTION (2026-08-20) - this test was inverted by `9dc8717`.**
> The original pass condition was "no `*` options whatsoever, the whole
> click bails", on the reasoning that `FightFires` is
> `directOrderable: false` in vanilla so there is no vanilla "put out
> fire" entry either. We now deliberately override that - see
> `FireCompat` and architecture doc section 0.
>
> **The failure mode to watch for is that no menu appears at all**, not
> that an empty one does. If `FireCompat.HasFireJob` refuses
> (unreachable, already handled within 5 tiles) or no selected pawn is
> fire-eligible, we add zero options - and since vanilla has nothing to
> offer on a bare burning tile either, `TryMakeFloatMenu` returns
> without showing anything. The click looks dead. `HasFireJob` never
> writes `JobFailReason`, so there is no greyed-out entry to explain it
> either. That is open review finding 2.
>
> Also note: drafted pawns are excluded from fire sweeps by
> `PawnValidator.CanSweep`, even though vanilla allows drafted
> firefighting. **Undraft before running this test** or it will fail for
> the wrong reason.

### T3.7 - Harvest and cut plants

**Do.** Sweep a mature field with `* Harvest crops until done`.
Separately, mark some trees for chopping and sweep `* Cut plants until
done`.

**Pass.** Both run to completion. Harvest shares the cell-scan path with
sow but computes its wanted plant per cell, so it should be unaffected
by the static - worth confirming rather than assuming.

---

## Phase 4 - Probe the known-open items

Not bugs to fix in this pass - open questions from the commit that only
real play can size. Record what you see; don't chase.

### T4.1 - Right-click latency at radius 50 · **PERF**

**Why.** Reachability now runs per cell inside the radial scan - roughly
800 `CanReach` calls at the default radius, about 7,800 at the maximum.

**Do.** Set sweep radius to 50, right-click in a large open farm area,
and watch for a hitch between click and menu.

**Record.** Any perceptible stall. The fix if it's real is to move the
reachability check after `HasJobOnCell` rather than before - cheap, but
not worth doing on speculation.

### T4.2 - Construction frames get one extra re-queue

**Why.** The preparatory-job check isn't scoped to cell targets, and
deliver-resource jobs also point `targetA` at the resource rather than
the frame - so frames get re-queued once too. Plausibly an improvement,
since frames usually need several deliveries. Untested beyond sow.

**Do.** Run a deliver-resources sweep across several frames and watch
for repeated targets in the trace.

**Record.** Whether the extra pass helps (fewer half-resourced frames
left behind) or just churns.

### T4.3 - Reservation pre-filter is stricter than the job it gates

**Why.** The scan's `CanReserve` check omits `ignoreOtherReservations`,
while `JobOnCell` is passed `forced: true`. So the pre-filter can reject
a target the actual job would have accepted.

**Do.** Have one colonist working normally in an area, then sweep a
second colonist over the same area.

**Record.** Whether the second pawn's scan count comes back visibly
short. A forced player order arguably *should* bypass this.

---

## Phase 5 - Close the two open reports

Both are unresolved from earlier sessions and both need information only
play can give. Neither has a code-level diagnosis yet.

### T5.1 - "Cannot force-haul stone blocks"

**First.** Open every stockpile in range and check whether **Blocks** is
actually ticked in its allowed-items filter. It's frequently a separate
category from other raw resources in the stockpile presets, and this
matches the earlier wood report exactly.

**Then.** Right-click the blocks and look for the *vanilla* Haul option,
not ours.

**Verdict.** Vanilla Haul missing too → not our bug; there's no valid
destination. Vanilla present but no `*` option → a real bug, and worth
capturing the trace.

> **Note (2026-08-20).** There is now a second candidate answer:
> Hauling sitting at priority 0 in the work tab for the selected pawns.
> `PawnValidator.CanSweep` requires `WorkIsActive`, and open review
> finding 1 means that refusal produces **no** greyed-out entry - it
> looks identical to "no option". Check the work tab before concluding
> anything from the menu.

### T5.2 - "The `* forced delivery to (ITEM)` is gone"

**Needed.** A repro. Specifically: what exactly was clicked, and did it
show *once* during the double-showing period and now shows zero times,
or never showed at all?

**Then.** Note that Sense of Urgency is *not* a candidate explanation,
despite what this plan said before 2026-08-21 - none of its defs is
sweep-eligible, so it cannot add or remove a `*` option.

---

## Phase 6 - The 2026-08-22 evening fixes

These are the pool-discard and rescan changes. **Nothing here has ever
run in a game.** Run this phase before Phase 1 if time is short - it
covers the bug that was actually reported from play.

### T6.1 - A big selection spreads across the pool · **PASSED 2026-08-27**

Moved to **Completed tests** at the bottom of this file. Passed on
`CleanFilth` rather than `HaulGeneral` - `BeginSweep CleanFilth: 648
targets, 50 pawns`, 51 distinct pawns worked. The `break` it tests was in
the pawn loop and is WorkGiver-agnostic, so the substitution is fair.

### T6.2 - A refused target is not destroyed · **CORE, STILL OPEN**

**Not passed by the 2026-08-27 log, and read the reason before assuming
it was.** That session was all cleaning, and this test's own contrast
note predicts what happened: **zero** `no job` lines, because cleaning
has no destination to reserve. What the log does show is 872
`skipping ... - reserved` with the targets staying in the pool, which is
the same fix seen from the side that doesn't stress it. **The hauling
case this test exists for is still untested.** Run it on haulables into
a nearly-full stockpile, as written below.

**Why.** 151 `no job` discards against ~97 haul assignments in one
session. A target one pawn couldn't take was thrown away for everyone.

**Do.** Same sweep as T6.1, into a stockpile that is nearly full or has
a narrow accepted-items filter, so `TryFindBestBetterStorageFor` fails
for some pawns.

**Pass.** `no job for ... left in pool, N total` lines appear **without**
the pool count dropping for them, and the same target id later appears
in a successful `<pawn>: HaulToCell on <that id>` line. Total items
hauled should be close to the pool size, not half of it.

**Contrast worth logging.** A `* Clean until done` sweep should produce
**zero** `no job` lines. Cleaning has no destination to reserve; that
contrast is what identified the mechanism in the first place.

### T6.3 - A pawn coming back from a break still has work · **CORE**

**Why.** In the 08-22 log `snake` paused for a need, resumed correctly,
and was then dropped one line later because the other eight pawns had
emptied the pool while she ate. The pause/resume fix worked; there was
nothing left to resume into.

**Do.** Start a large sweep with several pawns, one of them close to a
need threshold (needs default to 5%, mood 10%). Let them pause and
return.

**Pass.** `needs satisfied, resuming sweep` is followed by an actual
`<pawn>: <JobDef> on <target>` line, not silence. If the pool really is
empty, the rescan runs and either finds work or logs
`nothing left within N of <cell>, ending sweep`.

### T6.4 - A finished sweep says so · **NOT RUN - see note**

**The 2026-08-27 log has zero `nothing left within ... ending sweep`
lines**, which is consistent with T6.6 (rescans keep finding filth, so
nothing finished) rather than with this line being broken. Untested
either way - it needs a sweep that genuinely runs out.



**Do.** Sweep a small, fully completable pile - five haulables, nothing
else in range.

**Pass.** Each pawn's last line is
`nothing left within 16 of (x, y, z), ending sweep (HaulGeneral)`, not a
bare `job ended Succeeded`.

### T6.5 - Rescan does not duplicate the pool · **REGRESSION RISK**

**Why.** Rescans used to be append-only, which was safe only because
targets left the pool immediately. `AddNewTargets` dedups; if that
broke, the pool would double on every rescan.

**Do.** Any sweep large enough that a pawn empties its share and
triggers a rescan. Watch the `(N left)` counter across the run.

**Pass.** The count rises only by however much genuinely new work
appeared. A sudden near-doubling is the dedup failing.

### T6.6 - Sweeps that never end · **BEHAVIOUR CHANGE, OBSERVED 2026-08-27**

**Seen in the 08-27 log:** not one sweep ended on its own across 3,882
trace lines and three concurrent orders. That is the change working as
designed, and it is the decision this entry asks for - still unmade.



**Why.** Deliberate consequence of the rescan: an area sweep no longer
has a natural end. This needs a judgement call, not a pass/fail.

**Do.** `* Clean until done` in a busy, high-traffic room. Leave it
running for an in-game hour.

**Observe.** Pawns should keep cleaning as new filth appears, and only
stop when drafted or given another order. **Decide whether that is
wanted.** If it is too sticky, the fix is a cap on rescans per order -
do not add one without asking.

### T6.7 - A restricted pawn's rescan · **KNOWN CAVEAT**

**Why.** A rescan is driven by whichever pawn asked, so it inherits that
pawn's allowed area and reachability - a narrower scan than the original
click made.

**Do.** Put one pawn in a restricted allowed area, include them in a
sweep that spans outside it, and let them run the pool dry.

**Pass.** They end their own sweep without shrinking anyone else's pool.
The other pawns keep working targets outside that area.

---

## Phase 7 - The 2026-08-27 ordered changes · **RUN THIS FIRST**

Both were ordered directly and both outrank every other phase in this
file, Phase 6 included. Six entries; **T7.1 and T7.2 passed 2026-08-27**
and have moved to Completed tests, leaving T7.3-T7.6. Full specification in
`DoNotBeLazy_Architecture.md` section 0, "TOP PRIORITY".

### T7.3 - An undrafted group can still be moved · **CORE, THE OTHER HALF**

**Why this is separate.** The report was about formations, but the bug
does not need drafted pawns. `ChoicesAtForMultiSelect` never builds a
goto option, so an undrafted multi-select right-click that we make
non-empty opens a menu with no way to move.

**Do.** Select 3+ **undrafted** colonists. Right-click a spot that offers
a sweep - filth on the floor is easiest.

**Pass.** The menu opens with **`Move here` as the first entry**, above
`* Clean until done`. Choosing it walks all of them there, spread across
neighbouring cells rather than stacked on one.

**Fail.** No `Move here` entry - then either the incoming option list
wasn't empty (vanilla put something there, so we are not the cause and
the entry is correctly absent) or no selected pawn could reach the cell.
Try a plainly reachable cell before calling it a bug.

### T7.4 - `Move here` stays out of the way when it isn't ours

**Do.** Multi-select undrafted colonists and right-click something
vanilla already offers a multi-select option for.

**Pass.** No `Move here` entry from us. The menu was opening with or
without our contribution, so we didn't suppress anything and owe nothing.

### T7.5 - Work emanates from the click · **CORE**

**Setup.** A long line or wide field of filth, 20+ tiles across. Put
your pawns at the **far end** of it, deliberately.

**Do.** Select them all. Right-click the **far end from the pawns** and
`* Clean until done`.

**Pass.** They walk past nearer filth to clear the area around the cell
you clicked first, and the cleared area grows outward from that point in
rings. **The pawns walking past nearer work is the pass condition, not a
bug** - it is the accepted cost recorded in architecture doc section 0.

**Fail.** Each pawn cleans whatever is under its own feet and the field
finishes everywhere at once. That is the old nearest-to-pawn behaviour.

**Read it in the trace.** `<pawn>: <JobDef> on <target>` lines - the
target cells should be increasing in distance from the `BeginSweep`
cell, not from each pawn.

### T7.6 - Centre-out at radius 50 · **KNOWN COST, MEASURE IT**

**Why.** Strict centre-out is bounded by `sweepRadius`. At the default 16
the extra walking is seconds; at 50 it is the thing most likely to make
this change feel wrong.

**Do.** Set `sweepRadius` to 50 in mod settings. Repeat T7.5.

**Pass.** Judgement call, and the point is to make it deliberately: does
the walking look purposeful or stupid? Record the answer either way.

**If it looks stupid,** the mitigation is already designed and written
down - let a pawn take anything within a few tiles of itself before
falling back to centre-out (`RW-Wishlist.md` entry 2). **Do not apply it
without asking**; it deliberately softens an explicit instruction.

---

## Reporting back

Run `/pull-logs` - it archives, extracts and reads the log without
pasting it back. Give it the test ID and what you saw on screen.

**Two traps that have each cost real data.** (1) Check which DLL the game
actually loaded before reading a line of the log; `/pull-logs` step 1
does it. (2) RimWorld caps logging at ~1000 messages and prints
`Reached max messages limit`; it fired **four times** in the 08-27 log,
so counts from a noisy session are floors, not totals. Turn
`jobDiagnostics` off unless you are actively chasing an idle report.

If extracting by hand instead, paste only the `[DoNotBeLazy]` lines,
plus the test ID and what you saw on screen. Whole logs cost enormous
context to reach a two-line answer - the trace exists so they aren't
needed.

---

## Phase 8 - The 2026-08-29 ordering setting

Three entries. **T8.1 is the one that matters**; the other two guard the
two ways this change can be wrong without looking wrong.

Note that T7.5 and T7.6 in Phase 7 still test the centre-out *rule* -
they are unaffected by this phase and still unrun. Run them at the
default setting, which is the same behaviour they were written against.

### T8.1 - The setting actually changes the order · **CORE**

**Setup.** T7.5's setup exactly: a long line or wide field of filth, 20+
tiles, pawns at the far end. Doing it as a back-to-back pair against
T7.5 is the point - same field, same click, one setting changed.

**Do.** Options > Mod Settings > Do Not Be Lazy. Confirm the radio group
reads **"Sweep works outward from:"** with **the click** selected (that
is the default). Set it to **Each pawn**. Close settings. Select the
pawns, right-click the far end, `* Clean until done`.

**Pass.** Pawns clean what is under their own feet first and the field
finishes everywhere at once - the pre-08-27 behaviour. Flip back to
**The click**, repeat, and T7.5's centre-out result returns.

**Fail.** Identical behaviour in both modes. That means the setting is
not reaching `NextTargetIndex` - most likely the order was stamped
before the setting was read, or the wrong branch is wired.

**Read it in the trace.** The `BeginSweep` line now names the mode:
`... , order centre-out` or `... , order pawn-nearest`. If that word
does not match the radio button you just set, stop - nothing after it
means anything.

### T8.2 - The mode is stamped, not polled · **THE DESIGN DECISION**

**Why.** The mode is read once at click time and fixed for the life of
the order. That is deliberate (architecture doc 3.2), and it is
invisible unless tested for directly.

**Do.** Start a long sweep with **The click** selected. While it is
still running, open settings, switch to **Each pawn**, close settings,
and watch the same sweep.

**Pass.** The running sweep keeps clearing in rings from the original
click. The *next* right-click sweep uses pawn-nearest.

**Fail.** The running sweep changes ordering mid-flight. That means
`NextTargetIndex` is reading settings live instead of `order.CenterOut`.

### T8.3 - The setting survives a restart

**Do.** Set **Each pawn**, quit RimWorld fully, relaunch, reopen the
settings window.

**Pass.** Still **Each pawn**. A fresh install/profile with no saved
value shows **The click**.

**Fail.** Reverts to the default. `Scribe_Values.Look` for
`centerOutOrder` is missing or misnamed.

---

**Phase 7 comes before all of it** - it is the only phase covering work
that was ordered rather than found. **Phase 8 goes with it**: T8.1 is
cheap and shares a setup with T7.5. After that, phases 1 and 2 are the
pass that decides whether commit `cc502c9` holds; Phase 0 already passed
and is at the bottom of the file. Phases 3 through 5 can follow
separately if time is short.
---

# Completed tests

Everything below has passed in a real game. Kept for the procedure and
the pass/fail signatures, not because it needs running again. Nothing
here gates a new session except T0.1, which is a precondition - see the
Phase 0 pointer above.

## Phase 7 - passed entries

Both halves of the drafted-pawn report. **Reported passed by the user on
2026-08-27**, on the 08-27 build (`de25c3a5`); no log was pulled for
either, so the evidence is the observed behaviour, not a trace. The
undrafted half of the same report, T7.3, has not been run.

### T7.1 - A right-click moves a drafted squad again · **PASSED 2026-08-27**

**Setup.** Draft 3+ colonists. Stand them somewhere with filth or
haulables nearby, so a `*` option would have been on offer.

**Do.** Right-click a destination cell to move them into formation.

**Pass.** They move. No float menu opens at all - the click behaves
exactly as it does with the mod disabled.

**Fail.** A menu opens, or they don't move. If the menu opens and its
only entry is `* Clean until done`, the drafted suppression did not
take - check that every pawn in the selection is actually drafted, since
the gate is *all* drafted, not *any*.

### T7.2 - The same, single drafted pawn · **PASSED 2026-08-27**

**Do.** Draft one colonist, right-click a destination.

**Pass.** They move with no menu. Vanilla auto-takes a menu whose options
are all `autoTakeable`, and this is the path that was cancelled by any
appended entry, greyed or not.

## Phase 6 - passed entries

### T6.1 - A big selection spreads across the pool · **PASSED 2026-08-27**

**Evidence.** `logs/20260827-013412-dnbl.log`:
`BeginSweep CleanFilth: 648 targets, 50 pawns`, followed by clean jobs to
**51 distinct pawns** over the session. No pawn was dropped silently.

**Caveat on the substitution.** Written for `HaulGeneral`, passed on
`CleanFilth`. The `break` this tests sat in the pawn loop in
`BeginAreaSweep` and never looked at the WorkGiverDef, so the swap does
not weaken the result. The *hauling*-specific half of the 08-22 report is
T6.2, which is still open.

**Original procedure, as written:**

**Why.** The reported bug. In `logs/20260822-225440-dnbl.log`, a
17-target pool with **34 pawns selected** gave work to **2** of them and
discarded 16 targets; a 1-target pool with 36 selected served one pawn
and dropped 35. Those pawns stood still.

**Do.** Select ~30 colonists. Right-click a scattered pile of haulables
with roughly 15-20 items in range and take
`* Haul general things until done`.

**Pass.** `BeginSweep HaulGeneral: N targets, M pawns` is followed by
assignments to **many distinct pawns**, not one or two. No pawn in the
selection is left standing with no job and no `skipping`/`no job` line
explaining itself.

**Fail shape to watch for.** `M pawns` much larger than the number of
pawns that ever appear in a `<pawn>: <JobDef> on ...` line. That is the
old `break` behaviour returning.

## Phase 0 - Prove the instruments

Four checks, maybe ten minutes. All of them gate everything after.

### T0.1 - Ship the current build · **BLOCKER**

**Setup.** Copy `DoNotBeLazy\Assemblies\DoNotBeLazy.dll` into
`<RimWorld>\Mods\DoNotBeLazy\Assemblies\`, then fully restart the game.

**Do.** Before launching, compare the timestamp of the copy against the
source build.

```powershell
Get-Item "<RimWorld>\Mods\DoNotBeLazy\Assemblies\DoNotBeLazy.dll" |
  Select-Object LastWriteTime, Length
```

**Pass.** Matches the DLL in the repo at
`DoNotBeLazy\Assemblies\DoNotBeLazy.dll`.

**Fail.** Any earlier timestamp means the game is loading last session's
code and every result below is fiction. Recopy.

> **CORRECTION (2026-08-20).** The original plan hardcoded
> `8/16/2026 9:37:58 AM` here, and the correction that replaced it
> hardcoded `8/18/2026 10:07:03 PM` - both went stale within days. Don't
> hardcode it again: compare against whatever the repo's copy actually
> reads, since the useful question is "is the game running what I just
> built", not "does it match a date someone typed into a document". A
> hash is stronger than a timestamp - `md5sum` both copies.
>
> **Checked 2026-08-26 and matching.** Repo and installed DLL are
> byte-identical, md5 `da1ead6142e46c0912381357f6cd434c`, and no `.cs`
> file is newer than the build. Note this contradicts the "installed DLL
> is older than the repo build" line in `NEXT_SESSION.md`, which is
> stale.

### T0.2 - Make the logger speak · **BLOCKER**

**Setup.** Options → Mod settings → Do Not Be Lazy → tick **"Verbose
logging (for bug reports)"**. Close the settings window rather than
alt-tabbing away - closing is what writes the setting to disk.

**Do.** Select one colonist, right-click anything that offers a `*`
option, run it. Then extract:

```powershell
Select-String -Path "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log" -Pattern '\[DoNotBeLazy\]' | ForEach-Object { $_.Line }
```

RimWorld truncates `Player.log` on launch - extract before restarting,
not after.

**Pass.** One or more `[DoNotBeLazy]` lines come back.

**Fail.** Zero lines means the checkbox didn't persist or T0.1 didn't
take. **Stop here.** Do not run Phase 1 blind.

### T0.3 - Confirm the reflection target resolved · **BLOCKER**

**Why.** The whole sow fix hangs on reaching a `protected static` field
by name. If `AccessTools.Field` came back null, `GrowerCompat` is inert
and silently does nothing - the sow tests would all fail for a reason
unrelated to what they're testing.

**Do.** Scan the same extraction for this warning. It is logged
unconditionally, not gated behind the verbose checkbox, and fires the
first time `GrowerCompat` is touched rather than at startup.

```
WorkGiver_Grower.wantedPlantDef not found - sow sweeps
may target the wrong crop or unzoned cells
```

**Pass.** The warning is *absent* after at least one sow right-click.

**Fail.** Warning present - the field was renamed or is otherwise
unreachable in this build. Phase 1 is meaningless until that's resolved.

### T0.4 - Quiet the broken neighbours

**Do.** Disable **Automatic Hunting** for this run. It throws every tick
in `GameComponentTick` (`TraverseParms.For`) and also calls
`Toils_General.WaitWith`, so its noise sits in the middle of every
extraction and it is the leading suspect for colonists standing still.

**Corrected 2026-08-21.** This step used to say Sense of Urgency, on the
belief that it was the 1.6-compiled mod throwing on `WaitWith`. It isn't
- it ships a real 1.5 assembly with no such reference, and none of its
WorkGiverDefs is sweep-eligible, so it can neither break hunting nor
duplicate a `*` option. Leaving it enabled is fine.

**Keep.** Leave the rest of the ~60-mod list loaded, Performance Fish
included. It patches `WorkGiverDef::get_Worker()`, which
`EligibleDefs()` calls on every def - testing without it wouldn't test
the configuration you actually play.

**Note.** If you keep Automatic Hunting enabled anyway, expect its
exception in the raw log every tick. Not ours, but it makes an
extraction hard to read.

