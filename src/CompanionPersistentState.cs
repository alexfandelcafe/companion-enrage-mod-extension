using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace EnRAGECompanionsExtension
{
    /// <summary>
    /// Persisted, ped-handle-independent state for one enRAGE roster record.
    /// Handles and live game entities must never be stored here.
    /// </summary>
    [DataContract]
    public sealed class CompanionPersistentState
    {
        [DataMember(Order = 1)] public string RecordId { get; set; }
        [DataMember(Order = 2)] public CompanionFollowState FollowState { get; set; }
        [DataMember(Order = 3)] public CompanionMountPreference MountPreference { get; set; }
        [DataMember(Order = 4)] public CompanionCombatStance CombatStance { get; set; }
        [DataMember(Order = 5)] public CompanionSavedPosition SavedPosition { get; set; }
        [DataMember(Order = 6)] public CompanionInjuryState Injury { get; set; }
        [DataMember(Order = 7)] public int Affinity { get; set; }
        [DataMember(Order = 8)] public CompanionAppearanceState Appearance { get; set; }
        [DataMember(Order = 9)] public long LastUpdatedUtcTicks { get; set; }

        public CompanionPersistentState()
        {
            RecordId = string.Empty;
            FollowState = CompanionFollowState.Following;
            MountPreference = CompanionMountPreference.Automatic;
            CombatStance = CompanionCombatStance.FollowOrders;
            SavedPosition = new CompanionSavedPosition();
            Injury = new CompanionInjuryState();
            Affinity = 0;
            Appearance = new CompanionAppearanceState();
            LastUpdatedUtcTicks = DateTime.UtcNow.Ticks;
        }

        public void Normalize(string recordId)
        {
            RecordId = (recordId ?? RecordId ?? string.Empty).Trim();
            if (!Enum.IsDefined(typeof(CompanionFollowState), FollowState))
                FollowState = CompanionFollowState.Following;
            if (!Enum.IsDefined(typeof(CompanionMountPreference), MountPreference))
                MountPreference = CompanionMountPreference.Automatic;
            if (!Enum.IsDefined(typeof(CompanionCombatStance), CombatStance))
                CombatStance = CompanionCombatStance.FollowOrders;

            if (SavedPosition == null) SavedPosition = new CompanionSavedPosition();
            SavedPosition.Normalize();
            if (Injury == null) Injury = new CompanionInjuryState();
            Injury.Normalize();
            if (Appearance == null) Appearance = new CompanionAppearanceState();
            Appearance.Normalize();
            Affinity = Clamp(Affinity, 0, 100);
            if (LastUpdatedUtcTicks < 0 || LastUpdatedUtcTicks > DateTime.MaxValue.Ticks)
                LastUpdatedUtcTicks = DateTime.UtcNow.Ticks;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }

    [DataContract]
    public enum CompanionFollowState
    {
        [EnumMember] Following = 0,
        [EnumMember] Waiting = 1,
        [EnumMember] Resting = 2,
        [EnumMember] ReturningToPosition = 3,
        [EnumMember] Dismissed = 4
    }

    [DataContract]
    public enum CompanionMountPreference
    {
        [EnumMember] Automatic = 0,
        [EnumMember] StayMounted = 1,
        [EnumMember] OnFoot = 2
    }

    [DataContract]
    public enum CompanionCombatStance
    {
        [EnumMember] FollowOrders = 0,
        [EnumMember] HoldPosition = 1,
        [EnumMember] CoverPlayer = 2,
        [EnumMember] DefendArea = 3,
        [EnumMember] FireOnSignal = 4
    }

    [DataContract]
    public sealed class CompanionSavedPosition
    {
        [DataMember(Order = 1)] public bool HasValue { get; set; }
        [DataMember(Order = 2)] public float X { get; set; }
        [DataMember(Order = 3)] public float Y { get; set; }
        [DataMember(Order = 4)] public float Z { get; set; }
        [DataMember(Order = 5)] public float Heading { get; set; }

        public void Normalize()
        {
            if (!IsFinite(X) || !IsFinite(Y) || !IsFinite(Z) || !IsFinite(Heading))
            {
                HasValue = false;
                X = Y = Z = Heading = 0f;
                return;
            }

            Heading = Heading % 360f;
            if (Heading < 0f) Heading += 360f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    [DataContract]
    public sealed class CompanionInjuryState
    {
        [DataMember(Order = 1)] public bool IsInjured { get; set; }
        [DataMember(Order = 2)] public int Severity { get; set; }
        [DataMember(Order = 3)] public double RecoverySecondsRemaining { get; set; }
        [DataMember(Order = 4)] public long FirstInjuredUtcTicks { get; set; }

        public void Normalize()
        {
            Severity = Severity < 0 ? 0 : (Severity > 100 ? 100 : Severity);
            if (double.IsNaN(RecoverySecondsRemaining) || double.IsInfinity(RecoverySecondsRemaining))
                RecoverySecondsRemaining = 0;
            if (RecoverySecondsRemaining < 0) RecoverySecondsRemaining = 0;
            if (RecoverySecondsRemaining > 604800) RecoverySecondsRemaining = 604800;
            if (FirstInjuredUtcTicks < 0 || FirstInjuredUtcTicks > DateTime.MaxValue.Ticks)
                FirstInjuredUtcTicks = 0;
            if (!IsInjured)
            {
                Severity = 0;
                RecoverySecondsRemaining = 0;
                FirstInjuredUtcTicks = 0;
            }
        }
    }

    [DataContract]
    public sealed class CompanionAppearanceState
    {
        // Imported from EnRAGECompanionsExtension.outfits.txt during migration.
        [DataMember(Order = 1)] public bool HasLegacyOutfitHash { get; set; }
        [DataMember(Order = 2)] public uint LegacyOutfitHash { get; set; }
        // Separate slots prevent treating an outfit hash as a full wardrobe or weapon loadout.
        [DataMember(Order = 3)] public List<CompanionClothingComponent> ClothingComponents { get; set; }
        [DataMember(Order = 4)] public uint? HatComponentHash { get; set; }
        [DataMember(Order = 5)] public List<uint> WeaponHashes { get; set; }

        public CompanionAppearanceState()
        {
            ClothingComponents = new List<CompanionClothingComponent>();
            WeaponHashes = new List<uint>();
        }

        public void Normalize()
        {
            if (ClothingComponents == null) ClothingComponents = new List<CompanionClothingComponent>();
            if (WeaponHashes == null) WeaponHashes = new List<uint>();
            if (ClothingComponents.Count > 64) ClothingComponents.RemoveRange(64, ClothingComponents.Count - 64);
            if (WeaponHashes.Count > 32) WeaponHashes.RemoveRange(32, WeaponHashes.Count - 32);
            ClothingComponents.RemoveAll(x => x == null || x.ComponentId < 0 || x.ComponentId > 255);
        }
    }

    [DataContract]
    public sealed class CompanionClothingComponent
    {
        [DataMember(Order = 1)] public int ComponentId { get; set; }
        [DataMember(Order = 2)] public uint DrawableHash { get; set; }
        [DataMember(Order = 3)] public uint TextureHash { get; set; }
        [DataMember(Order = 4)] public uint PaletteHash { get; set; }
    }
}
