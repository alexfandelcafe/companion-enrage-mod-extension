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
        public const int CurrentSchemaVersion = 1;
        public const string StateFileName = "EnRAGECompanionsExtension.companions.json";
        public const string LegacyOutfitFileName = "EnRAGECompanionsExtension.outfits.txt";

        private readonly string _statePath;
        private readonly string _legacyOutfitPath;
        private readonly Dictionary<string, CompanionPersistentState> _states =
            new Dictionary<string, CompanionPersistentState>(StringComparer.Ordinal);
        private bool _dirty;

        public event Action<string> Diagnostic;

        public CompanionStateStore(string scriptsDirectory)
        {
            if (string.IsNullOrWhiteSpace(scriptsDirectory))
                throw new ArgumentException("Scripts directory is required.", nameof(scriptsDirectory));

            Directory.CreateDirectory(scriptsDirectory);
            _statePath = Path.Combine(scriptsDirectory, StateFileName);
            _legacyOutfitPath = Path.Combine(scriptsDirectory, LegacyOutfitFileName);
            Load();
        }

        public int Count { get { return _states.Count; } }
        public bool IsDirty { get { return _dirty; } }

        public CompanionPersistentState GetOrCreate(string recordId)
        {
            string id = NormalizeId(recordId);
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
                    Emit("Loaded " + _states.Count + " companion state record(s), schema " + document.SchemaVersion + ".");
                }
                catch (Exception ex)
                {
                    QuarantineCorruptStateFile();
                    Emit("Could not load companion state; started with an empty state store. Details: " + ex.Message);
                }
            }

            // Migration is additive: the old file is read but never deleted or rewritten.
            ImportLegacyOutfits();
        }

        private void ImportLegacyOutfits()
        {
            if (!File.Exists(_legacyOutfitPath)) return;

            int imported = 0;
            try
            {
                foreach (string line in File.ReadAllLines(_legacyOutfitPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int separator = line.IndexOf('|');
                    if (separator <= 0 || separator == line.Length - 1) continue;

                    string id = NormalizeId(line.Substring(0, separator));
                    string hashText = line.Substring(separator + 1).Trim();
                    uint hash;
                    if (id.Length == 0 || !uint.TryParse(hashText,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out hash))
                        continue;

                    CompanionPersistentState state = GetOrCreate(id);
                    if (!state.Appearance.HasLegacyOutfitHash)
                    {
                        state.Appearance.HasLegacyOutfitHash = true;
                        state.Appearance.LegacyOutfitHash = hash;
                        state.Normalize(id);
                        _dirty = true;
                        imported++;
                    }
                }
                if (imported > 0)
                    Emit("Imported " + imported + " legacy outfit hash record(s); original file retained.");
            }
            catch (Exception ex)
            {
                Emit("Could not import legacy outfit file: " + ex.Message);
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
