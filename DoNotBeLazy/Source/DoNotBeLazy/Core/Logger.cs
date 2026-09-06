namespace DoNotBeLazy.Core
{
    // Named Logger (not Log) to avoid colliding with Verse.Log when both
    // namespaces are in scope.
    //
    // VerboseLogging is driven by the settings checkbox - set from
    // DoNotBeLazySettings on both ExposeData (load) and the toggle itself.
    // It sat hardcoded false from Phase 1 until 2026-08-16, which silently
    // made every Logger.Message call in the mod a no-op: several live
    // playtest logs showed zero DoNotBeLazy lines and were read as "nothing
    // fired" when the truth was "nothing could be printed".
    //
    // **Changed 2026-09-06: Message and Diag no longer touch Verse.Log at
    // all.** They write to this mod's own file - see Core/LogFile for the
    // reason, which is that `Verse.Log` silences the WHOLE GAME after 1000
    // messages and this mod is far and away the noisiest thing here. A
    // 3629-target CleanFilth sweep can no longer cost RimWorld its log.
    //
    // **The standing rule is unchanged and still applies.** "A log line that
    // can fire per target, per def or per click is a defect until proved
    // otherwise" was never really about the cap - it is about a trace nobody
    // can read. Writing 964 useless lines to our own file instead of
    // RimWorld's makes them cheaper, not useful.
    public static class Logger
    {
        public static bool VerboseLogging = false;

        // Separate switch from VerboseLogging on purpose - job-pipeline
        // diagnostics and the sweep trace answer different questions and
        // you rarely want both walls of text at once.
        public static bool JobDiagnostics = false;

        private const string Prefix = "[DoNotBeLazy] ";

        public static void Message(string text)
        {
            if (!VerboseLogging)
            {
                return;
            }

            if (LogFile.Available)
            {
                LogFile.Write("MSG ", text);
                return;
            }

            // The file could not be opened. Better to spend the cap than to
            // hand back an empty verbose session.
            Verse.Log.Message(Prefix + text);
        }

        // Same prefix and the same file as everything else, so one extraction
        // still catches the lot - the pull-logs command splits them apart
        // afterwards. The level tag is what tells them apart inside the file.
        public static void Diag(string text)
        {
            if (!JobDiagnostics)
            {
                return;
            }

            if (LogFile.Available)
            {
                LogFile.Write("DIAG", text);
                return;
            }

            Verse.Log.Message(Prefix + text);
        }

        // Warnings and errors go BOTH places: the file, so the mod's own log
        // is complete on its own and can be read without Player.log beside
        // it, and Verse.Log, because a broken mod has to be visible in-game.
        // There are few enough of these that the cap is not at risk.
        public static void Warning(string text)
        {
            LogFile.Write("WARN", text);
            Verse.Log.Warning(Prefix + text);
        }

        public static void Error(string text)
        {
            LogFile.Write("ERR ", text);
            Verse.Log.Error(Prefix + text);
        }
    }
}
