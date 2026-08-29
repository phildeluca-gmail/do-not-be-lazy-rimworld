using Verse;

namespace UninstallHotkey.Core
{
    // Scaffold, created 2026-08-29. Loads and does nothing else - there is
    // no agreed behaviour for this mod yet, only a name. See
    // UninstallHotkey_Architecture.md in the repo root.
    //
    // RimWorld constructs one instance of this per load, passing the
    // ModContentPack. The log line is the only thing here, and it exists so
    // "did the scaffold actually load" is answerable without guessing.
    public class UninstallHotkeyMod : Mod
    {
        public UninstallHotkeyMod(ModContentPack content) : base(content)
        {
            Log.Message("[UninstallHotkey] Loaded (scaffold - no behaviour yet).");
        }
    }
}
