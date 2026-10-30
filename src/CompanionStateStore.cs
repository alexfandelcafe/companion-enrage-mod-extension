using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EnRAGECompanionsExtension
{
    /// <summary>
    /// Versioned, atomic-on-best-effort persistence for companion state.
    /// All keys are enRAGE record IDs, never transient game entity handles.
    /// Callers should save only after state changes, not every game tick.
    /// </summary>
    public sealed class CompanionStateStore
    {
        public const int CurrentSchemaVersion = 3;
        public const string StateFileName = "EnRAGECompanionsExtension.companions.json";

        private readonly string _statePath;
        private readonly Dictionary<string, CompanionPersistentState> _states =
            new Dictionary<string, CompanionPersistentState>(StringComparer.Ordinal);
        private bool _dirty;

        public event Action<string> Diagnostic;

        public CompanionStateStore(string scriptsDirectory, Action<string> diagnostic = null)
        {
            if (string.IsNullOrWhiteSpace(scriptsDirectory))
                throw new ArgumentException("Scripts directory is required.", nameof(scriptsDirectory));

            if (diagnostic != null) Diagnostic += diagnostic;
            Directory.CreateDirectory(scriptsDirectory);
            _statePath = Path.Combine(scriptsDirectory, StateFileName);
            Load();
        }

        public int Count { get { return _states.Count; } }
        public bool IsDirty { get { return _dirty; } }

        public CompanionPersistentState GetOrCreate(string recordId)
        {
            string id = NormalizeId(recordId);
            if (id.Length == 0)
                throw new ArgumentException("Persistent companion state requires a record ID.", nameof(recordId));

            CompanionPersistentState state;
            if (_states.TryGetValue(id, out state))
                return state;

            state = new CompanionPersistentState();
            state.Normalize(id);
            _states.Add(id, state);
            _dirty = true;
            return state;
        }

        public bool TryGet(string recordId, out CompanionPersistentState state)
        {
            string id = NormalizeId(recordId);
            if (id.Length == 0)
            {
                state = null;
                return false;
            }
            return _states.TryGetValue(id, out state);
        }

        public bool Remove(string recordId)
        {
            string id = NormalizeId(recordId);
            if (id.Length == 0 || !_states.Remove(id))
                return false;

            _dirty = true;
            return true;
        }

        public void MarkChanged(CompanionPersistentState state)
        {
            if (state == null)
                return;

            string id = NormalizeId(state.RecordId);
            if (id.Length == 0)
                throw new ArgumentException("Persistent companion state requires a record ID.", nameof(state));

            state.Normalize(id);
            state.LastUpdatedUtcTicks = DateTime.UtcNow.Ticks;
            _states[id] = state;
            _dirty = true;
        }

        public bool SaveIfDirty()
        {
            if (!_dirty) return true;
            return Save();
        }

        public bool Save()
        {
            string tempPath = _statePath + ".tmp";
            string backupPath = _statePath + ".bak";

            try
            {
                var document = new CompanionStateDocument();
                document.SchemaVersion = CurrentSchemaVersion;
                document.SavedUtcTicks = DateTime.UtcNow.Ticks;
                document.Companions = _states.Values
                    .OrderBy(x => x.RecordId, StringComparer.Ordinal)
                    .ToList();

                foreach (CompanionPersistentState state in document.Companions)
                    state.Normalize(state.RecordId);

                var serializer = new DataContractJsonSerializer(typeof(CompanionStateDocument));
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.WriteObject(stream, document);
                    stream.Flush(true);
                }

                if (File.Exists(_statePath))
                {
                    try
                    {
                        if (File.Exists(backupPath)) File.Delete(backupPath);
                        File.Replace(tempPath, _statePath, backupPath, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceWithFallback(tempPath, backupPath);
                    }
                    catch (IOException)
                    {
                        ReplaceWithFallback(tempPath, backupPath);
                    }
                }
                else
                {
                    File.Move(tempPath, _statePath);
                }

                _dirty = false;
                Emit("Persisted " + _states.Count + " companion state record(s).");
                return true;
            }
            catch (Exception ex)
            {
                Emit("Could not save companion state: " + ex.Message);
                TryDelete(tempPath);
                return false;
            }
        }

        private void Load()
        {
            if (File.Exists(_statePath))
            {
                try
                {
                    var serializer = new DataContractJsonSerializer(typeof(CompanionStateDocument));
                    CompanionStateDocument document;
                    using (var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        document = serializer.ReadObject(stream) as CompanionStateDocument;

                    if (document == null)
                        throw new InvalidDataException("State document was empty.");

                    // Older development builds used {"Version":1,"Records":[...]}. Detect and
                    // import that documented legacy shape before treating a missing SchemaVersion
                    // as corruption; keep the original file intact until the migrated JSON is saved.
                    if (document.SchemaVersion == 0 && TryImportLegacyStateDocument())
                    {
                        Emit("Migrated legacy companion state JSON into schema " + CurrentSchemaVersion + ".");
                    }
                    else
                    {
                        if (document.SchemaVersion < 1 || document.SchemaVersion > CurrentSchemaVersion)
                            throw new InvalidDataException("Unsupported companion state schema version: " + document.SchemaVersion);

                        if (document.Companions != null)
                        {
                            foreach (CompanionPersistentState state in document.Companions)
                            {
                                if (state == null) continue;
                                string id = NormalizeId(state.RecordId);
                                if (id.Length == 0) continue;
                                state.Normalize(id);
                                _states[id] = state;
                            }
                        }
                        if (document.SchemaVersion < CurrentSchemaVersion)
                        {
                            // Rewrite older records through the reduced schema on startup. Fields
                            // not defined by the current model are dropped from the active JSON.
                            _dirty = true;
                            Emit("Queued companion state migration from schema " + document.SchemaVersion +
                                " to schema " + CurrentSchemaVersion + ".");
                        }
                        Emit("Loaded " + _states.Count + " companion state record(s), schema " + document.SchemaVersion + ".");
                    }
                }
                catch (Exception ex)
                {
                    // Some serializer/runtime versions reject the older field set before its
                    // missing schema can be checked. Give the explicit legacy importer one chance
                    // before quarantining a potentially valid Version/Records save.
                    if (TryImportLegacyStateDocument())
                    {
                        Emit("Migrated legacy companion state JSON into schema " + CurrentSchemaVersion + " after compatibility fallback.");
                    }
                    else if (TryRestoreBackupState())
                    {
                        Emit("Recovered companion state from the backup after the primary state file could not be read.");
                    }
                    else
                    {
                        _states.Clear();
                        QuarantineCorruptStateFile();
                        Emit("Could not load companion state or its backup; started with an empty state store. Details: " + ex.Message);
                    }
                }
            }
            else
            {
                // A crash can leave the primary file missing after the temp-file replacement path.
                // Restore the last known-good snapshot rather than starting an empty roster state.
                TryRestoreBackupState();
            }

        }

        private bool TryRestoreBackupState()
        {
            string backupPath = _statePath + ".bak";
            if (!File.Exists(backupPath)) return false;

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(CompanionStateDocument));
                CompanionStateDocument document;
                using (var stream = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    document = serializer.ReadObject(stream) as CompanionStateDocument;

                if (document == null || document.SchemaVersion < 1 ||
                    document.SchemaVersion > CurrentSchemaVersion)
                    throw new InvalidDataException("Backup state schema is invalid or unsupported.");

                var recoveredStates = new List<CompanionPersistentState>();
                if (document.Companions != null)
                {
                    foreach (CompanionPersistentState state in document.Companions)
                    {
                        if (state == null) continue;
                        string id = NormalizeId(state.RecordId);
                        if (id.Length == 0) continue;
                        state.Normalize(id);
                        recoveredStates.Add(state);
                    }
                }

                // Preserve the unreadable primary file before replacing it. If it cannot be
                // moved, try copying it aside so the last recoverable bytes are not discarded.
                if (File.Exists(_statePath))
                {
                    QuarantineCorruptStateFile();
                    if (File.Exists(_statePath))
                    {
                        string quarantineCopy = _statePath + ".corrupt-copy-" +
                            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                        File.Copy(_statePath, quarantineCopy, false);
                    }
                }

                // Copy, rather than move, so the known-good backup remains available if the
                // process exits before the recovered primary file is next saved.
                File.Copy(backupPath, _statePath, true);

                _states.Clear();
                foreach (CompanionPersistentState state in recoveredStates)
                    _states[state.RecordId] = state;
                // A restored schema-1 backup is immediately rewritten into the reduced schema 2
                // by CompanionStateRuntime, so removed fields do not persist in the active file.
                _dirty = document.SchemaVersion < CurrentSchemaVersion;
                Emit("Recovered " + _states.Count + " companion state record(s) from " +
                    Path.GetFileName(backupPath) + ( _dirty ? "; schema migration queued." : "."));
                return true;
            }
            catch (Exception ex)
            {
                Emit("Could not recover companion state backup: " + ex.Message);
                return false;
            }
        }

        private bool TryImportLegacyStateDocument()
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(LegacyCompanionStateDocument));
                LegacyCompanionStateDocument legacy;
                using (var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    legacy = serializer.ReadObject(stream) as LegacyCompanionStateDocument;

                if (legacy == null || legacy.Version < 1 || legacy.Records == null)
                    return false;

                foreach (LegacyCompanionStateRecord old in legacy.Records)
                {
                    if (old == null) continue;
                    string id = NormalizeId(old.Id);
                    if (id.Length == 0) continue;

                    CompanionPersistentState state;
                    if (!_states.TryGetValue(id, out state))
                        state = new CompanionPersistentState();

                    state.RecordId = id;
                    state.RosterOrder = old.Order;
                    if (Enum.IsDefined(typeof(CompanionCombatStance), old.Stance))
                        state.CombatStance = (CompanionCombatStance)old.Stance;
                    if (Enum.IsDefined(typeof(CompanionMountPreference), old.MountPolicy))
                        state.MountPreference = (CompanionMountPreference)old.MountPolicy;

                    state.SavedPosition = new CompanionSavedPosition
                    {
                        HasValue = old.HasAnchor,
                        X = old.AnchorX,
                        Y = old.AnchorY,
                        Z = old.AnchorZ,
                        Heading = 0f
                    };

                    int severity = old.InjurySeverity < 0 ? 0 : (old.InjurySeverity > 100 ? 100 : old.InjurySeverity);
                    state.Injury = new CompanionInjuryState
                    {
                        IsInjured = severity > 0 || old.InjuryStartedUtcTicks > 0,
                        Severity = severity,
                        RecoverySecondsRemaining = 0,
                        FirstInjuredUtcTicks = old.InjuryStartedUtcTicks
                    };
                    state.Affinity = old.Affinity;

                    state.Normalize(id);
                    _states[id] = state;
                }

                _dirty = true;
                Emit("Imported " + _states.Count + " legacy companion state record(s) from Version/Records format.");
                return true;
            }
            catch (Exception ex)
            {
                Emit("Legacy companion state migration failed: " + ex.Message);
                return false;
            }
        }

        private void QuarantineCorruptStateFile()
        {
            try
            {
                if (!File.Exists(_statePath)) return;
                string quarantine = _statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.Move(_statePath, quarantine);
                Emit("Moved unreadable state file to " + Path.GetFileName(quarantine) + ".");
            }
            catch (Exception ex)
            {
                Emit("Could not quarantine unreadable state file: " + ex.Message);
            }
        }

        private void ReplaceWithFallback(string tempPath, string backupPath)
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            if (File.Exists(_statePath)) File.Move(_statePath, backupPath);
            File.Move(tempPath, _statePath);
        }

        private static string NormalizeId(string recordId)
        {
            return (recordId ?? string.Empty).Trim();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* Keep the original state file; cleanup can be retried later. */ }
        }

        private void Emit(string message)
        {
            Action<string> handler = Diagnostic;
            if (handler == null) return;
            try { handler(message); }
            catch { /* Diagnostics must never break persistence. */ }
        }
    }

    [System.Runtime.Serialization.DataContract]
    internal sealed class LegacyCompanionStateDocument
    {
        [System.Runtime.Serialization.DataMember(Name = "Version", Order = 1)] public int Version { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "Records", Order = 2)] public List<LegacyCompanionStateRecord> Records { get; set; }
    }

    [System.Runtime.Serialization.DataContract]
    internal sealed class LegacyCompanionStateRecord
    {
        [System.Runtime.Serialization.DataMember] public string Id { get; set; }
        [System.Runtime.Serialization.DataMember] public int Order { get; set; }
        [System.Runtime.Serialization.DataMember] public int Stance { get; set; }
        [System.Runtime.Serialization.DataMember] public int MountPolicy { get; set; }
        [System.Runtime.Serialization.DataMember] public bool HasAnchor { get; set; }
        [System.Runtime.Serialization.DataMember] public float AnchorX { get; set; }
        [System.Runtime.Serialization.DataMember] public float AnchorY { get; set; }
        [System.Runtime.Serialization.DataMember] public float AnchorZ { get; set; }
        [System.Runtime.Serialization.DataMember] public int InjurySeverity { get; set; }
        [System.Runtime.Serialization.DataMember] public long InjuryStartedUtcTicks { get; set; }
        [System.Runtime.Serialization.DataMember] public int Affinity { get; set; }
    }

    [System.Runtime.Serialization.DataContract]
    internal sealed class CompanionStateDocument
    {
        [System.Runtime.Serialization.DataMember(Order = 1)] public int SchemaVersion { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 2)] public long SavedUtcTicks { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 3)] public List<CompanionPersistentState> Companions { get; set; }

        public CompanionStateDocument()
        {
            SchemaVersion = CompanionStateStore.CurrentSchemaVersion;
            SavedUtcTicks = DateTime.UtcNow.Ticks;
            Companions = new List<CompanionPersistentState>();
        }
    }
}
