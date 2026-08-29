<!-- Architecture stub for Notify Ripe. Created 2026-08-29 alongside the scaffold. Holds the verbatim ask and nothing agreed. NOTHING HERE IS A GO-AHEAD TO BUILD BEHAVIOUR. -->

# Notify Ripe - Architecture

**Status: scaffold only. No behaviour is designed, costed or agreed.**

The folder `NotifyRipe/` exists, builds clean, and loads in RimWorld printing
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

A letter or alert when a wild plant reaches harvestable growth.

**This is the only one of the five that arrived with any stated
interface.** The parenthetical "(options: Ambrosia, Berries)" is
explicitly a settings list, so two toggles are already specified:

- Ambrosia (`Plant_Ambrosia`)
- Berries (`Plant_Berry`)

Both are wild plants the player harvests opportunistically and both are
easy to miss, which fits "notify" precisely.

---

## 3. Questions to settle before this becomes a plan

- Letter, alert, or message? A letter pauses and demands attention; an
  alert sits in the corner until dismissed; a message flashes past.
  Ambrosia probably wants a letter, berries probably do not.
- One notification per bush, or one per patch? Ambrosia spawns in
  clusters - a letter per bush would be unusable.
- Only wild plants, or also player-grown ones? Grown crops already have
  their own harvest handling, so probably wild only.
- Should the two options be independent toggles (implied by the ask),
  and is the list meant to be extensible to other plants later?
- Does it re-notify if the player ignores it, or notify once per plant?

---

## 4. What exists now

```
NotifyRipe/
  About/About.xml            <- packageId phildeluca.notifyripe, no dependencies declared yet
  Source/NotifyRipe/
    NotifyRipe.csproj            <- net472, refs ../../../lib, outputs to NotifyRipe/Assemblies/
    Core/NotifyRipeMod.cs        <- Mod subclass, one log line, nothing else
```

Add the Harmony `modDependencies` block to `About.xml` (copy it from
`DoNotBeLazy/About/About.xml`) if and when this mod actually patches
something.

**No `Defs/` folder.** Anything needing a `KeyBindingDef`, `LetterDef`
or similar creates the first XML in this mod.
