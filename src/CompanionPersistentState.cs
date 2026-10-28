using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace EnRAGECompanionsExtension
{
    internal enum CompanionOrder
    {
        Following = 0,
        Waiting = 1,
        ReturningToAnchor = 2,
        DismissedWaiting = 3
    }

    internal enum CompanionCombatStance
    {
        FollowOrders = 0,
        HoldPosition = 1,
        CoverPlayer = 2,
        ProtectArea = 3,
        FireOnSignal = 4
    }

    internal enum CompanionMountPolicy
    {
        Normal = 0,
        StayMounted = 1
    }

    [DataContract]
    internal sealed class CompanionStateDocument
    {
        public const int CurrentVersion = 1;

        [DataMember(Order = 1)]
        public int Version { get; set; } = CurrentVersion;

        [DataMember(Order = 2)]
        public List<CompanionPersistentState> Records { get; set; } = new List<CompanionPersistentState>();
    }

    [DataContract]
    internal sealed class CompanionPersistentState
    {
        [DataMember(Order = 1)]
        public string Id { get; set; }

        [DataMember(Order = 2)]
        public CompanionOrder Order { get; set; } = CompanionOrder.Following;

        [DataMember(Order = 3)]
        public CompanionCombatStance Stance { get; set; } = CompanionCombatStance.FollowOrders;

        [DataMember(Order = 4)]
        public CompanionMountPolicy MountPolicy { get; set; } = CompanionMountPolicy.Normal;

        [DataMember(Order = 5)]
        public bool HasAnchor { get; set; }

        [DataMember(Order = 6)]
        public float AnchorX { get; set; }

        [DataMember(Order = 7)]
        public float AnchorY { get; set; }

        [DataMember(Order = 8)]
        public float AnchorZ { get; set; }

        [DataMember(Order = 9)]
        public int InjurySeverity { get; set; }

        [DataMember(Order = 10)]
        public long InjuryStartedUtcTicks { get; set; }

        [DataMember(Order = 11)]
        public long RestStartedUtcTicks { get; set; }

        [DataMember(Order = 12)]
        public int Affinity { get; set; } = 50;

        // The legacy outfits.txt remains supported. This field is the migration target and is not
        // treated as a substitute for per-component clothing, hat, or weapon data.
        [DataMember(Order = 13)]
        public uint MetaOutfitHash { get; set; }

        [DataMember(Order = 14)]
        public List<ClothingComponentState> ClothingComponents { get; set; } = new List<ClothingComponentState>();

        [DataMember(Order = 15)]
        public uint HatComponentHash { get; set; }

        [DataMember(Order = 16)]
        public uint LongGunHash { get; set; }

        [DataMember(Order = 17)]
        public uint SidearmHash { get; set; }
    }

    [DataContract]
    internal sealed class ClothingComponentState
    {
        [DataMember(Order = 1)]
        public int ComponentId { get; set; }

        [DataMember(Order = 2)]
        public uint DrawableHash { get; set; }

        [DataMember(Order = 3)]
        public int TextureIndex { get; set; }

        [DataMember(Order = 4)]
        public int Palette { get; set; }
    }

    /// <summary>
    /// Versioned JSON store keyed by the stable enRAGE roster record ID, never by a transient ped handle.
    /// Saves are performed on state changes; a failed replacement leaves the previous file intact.
    /// </summary>
    internal sealed class CompanionStateStore
    {
        private readonly string _path;
        private readonly Action<string> _log;
        private readonly Dictionary<string, CompanionPersistentState> _records =
            new Dictionary<string, CompanionPersistentState>(StringComparer.OrdinalIgnoreCase);
        private bool _readOnlyDueToFutureVersion;
        private bool _dirty;

        internal CompanionStateStore(string path, Action<string> log)
        {
            _path = path;
            _log = log ?? delegate { };
            Load();
        }

        internal IEnumerable<CompanionPersistentState> Records
        {
            get { return _records.Values; }
        }

        internal CompanionPersistentState Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            CompanionPersistentState state;
            if (_records.TryGetValue(id.Trim(), out state))
                return state;

            state = new CompanionPersistentState { Id = id.Trim() };
            _records[state.Id] = state;
            _dirty = true;
            return state;
        }

        internal bool TryGet(string id, out CompanionPersistentState state)
        {
            state = null;
            return !string.IsNullOrWhiteSpace(id) && _records.TryGetValue(id.Trim(), out state);
        }

        internal void ImportLegacyOutfits(string legacyPath)
        {
            if (string.IsNullOrWhiteSpace(legacyPath) || !File.Exists(legacyPath))
                return;

            int imported = 0;
            try
            {
                foreach (string raw in File.ReadAllLines(legacyPath))
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) ||
                        line.StartsWith(";", StringComparison.Ordinal))
                        continue;

                    int separator = line.IndexOf('|');
                    if (separator <= 0 || separator >= line.Length - 1)
                        continue;

                    string id = line.Substring(0, separator).Trim();
                    uint hash;
                    if (id.Length == 0 ||
                        !uint.TryParse(line.Substring(separator + 1).Trim(),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out hash) ||
                        hash == 0U)
                        continue;

                    CompanionPersistentState state = Get(id);
                    if (state.MetaOutfitHash == 0U)
                    {
                        state.MetaOutfitHash = hash;
                        imported++;
                        _dirty = true;
                    }
                }

                if (imported > 0)
                {
                    _log("Migrated " + imported + " legacy outfit hash(es) into the versioned companion state store; original outfits.txt was retained.");
                    SaveIfDirty();
                }
            }
            catch (Exception ex)
            {
                _log("Legacy outfit migration warning: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal void SaveIfDirty()
        {
            if (!_dirty || _readOnlyDueToFutureVersion)
                return;

            string directory = Path.GetDirectoryName(_path);
            string temporary = _path + ".tmp";
            string backup = _path + ".bak";

            try
            {
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                CompanionStateDocument document = new CompanionStateDocument
                {
                    Version = CompanionStateDocument.CurrentVersion,
                    Records = _records.Values
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CompanionStateDocument));
                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.WriteObject(stream, document);
                    stream.Flush(true);
                }

                if (File.Exists(_path))
                {
                    try
                    {
                        if (File.Exists(backup))
                            File.Delete(backup);
                        File.Replace(temporary, _path, backup, true);
                    }
                    catch (Exception replaceError)
                    {
                        // Do not delete or truncate the old file if an atomic replace is unsupported.
                        _log("Atomic companion-state replacement failed; previous file was retained. " +
                            replaceError.GetType().Name + ": " + replaceError.Message);
                        return;
                    }
                }
                else
                {
                    File.Move(temporary, _path);
                }

                _dirty = false;
            }
            catch (Exception ex)
            {
                _log("Could not save companion-state file: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        internal void MarkDirty()
        {
            _dirty = true;
        }

        private void Load()
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
                return;

            try
            {
                CompanionStateDocument document;
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CompanionStateDocument));
                using (FileStream stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    document = serializer.ReadObject(stream) as CompanionStateDocument;

                if (document == null)
                    throw new SerializationException("The JSON document was empty or did not contain a state document.");

                if (document.Version > CompanionStateDocument.CurrentVersion)
                {
                    _readOnlyDueToFutureVersion = true;
                    _log("Companion state schema version " + document.Version +
                        " is newer than this extension supports. File was opened read-only to prevent data loss.");
                    return;
                }

                foreach (CompanionPersistentState state in document.Records ?? new List<CompanionPersistentState>())
                {
                    if (state == null || string.IsNullOrWhiteSpace(state.Id))
                        continue;
                    Normalize(state);
                    _records[state.Id.Trim()] = state;
                }

                _log("Loaded versioned companion state: " + _records.Count + " record(s), schema " + document.Version + ".");
                if (document.Version < CompanionStateDocument.CurrentVersion)
                {
                    _dirty = true;
                    SaveIfDirty();
                }
            }
            catch (Exception ex)
            {
                // Keep the corrupt file as evidence and start with empty in-memory state. Never delete it.
                string quarantine = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(_path, quarantine, false); } catch { }
                _log("Could not read companion-state file; the original was retained and a safe empty state was started. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Normalize(CompanionPersistentState state)
        {
            if (!Enum.IsDefined(typeof(CompanionOrder), state.Order))
                state.Order = CompanionOrder.Following;
            if (!Enum.IsDefined(typeof(CompanionCombatStance), state.Stance))
                state.Stance = CompanionCombatStance.FollowOrders;
            if (!Enum.IsDefined(typeof(CompanionMountPolicy), state.MountPolicy))
                state.MountPolicy = CompanionMountPolicy.Normal;
            state.InjurySeverity = Math.Max(0, Math.Min(2, state.InjurySeverity));
            state.Affinity = Math.Max(0, Math.Min(100, state.Affinity));
            if (state.ClothingComponents == null)
                state.ClothingComponents = new List<ClothingComponentState>();
            state.ClothingComponents = state.ClothingComponents
                .Where(x => x != null && x.ComponentId >= 0 && x.ComponentId <= 64)
                .GroupBy(x => x.ComponentId)
                .Select(x => x.Last())
                .ToList();
        }
    }
}
