<!-- Architecture stub for Notify Still Being Attacked. Created 2026-08-29 alongside the scaffold. Holds the verbatim ask and nothing agreed. NOTHING HERE IS A GO-AHEAD TO BUILD BEHAVIOUR. -->

# Notify Still Being Attacked - Architecture

**Status: scaffold only. No behaviour is designed, costed or agreed.**

The folder `NotifyStillBeingAttacked/` exists, builds clean, and loads in RimWorld printing
one line. That is the whole of it. This document exists so the ask is
recorded somewhere better than a wishlist bullet, and so this mod can
graduate the way CLAUDE.md requires - by being written up here first.

**Ordered:** 2026-08-27, as one of five names in a single instruction.
**Scaffolded:** 2026-08-29.

---

## 1. The ask, verbatim

> "New mods coming, just build separate folders for each: Highlight
> Corpses With Tech; Notify Ripe (options: Ambrosia, Berries); Notify
> Still Being Attacked; Uninstall Hotkey; Menu Hotkeys."

**That instruction was a go-ahead for folders and explicitly not for
behaviour.** Everything in section 2 below is a reading of the name, not
a specification. Do not build from it.

---

## 2. What the name appears to mean - A GUESS

Probably a recurring alert while a colonist or the colony is under
ongoing attack - covering the case where the initial letter is missed or
dismissed and the fight is still going on.

The vanilla gap this fits: RimWorld tells you once when a raid arrives
and then goes quiet. If you were looking elsewhere, or you dismissed the
letter, nothing reminds you that it is still happening.

---

## 3. Questions to settle before this becomes a plan

- What is "still being attacked" about - the colony (a raid in
  progress), a specific colonist (being hit right now), or an animal or
  prisoner?
- What is the trigger to repeat: a fixed interval, or an event like a
  colonist taking a new wound?
- Alert (persistent, corner of screen) or repeated letter (interrupts)?
  A repeated letter during a long firefight would be intolerable.
- Should it auto-pause the game, and if so how often?
- Does it need to cover attacks on animals, or only humanlike
  colonists?

---

## 4. What exists now

```
NotifyStillBeingAttacked/
  About/About.xml            <- packageId phildeluca.notifystillbeingattacked, no dependencies declared yet
  Source/NotifyStillBeingAttacked/
    NotifyStillBeingAttacked.csproj            <- net472, refs ../../../lib, outputs to NotifyStillBeingAttacked/Assemblies/
    Core/NotifyStillBeingAttackedMod.cs        <- Mod subclass, one log line, nothing else
```

Add the Harmony `modDependencies` block to `About.xml` (copy it from
`DoNotBeLazy/About/About.xml`) if and when this mod actually patches
something.

**No `Defs/` folder.** Anything needing a `KeyBindingDef`, `LetterDef`
or similar creates the first XML in this mod.
