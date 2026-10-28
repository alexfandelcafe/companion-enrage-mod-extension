using System;
using System.Diagnostics;
using System.IO;

namespace EnRAGECompanionsExtension
{
    /// <summary>
    /// Owns the session-wide persistent state store. Runtime entity handles remain in
    /// EnRAGEHooks; this store contains only stable record IDs and serializable values.
    /// </summary>
    internal static class CompanionStateRuntime
    {
        private static readonly object Sync = new object();
        private static CompanionStateStore _store;

        public static CompanionStateStore Store
        {
            get { lock (Sync) return _store; }
        }

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_store != null) return;

                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string scriptsDirectory = string.Equals(
                    new DirectoryInfo(baseDirectory).Name, "scripts", StringComparison.OrdinalIgnoreCase)
                    ? baseDirectory
                    : Path.Combine(baseDirectory, "scripts");

                try
                {
                    var store = new CompanionStateStore(scriptsDirectory,
                        message => Trace.WriteLine("[EnRAGECompanionsExtension] " + message));
                    _store = store;

                    // Writes only when migration or a newly-created state made the store dirty.
                    // No per-frame disk I/O.
                    if (_store.IsDirty)
                        _store.SaveIfDirty();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("[EnRAGECompanionsExtension] Persistent state initialization failed: " + ex);
                    _store = null;
                }
            }
        }
    }
}
