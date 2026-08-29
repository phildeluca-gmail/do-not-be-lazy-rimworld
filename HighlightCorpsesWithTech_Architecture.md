<!-- Architecture stub for Highlight Corpses With Tech. Created 2026-08-29 alongside the scaffold. Holds the verbatim ask and nothing agreed. NOTHING HERE IS A GO-AHEAD TO BUILD BEHAVIOUR. -->

# Highlight Corpses With Tech - Architecture

**Status: scaffold only. No behaviour is designed, costed or agreed.**

The folder `HighlightCorpsesWithTech/` exists, builds clean, and loads in RimWorld printing
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

Probably an overlay or marker drawn on corpses that carry
researchable or strippable technology, so they are not missed before
they rot.

**The name is genuinely ambiguous about what "with tech" modifies:**

- **The corpse.** Highlight corpses that *have* tech on or in them -
  mechanoids, high-tech raider gear, implants worth extracting. This is
  the reading that makes it a useful mod.
- **The highlight.** Draw the highlight in a tech-styled way - a scanner
  overlay, a HUD marker. This reading makes it a cosmetic mod.

These are completely different pieces of work. Ask before assuming.

---

## 3. Questions to settle before this becomes a plan

- Does "with tech" describe the corpse or the highlight? (See above -
  this is the blocking one.)
- If the corpse: what counts as tech? Mechanoid corpses, corpses wearing
  or carrying items above some tech level, corpses with extractable
  implants (bionics, archotech), or a list the player configures?
- What does "highlight" mean on screen - a coloured overlay on the
  corpse, an icon above it, a map-edge marker, an entry in an alert?
- Does it need to distinguish "will rot soon" from "is fine for now"?
  Rot timing is the whole reason to be told at all.

---

## 4. What exists now

```
HighlightCorpsesWithTech/
  About/About.xml            <- packageId phildeluca.highlightcorpseswithtech, no dependencies declared yet
  Source/HighlightCorpsesWithTech/
    HighlightCorpsesWithTech.csproj            <- net472, refs ../../../lib, outputs to HighlightCorpsesWithTech/Assemblies/
    Core/HighlightCorpsesWithTechMod.cs        <- Mod subclass, one log line, nothing else
```

Add the Harmony `modDependencies` block to `About.xml` (copy it from
`DoNotBeLazy/About/About.xml`) if and when this mod actually patches
something.

**No `Defs/` folder.** Anything needing a `KeyBindingDef`, `LetterDef`
or similar creates the first XML in this mod.
