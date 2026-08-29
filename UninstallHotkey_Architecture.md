<!-- Architecture stub for Uninstall Hotkey. Created 2026-08-29 alongside the scaffold. Holds the verbatim ask and nothing agreed. NOTHING HERE IS A GO-AHEAD TO BUILD BEHAVIOUR. -->

# Uninstall Hotkey - Architecture

**Status: scaffold only. No behaviour is designed, costed or agreed.**

The folder `UninstallHotkey/` exists, builds clean, and loads in RimWorld printing
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

A `KeyBindingDef` that fires the Uninstall designator on the current
selection or the thing under the cursor, instead of going through the
architect menu.

This is the most self-explanatory of the five and probably the smallest.
It needs the first XML in the project - a `Defs/KeyBindings.xml` holding
a `KeyBindingDef` - plus somewhere to read the key each frame.

---

## 3. Questions to settle before this becomes a plan

- Does the key act on the current *selection*, or on whatever is under
  the *cursor*? Selection is simpler and matches how designators
  normally work.
- Does it toggle the Uninstall *designator* (so the next click
  designates), or immediately designate what is already selected? These
  feel different in the hand.
- Should it work on multiple selected buildings at once?
- What default key? It has to avoid colliding with vanilla bindings or
  with the ~60 mods in the modlist.
- Does "uninstall" also cover Deconstruct, or strictly Uninstall (which
  only applies to minifiable buildings)?

---

## 4. What exists now

```
UninstallHotkey/
  About/About.xml            <- packageId phildeluca.uninstallhotkey, no dependencies declared yet
  Source/UninstallHotkey/
    UninstallHotkey.csproj            <- net472, refs ../../../lib, outputs to UninstallHotkey/Assemblies/
    Core/UninstallHotkeyMod.cs        <- Mod subclass, one log line, nothing else
```

Add the Harmony `modDependencies` block to `About.xml` (copy it from
`DoNotBeLazy/About/About.xml`) if and when this mod actually patches
something.

**No `Defs/` folder.** Anything needing a `KeyBindingDef`, `LetterDef`
or similar creates the first XML in this mod.
