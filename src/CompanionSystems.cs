using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RDR2;
using RDR2.Native;

namespace EnRAGECompanionsExtension
{
    /// <summary>
    /// Per-roster-ID state machine and native adapter for extension-managed companions.
    /// Entity handles are transient indexes only; persistent state is keyed by NemesisRecord.Id.
    /// </summary>
    internal static class CompanionSystems
    {
        private static CompanionStateStore _store;
        private static CompanionExtensionSettings _settings = new CompanionExtensionSettings();
        private static Action<string> _log = delegate { };
        private static readonly Dictionary<string, int> LastPedHealth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastPedHandle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastRecordTick = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> DamageWindowStartedAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> DamageWindowTotal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastTravelTick = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> TravelAccumulatorMs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> TravelDistanceMeters = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, RDR2.Math.Vector3> LastTravelPlayerPosition =
            new Dictionary<string, RDR2.Math.Vector3>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastAnchorTaskAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastStandStillPedHandle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastCombatTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> LastCombatAffinityUtc = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> MoveRateApplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> MoveRatePedHandles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static MethodInfo _moveRateNative;
        private static MethodInfo _shootingNative;
        private static MethodInfo _standStillNative;
        private static MethodInfo _goStraightNative;
        private static MethodInfo _blockingEventsNative;
        private static bool _nativeLookupDone;
        private static bool _moveRateWarningLogged;
        private static bool _shootingWarningLogged;
        private static bool _standStillWarningLogged;
        private static bool _goStraightWarningLogged;
        private static bool _blockingWarningLogged;
        private static int _fireSignalUntil;
        private static int _lastFireSignalProbeAt = -100000;
        private static bool _shootingNativeAvailable;

        internal static CompanionExtensionSettings Settings { get { return _settings; } }

        internal static void Initialize(string statePath, string outfitPath, string iniPath, Action<string> log)
        {
            if (_store != null)
                return;

            _log = log ?? delegate { };
            _settings = CompanionExtensionSettings.Load(iniPath, _log);
            _store = new CompanionStateStore(statePath, _log);
            _store.ImportLegacyOutfits(outfitPath);
            ResolveOptionalNatives();

            _log("Companion systems initialized. orders=" + _settings.EnableIndividualOrders +
                ", injuries=" + _settings.EnableInjurySystem + ", affinity=" + _settings.EnableAffinity +
                ", combat stances=" + _settings.EnableCombatStances + ".");
        }

        internal static CompanionPersistentState GetState(string id)
        {
            if (_store == null || string.IsNullOrWhiteSpace(id))
                return null;

            CompanionPersistentState state;
            if (_store.TryGet(id, out state))
                return state;

            state = _store.Get(id);
            _store.SaveIfDirty();
            return state;
        }

        internal static CompanionOrder GetOrder(string id)
        {
            CompanionPersistentState state = GetState(id);
            return state == null ? CompanionOrder.Following : state.Order;
        }

        internal static CompanionCombatStance GetStance(string id)
        {
            CompanionPersistentState state = GetState(id);
            return state == null ? CompanionCombatStance.FollowOrders : state.Stance;
        }

        internal static CompanionMountPolicy GetMountPolicy(string id)
        {
            CompanionPersistentState state = GetState(id);
            return state == null ? CompanionMountPolicy.Normal : state.MountPolicy;
        }

        internal static int GetAffinity(string id)
        {
            CompanionPersistentState state = GetState(id);
            return state == null ? 50 : Math.Max(0, Math.Min(100, state.Affinity));
        }

        internal static bool IsInjured(string id)
        {
            CompanionPersistentState state = GetState(id);
            return state != null && _settings.EnableInjurySystem && state.InjurySeverity > 0;
        }

        internal static bool HasAnchor(string id)
        {
            CompanionPersistentState state = GetState(id);
            return state != null && state.HasAnchor;
        }

        internal static bool TryGetMetaOutfitHash(string id, out uint hash)
        {
            hash = 0U;
            CompanionPersistentState state = GetState(id);
            if (state == null || state.MetaOutfitHash == 0U)
                return false;
            hash = state.MetaOutfitHash;
            return true;
        }

        internal static void SetMetaOutfitHash(string id, uint hash)
        {
            if (!_settings.EnableAppearancePersistence || string.IsNullOrWhiteSpace(id) || hash == 0U)
                return;
            CompanionPersistentState state = GetState(id);
            if (state == null || state.MetaOutfitHash == hash)
                return;
            state.MetaOutfitHash = hash;
            Save();
        }

        internal static void SetWaitHere(string id, Ped ped)
        {
            if (!_settings.EnableIndividualOrders || !_settings.EnableWaitMode || !IsUsablePed(ped))
                return;
            CompanionPersistentState state = GetState(id);
            if (state == null) return;

            CaptureAnchor(state, ped);
            state.Order = CompanionOrder.Waiting;
            if (state.RestStartedUtcTicks == 0L)
                state.RestStartedUtcTicks = DateTime.UtcNow.Ticks;
            LastStandStillPedHandle.Remove(id);
            LastAnchorTaskAt.Remove(id);
            TryClearTasks(ped);
            TryStandStill(ped, id);
            Save();
        }

        internal static void SetFollowing(string id, Ped ped)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            CompanionPersistentState state = GetState(id);
            if (state == null) return;
            state.Order = CompanionOrder.Following;
            state.RestStartedUtcTicks = 0L;
            if (state.Stance == CompanionCombatStance.HoldPosition ||
                state.Stance == CompanionCombatStance.ProtectArea)
                state.Stance = CompanionCombatStance.FollowOrders;
            LastStandStillPedHandle.Remove(id);
            LastAnchorTaskAt.Remove(id);
            if (IsUsablePed(ped))
            {
                try { ped.BlockPermanentEvents = false; } catch { }
                TryClearTasks(ped);
            }
            Save();
        }

        internal static void ToggleWaitFollow(string id, Ped ped)
        {
            CompanionPersistentState state = GetState(id);
            if (state == null) return;
            if (state.Order == CompanionOrder.Following &&
                state.Stance != CompanionCombatStance.HoldPosition &&
                state.Stance != CompanionCombatStance.ProtectArea)
                SetWaitHere(id, ped);
            else
                SetFollowing(id, ped);
        }

        internal static void OnDismissed(string id, Ped ped)
        {
            if (string.IsNullOrWhiteSpace(id) || !IsUsablePed(ped))
                return;

            CompanionPersistentState state = GetState(id);
            if (state == null) return;
            CaptureAnchor(state, ped);
            state.Order = CompanionOrder.DismissedWaiting;
            state.RestStartedUtcTicks = DateTime.UtcNow.Ticks;
            LastStandStillPedHandle.Remove(id);
            LastAnchorTaskAt.Remove(id);
            TryClearTasks(ped);
            TrySetBlockingEvents(ped, true);
            if (ped.IsOnMount)
            {
                // Retain the mount entity but let the released companion settle on foot near the saved anchor.
                try { TASK.TASK_DISMOUNT_ANIMAL(ped.Handle, 0, 0UL, 0UL, 0UL, 0); }
                catch (Exception ex) { _log("Dismissed companion dismount warning for " + id + ": " + ex.Message); }
            }
            else
                TryStandStill(ped, id);
            Save();
        }

        internal static void OnCalled(string id, Ped ped)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            CompanionPersistentState state = GetState(id);
            if (state == null) return;

            state.Order = CompanionOrder.Following;
            state.RestStartedUtcTicks = 0L;
            if (state.Stance == CompanionCombatStance.HoldPosition ||
                state.Stance == CompanionCombatStance.ProtectArea)
                state.Stance = CompanionCombatStance.FollowOrders;
            LastStandStillPedHandle.Remove(id);
            LastAnchorTaskAt.Remove(id);
            if (IsUsablePed(ped))
                TrySetBlockingEvents(ped, false);
            Save();
        }

        internal static void SaveAnchor(string id, Ped ped)
        {
            if (!IsUsablePed(ped)) return;
            CompanionPersistentState state = GetState(id);
            if (state == null) return;
            CaptureAnchor(state, ped);
            Save();
        }

        internal static void ReturnToAnchor(string id, Ped ped)
        {
            CompanionPersistentState state = GetState(id);
            if (state == null || !state.HasAnchor || !IsUsablePed(ped))
                return;
            state.Order = CompanionOrder.ReturningToAnchor;
            state.RestStartedUtcTicks = 0L;
            LastStandStillPedHandle.Remove(id);
            LastAnchorTaskAt[id] = -100000;
            TryClearTasks(ped);
            Save();
        }

        internal static void SetMountPolicy(string id, CompanionMountPolicy policy)
        {
            CompanionPersistentState state = GetState(id);
            if (state == null) return;
            state.MountPolicy = policy;
            Save();
        }

        internal static void CycleStance(string id, Ped ped)
        {
            if (!_settings.EnableCombatStances || string.IsNullOrWhiteSpace(id))
                return;

            CompanionPersistentState state = GetState(id);
            if (state == null) return;
            int next = ((int)state.Stance + 1) % Enum.GetValues(typeof(CompanionCombatStance)).Length;
            state.Stance = (CompanionCombatStance)next;

            if (state.Stance == CompanionCombatStance.HoldPosition ||
                state.Stance == CompanionCombatStance.ProtectArea)
            {
                if (IsUsablePed(ped)) CaptureAnchor(state, ped);
                state.Order = CompanionOrder.Waiting;
                if (state.RestStartedUtcTicks == 0L)
                    state.RestStartedUtcTicks = DateTime.UtcNow.Ticks;
                LastStandStillPedHandle.Remove(id);
                LastAnchorTaskAt.Remove(id);
                if (IsUsablePed(ped))
                {
                    TryClearTasks(ped);
                    TryStandStill(ped, id);
                }
            }
            else if (state.Order == CompanionOrder.Waiting)
            {
                state.Order = CompanionOrder.Following;
                state.RestStartedUtcTicks = 0L;
                LastStandStillPedHandle.Remove(id);
                LastAnchorTaskAt.Remove(id);
                if (IsUsablePed(ped)) TryClearTasks(ped);
            }
            Save();
        }

        internal static string StanceLabel(CompanionCombatStance stance)
        {
            switch (stance)
            {
                case CompanionCombatStance.HoldPosition: return "Mantener posición";
                case CompanionCombatStance.CoverPlayer: return "Cubrirme";
                case CompanionCombatStance.ProtectArea: return "Proteger área";
                case CompanionCombatStance.FireOnSignal: return "Fuego a mi señal";
                default: return "Seguir órdenes";
            }
        }

        internal static bool ShouldOverrideEnRAGECycle(string id)
        {
            if (!_settings.EnableIndividualOrders) return false;
            CompanionPersistentState state = GetState(id);
            if (state == null) return false;
            bool waitOrder = _settings.EnableWaitMode &&
                (state.Order == CompanionOrder.Waiting || state.Order == CompanionOrder.ReturningToAnchor);
            bool stanceOrder = _settings.EnableCombatStances &&
                (state.Stance == CompanionCombatStance.HoldPosition || state.Stance == CompanionCombatStance.ProtectArea);
            return waitOrder || stanceOrder;
        }

        internal static void Tick(string id, Ped ped, Ped player, bool isActive)
        {
            if (_store == null || string.IsNullOrWhiteSpace(id) || !IsUsablePed(ped))
                return;

            UpdatePlayerFireSignal(player);
            CompanionPersistentState state = GetState(id);
            if (state == null)
                return;

            if (isActive && state.Order == CompanionOrder.DismissedWaiting)
            {
                // A call/recruit transition restores active control but retains the saved posture and anchor.
                state.Order = CompanionOrder.Following;
                state.RestStartedUtcTicks = 0L;
                LastStandStillPedHandle.Remove(id);
                LastAnchorTaskAt.Remove(id);
                TrySetBlockingEvents(ped, false);
                Save();
            }

            int now = Game.GameTime;
            int observedHandle;
            if (!LastPedHandle.TryGetValue(id, out observedHandle) || observedHandle != ped.Handle)
            {
                // A respawn is a new physical entity; do not interpret the difference from the
                // previous ped's health as fresh damage to the persistent companion record.
                LastPedHandle[id] = ped.Handle;
                LastPedHealth[id] = ped.Health;
                LastRecordTick[id] = now;
                LastTravelTick[id] = now;
                TravelAccumulatorMs[id] = 0;
                TravelDistanceMeters[id] = 0f;
                LastTravelPlayerPosition.Remove(id);
                DamageWindowStartedAt.Remove(id);
                DamageWindowTotal.Remove(id);
                LastAnchorTaskAt.Remove(id);
                LastStandStillPedHandle.Remove(id);
            }

            int previousTick;
            if (!LastRecordTick.TryGetValue(id, out previousTick))
            {
                LastRecordTick[id] = now;
                if (isActive && state.Order == CompanionOrder.Following)
                    LastTravelTick[id] = now;
                LastPedHealth[id] = ped.Health;
                LastPedHandle[id] = ped.Handle;
            }
            else
            {
                int elapsed = Elapsed(now, previousTick);
                if (elapsed >= 200)
                {
                    LastRecordTick[id] = now;
                    ObserveDamage(id, state, ped, now);
                    UpdateRecovery(id, state, ped);
                }
            }

            bool waiting = (_settings.EnableWaitMode &&
                (state.Order == CompanionOrder.Waiting || state.Order == CompanionOrder.DismissedWaiting)) ||
                (_settings.EnableCombatStances &&
                (state.Stance == CompanionCombatStance.HoldPosition || state.Stance == CompanionCombatStance.ProtectArea));

            if (_settings.EnableWaitMode && isActive && state.Order == CompanionOrder.ReturningToAnchor)
                MaintainAnchor(id, state, ped, now, true);
            else if (waiting && (isActive || state.Order == CompanionOrder.DismissedWaiting))
                MaintainAnchor(id, state, ped, now, false);

            if (isActive && _settings.EnableAffinity &&
                state.Order == CompanionOrder.Following &&
                state.Stance != CompanionCombatStance.HoldPosition &&
                state.Stance != CompanionCombatStance.ProtectArea &&
                player != null && player.Exists())
                UpdateTravelAffinity(id, state, ped, player, now);
            else
                LastTravelTick[id] = now;

            ApplyMoveRate(id, state, ped);

            // A dismissed companion is deliberately not allowed to start wandering again.
            if (state.Order == CompanionOrder.DismissedWaiting)
                TrySetBlockingEvents(ped, true);
        }

        internal static bool IsTargetAllowed(string id, Ped companion, Ped player, Ped target, int now)
        {
            if (string.IsNullOrWhiteSpace(id) || !IsUsablePed(companion) ||
                !IsUsablePed(player) || !IsUsablePed(target))
                return false;

            CompanionPersistentState state = GetState(id);
            if (state == null || !_settings.EnableCombatStances)
                return true;

            switch (state.Stance)
            {
                case CompanionCombatStance.HoldPosition:
                    if (!state.HasAnchor) CaptureAnchor(state, companion);
                    return Distance(target.Position.X, target.Position.Y, target.Position.Z,
                        state.AnchorX, state.AnchorY, state.AnchorZ) <= Math.Min(18.0f, _settings.ProtectAreaRadius);
                case CompanionCombatStance.CoverPlayer:
                    return Distance(target.Position, player.Position) <= _settings.ProtectAreaRadius * 1.5f &&
                        Distance(companion.Position, player.Position) <= _settings.ProtectAreaRadius * 1.75f;
                case CompanionCombatStance.ProtectArea:
                    if (!state.HasAnchor) CaptureAnchor(state, companion);
                    return Distance(target.Position.X, target.Position.Y, target.Position.Z,
                        state.AnchorX, state.AnchorY, state.AnchorZ) <= _settings.ProtectAreaRadius;
                case CompanionCombatStance.FireOnSignal:
                    return !_settings.FireOnSignalOnly || IsFireSignalActive(now);
                default:
                    return true;
            }
        }

        internal static void NotifyCombat(string id, int targetHandle)
        {
            if (!_settings.EnableAffinity || string.IsNullOrWhiteSpace(id) || targetHandle == 0)
                return;

            int previous;
            if (LastCombatTarget.TryGetValue(id, out previous) && previous == targetHandle)
                return;
            LastCombatTarget[id] = targetHandle;

            long now = DateTime.UtcNow.Ticks;
            long last;
            if (LastCombatAffinityUtc.TryGetValue(id, out last) &&
                now - last < TimeSpan.FromSeconds(30).Ticks)
                return;

            LastCombatAffinityUtc[id] = now;
            AddAffinity(id, _settings.AffinityCombatGain);
        }

        internal static void OnTreatment(string id)
        {
            CompanionPersistentState state = GetState(id);
            if (state == null) return;

            if (_settings.EnableInjurySystem && state.InjurySeverity > 0)
            {
                if (_settings.MedicineRestoresFullHealth)
                    state.InjurySeverity = 0;
                else
                    state.InjurySeverity = Math.Max(0, state.InjurySeverity - _settings.MedicineInjuryReduction);

                if (state.InjurySeverity == 0)
                    state.InjuryStartedUtcTicks = 0L;
                state.RestStartedUtcTicks = 0L;
                Save();
            }

            AddAffinity(id, _settings.AffinityTreatmentGain);
        }

        internal static float TreatmentHealthFraction
        {
            get { return _settings.MedicineRestoresFullHealth ? 1f : _settings.MedicineHealthRestoreFraction; }
        }

        internal static int TreatmentCostCents
        {
            get { return _settings.MedicineCostCents; }
        }

        internal static bool IsFireSignalActive(int now)
        {
            return _shootingNativeAvailable && Elapsed(now, _fireSignalUntil) < 0;
        }

        private static void UpdatePlayerFireSignal(Ped player)
        {
            if (!_settings.EnableCombatStances || !_settings.FireOnSignalOnly ||
                player == null || !player.Exists())
                return;

            int now = Game.GameTime;
            if (Elapsed(now, _lastFireSignalProbeAt) < 100)
                return;
            _lastFireSignalProbeAt = now;

            ResolveOptionalNatives();
            if (_shootingNative == null)
            {
                if (!_shootingWarningLogged)
                {
                    _shootingWarningLogged = true;
                    _log("Fuego a mi señal is fail-closed: PED.IS_PED_SHOOTING(int) was not available in this ScriptHook API assembly.");
                }
                _shootingNativeAvailable = false;
                return;
            }

            try
            {
                object value = _shootingNative.Invoke(null, new object[] { player.Handle });
                _shootingNativeAvailable = value is bool;
                if (value is bool shooting && shooting)
                    _fireSignalUntil = now + _settings.FireSignalWindowMs;
            }
            catch (Exception ex)
            {
                _shootingNativeAvailable = false;
                if (!_shootingWarningLogged)
                {
                    _shootingWarningLogged = true;
                    _log("Could not query player shooting state; Fuego a mi señal remains disabled. " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void ObserveDamage(string id, CompanionPersistentState state, Ped ped, int now)
        {
            if (!_settings.EnableInjurySystem)
                return;

            int current = Math.Max(0, ped.Health);
            int previous;
            if (!LastPedHealth.TryGetValue(id, out previous))
            {
                LastPedHealth[id] = current;
                return;
            }

            int damage = previous - current;
            LastPedHealth[id] = current;
            if (damage <= 0)
                return;

            int windowStart;
            if (!DamageWindowStartedAt.TryGetValue(id, out windowStart) || Elapsed(now, windowStart) > 10000)
            {
                DamageWindowStartedAt[id] = now;
                DamageWindowTotal[id] = 0;
            }

            int accumulated;
            DamageWindowTotal.TryGetValue(id, out accumulated);
            accumulated = Math.Min(1000, accumulated + damage);
            DamageWindowTotal[id] = accumulated;
            if (accumulated < _settings.InjuryDamageThreshold)
                return;

            int oldSeverity = state.InjurySeverity;
            int requestedSeverity = accumulated >= _settings.InjuryDamageThreshold * 2 ? 2 : 1;
            if (ped.MaxHealth > 0 && current <= ped.MaxHealth * 0.25f)
                requestedSeverity = 2;

            if (requestedSeverity > state.InjurySeverity)
            {
                state.InjurySeverity = Math.Min(2, requestedSeverity);
                if (oldSeverity == 0)
                    state.InjuryStartedUtcTicks = DateTime.UtcNow.Ticks;
                if (state.Order == CompanionOrder.Waiting || state.Order == CompanionOrder.DismissedWaiting)
                    state.RestStartedUtcTicks = DateTime.UtcNow.Ticks;
                _log("Companion injury recorded for " + id + ": severity " + state.InjurySeverity +
                    ", observed damage window " + accumulated + ".");
                Save();
            }

            DamageWindowStartedAt[id] = now;
            DamageWindowTotal[id] = 0;
        }

        private static void UpdateRecovery(string id, CompanionPersistentState state, Ped ped)
        {
            if (!_settings.EnableInjurySystem || state.InjurySeverity <= 0 || ped.IsDead)
                return;

            bool resting = state.Order == CompanionOrder.Waiting || state.Order == CompanionOrder.DismissedWaiting;
            if (!resting)
            {
                state.RestStartedUtcTicks = 0L;
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            if (state.RestStartedUtcTicks == 0L)
            {
                state.RestStartedUtcTicks = now;
                Save();
                return;
            }

            if (now - state.RestStartedUtcTicks < TimeSpan.FromSeconds(_settings.InjuryRecoverySeconds).Ticks)
                return;

            state.InjurySeverity = Math.Max(0, state.InjurySeverity - 1);
            state.RestStartedUtcTicks = now;
            if (state.InjurySeverity == 0)
                state.InjuryStartedUtcTicks = 0L;
            _log("Companion rest recovery applied to " + id + "; injury severity is now " + state.InjurySeverity + ".");
            Save();
        }

        private static void UpdateTravelAffinity(string id, CompanionPersistentState state, Ped ped, Ped player, int now)
        {
            int last;
            if (!LastTravelTick.TryGetValue(id, out last))
            {
                LastTravelTick[id] = now;
                TravelAccumulatorMs[id] = 0;
                TravelDistanceMeters[id] = 0f;
                LastTravelPlayerPosition[id] = player.Position;
                return;
            }

            int elapsed = Elapsed(now, last);
            LastTravelTick[id] = now;
            if (elapsed < 0 || elapsed > 10000)
                elapsed = 0;

            RDR2.Math.Vector3 previousPosition;
            if (LastTravelPlayerPosition.TryGetValue(id, out previousPosition))
            {
                float moved = Distance(previousPosition, player.Position);
                // Discard teleports/streaming jumps instead of rewarding them as travel.
                if (!float.IsNaN(moved) && !float.IsInfinity(moved) && moved <= 15f)
                {
                    float distance;
                    TravelDistanceMeters.TryGetValue(id, out distance);
                    TravelDistanceMeters[id] = Math.Min(10000f, distance + moved);
                }
            }
            LastTravelPlayerPosition[id] = player.Position;

            bool eligible = Distance(ped.Position, player.Position) <= 25.0f &&
                !ped.IsDead && !ped.IsInCombat;
            if (!eligible)
                return;

            int accumulator;
            TravelAccumulatorMs.TryGetValue(id, out accumulator);
            accumulator = Math.Min(accumulator + elapsed, _settings.AffinityTravelIntervalSeconds * 1000);
            TravelAccumulatorMs[id] = accumulator;

            float traveled;
            TravelDistanceMeters.TryGetValue(id, out traveled);
            // Staying still beside the player is not treated as traveling together.
            if (accumulator >= _settings.AffinityTravelIntervalSeconds * 1000 && traveled >= 50f)
            {
                accumulator -= _settings.AffinityTravelIntervalSeconds * 1000;
                TravelDistanceMeters[id] = Math.Max(0f, traveled - 50f);
                TravelAccumulatorMs[id] = accumulator;
                AddAffinity(id, _settings.AffinityTravelGain);
            }
        }

        private static void AddAffinity(string id, int amount)
        {
            if (!_settings.EnableAffinity || string.IsNullOrWhiteSpace(id) || amount <= 0)
                return;
            CompanionPersistentState state = GetState(id);
            if (state == null) return;
            int updated = Math.Max(0, Math.Min(100, state.Affinity + amount));
            if (updated == state.Affinity) return;
            state.Affinity = updated;
            Save();
        }

        private static void MaintainAnchor(string id, CompanionPersistentState state, Ped ped, int now, bool returnTask)
        {
            if (!state.HasAnchor || !IsUsablePed(ped))
                return;

            float distance = Distance(ped.Position.X, ped.Position.Y, ped.Position.Z,
                state.AnchorX, state.AnchorY, state.AnchorZ);
            float radius = _settings.WaitRadius;
            if (returnTask)
                radius = Math.Min(radius, 1.75f);

            if (distance <= radius)
            {
                if (returnTask)
                {
                    state.Order = CompanionOrder.Waiting;
                    if (state.RestStartedUtcTicks == 0L)
                        state.RestStartedUtcTicks = DateTime.UtcNow.Ticks;
                    LastAnchorTaskAt.Remove(id);
                    LastStandStillPedHandle.Remove(id);
                    if (!ped.IsOnMount)
                        TryStandStill(ped, id);
                    Save();
                }
                else if (!ped.IsOnMount)
                    TryStandStill(ped, id);
                return;
            }

            int lastTask;
            if (LastAnchorTaskAt.TryGetValue(id, out lastTask) && Elapsed(now, lastTask) < 2500)
                return;

            // A stay-mounted order is authoritative: do not dismount or replace the horse's task.
            if (ped.IsOnMount && state.MountPolicy == CompanionMountPolicy.StayMounted)
                return;

            LastAnchorTaskAt[id] = now;
            LastStandStillPedHandle.Remove(id);
            if (ped.IsOnMount)
            {
                try { TASK.TASK_DISMOUNT_ANIMAL(ped.Handle, 0, 0UL, 0UL, 0UL, 0); }
                catch (Exception ex) { _log("Wait-position dismount warning for " + id + ": " + ex.Message); }
                return;
            }

            if (!TryGoToAnchor(ped, state))
            {
                if (!_goStraightWarningLogged)
                {
                    _goStraightWarningLogged = true;
                    _log("TASK_GO_STRAIGHT_TO_COORD with the verified RDR2 signature is unavailable in this API build. " +
                        "Wait positions remain recorded, but automatic return movement is disabled.");
                }
            }
        }

        private static bool TryGoToAnchor(Ped ped, CompanionPersistentState state)
        {
            ResolveOptionalNatives();
            if (_goStraightNative == null || !IsUsablePed(ped))
                return false;
            ParameterInfo[] p = _goStraightNative.GetParameters();
            object[] args;
            if (p.Length == 9)
            {
                args = new object[] { ped.Handle, state.AnchorX, state.AnchorY, state.AnchorZ, 1.0f, -1, 0.0f, 1.0f, 0 };
            }
            else if (p.Length == 8)
            {
                args = new object[] { ped.Handle, state.AnchorX, state.AnchorY, state.AnchorZ, 1.0f, -1, 0.0f, 1.0f };
            }
            else
                return false;

            try { _goStraightNative.Invoke(null, args); return true; }
            catch (Exception ex)
            {
                _log("Return-to-position task failed safely: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void ApplyMoveRate(string id, CompanionPersistentState state, Ped ped)
        {
            if (!_settings.EnableInjurySystem)
                return;

            bool injured = state.InjurySeverity > 0 && !ped.IsDead && !ped.IsOnMount && !ped.IsInVehicle;
            int appliedHandle;
            bool samePed = MoveRatePedHandles.TryGetValue(id, out appliedHandle) && appliedHandle == ped.Handle;
            bool wasApplied = MoveRateApplied.Contains(id);

            // Avoid a native call every tick. Reapply only when injury state or the physical ped changes.
            if (injured && wasApplied && samePed)
                return;
            if (!injured && (!wasApplied || !samePed))
            {
                MoveRateApplied.Remove(id);
                MoveRatePedHandles.Remove(id);
                return;
            }

            ResolveOptionalNatives();
            if (_moveRateNative == null)
            {
                if (!_moveRateWarningLogged)
                {
                    _moveRateWarningLogged = true;
                    _log("Injury state persists, but PED.SET_PED_MOVE_RATE_OVERRIDE(int,float) is unavailable; movement slowing is disabled for this API build.");
                }
                return;
            }

            try
            {
                _moveRateNative.Invoke(null, new object[]
                {
                    ped.Handle,
                    injured ? _settings.InjuryMoveRateMultiplier : 1.0f
                });
                if (injured)
                {
                    MoveRateApplied.Add(id);
                    MoveRatePedHandles[id] = ped.Handle;
                }
                else
                {
                    MoveRateApplied.Remove(id);
                    MoveRatePedHandles.Remove(id);
                }
            }
            catch (Exception ex)
            {
                if (!_moveRateWarningLogged)
                {
                    _moveRateWarningLogged = true;
                    _log("Could not apply companion injury movement rate: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void TryStandStill(Ped ped, string id)
        {
            if (!IsUsablePed(ped) || ped.IsOnMount)
                return;

            int lastHandle;
            if (LastStandStillPedHandle.TryGetValue(id, out lastHandle) && lastHandle == ped.Handle)
                return;

            ResolveOptionalNatives();
            if (_standStillNative == null)
            {
                if (!_standStillWarningLogged)
                {
                    _standStillWarningLogged = true;
                    _log("TASK_STAND_STILL(int,int) is unavailable; waiting falls back to cleared tasks and saved position control.");
                }
                return;
            }

            try
            {
                _standStillNative.Invoke(null, new object[] { ped.Handle, -1 });
                LastStandStillPedHandle[id] = ped.Handle;
            }
            catch (Exception ex)
            {
                if (!_standStillWarningLogged)
                {
                    _standStillWarningLogged = true;
                    _log("Could not apply TASK_STAND_STILL: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void TrySetBlockingEvents(Ped ped, bool blocked)
        {
            if (!IsUsablePed(ped)) return;
            ResolveOptionalNatives();
            if (_blockingEventsNative == null)
            {
                if (!_blockingWarningLogged)
                {
                    _blockingWarningLogged = true;
                    _log("PED.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS(int,bool) is unavailable; dismiss-time ambient event suppression is limited.");
                }
                return;
            }
            try { _blockingEventsNative.Invoke(null, new object[] { ped.Handle, blocked }); }
            catch (Exception ex)
            {
                if (!_blockingWarningLogged)
                {
                    _blockingWarningLogged = true;
                    _log("Could not set temporary-event blocking: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void TryClearTasks(Ped ped)
        {
            if (!IsUsablePed(ped)) return;
            try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); }
            catch (Exception ex) { _log("Companion task reset warning: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void CaptureAnchor(CompanionPersistentState state, Ped ped)
        {
            state.AnchorX = ped.Position.X;
            state.AnchorY = ped.Position.Y;
            state.AnchorZ = ped.Position.Z;
            state.HasAnchor = true;
        }

        private static void Save()
        {
            if (_store != null)
            {
                _store.MarkDirty();
                _store.SaveIfDirty();
            }
        }

        private static void ResolveOptionalNatives()
        {
            if (_nativeLookupDone)
                return;
            _nativeLookupDone = true;

            _moveRateNative = FindNative("PED", "SET_PED_MOVE_RATE_OVERRIDE", p =>
                p.Length == 2 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(float) &&
                p[0].ParameterType.IsPrimitive && p[1].ParameterType.IsPrimitive);
            _shootingNative = FindNative("PED", "IS_PED_SHOOTING", p =>
                p.Length == 1 && p[0].ParameterType == typeof(int));
            _standStillNative = FindNative("TASK", "TASK_STAND_STILL", p =>
                p.Length == 2 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(int));
            _blockingEventsNative = FindNative("PED", "SET_BLOCKING_OF_NON_TEMPORARY_EVENTS", p =>
                p.Length == 2 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(bool));
            _goStraightNative = FindNative("TASK", "TASK_GO_STRAIGHT_TO_COORD", p =>
                (p.Length == 8 || p.Length == 9) &&
                p[0].ParameterType == typeof(int) &&
                p[1].ParameterType == typeof(float) && p[2].ParameterType == typeof(float) &&
                p[3].ParameterType == typeof(float) && p[4].ParameterType == typeof(float) &&
                p[5].ParameterType == typeof(int) && p[6].ParameterType == typeof(float) &&
                p[7].ParameterType == typeof(float) &&
                (p.Length == 8 || p[8].ParameterType == typeof(int)));
        }

        private static MethodInfo FindNative(string namespaceName, string methodName, Func<ParameterInfo[], bool> predicate)
        {
            try
            {
                Assembly apiAssembly = typeof(Ped).Assembly;
                Type nativeType = apiAssembly.GetType("RDR2.Native." + namespaceName, false);
                if (nativeType == null) return null;
                return nativeType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) &&
                        m.ReturnType != typeof(void) == (methodName == "IS_PED_SHOOTING") &&
                        predicate(m.GetParameters()));
            }
            catch { return null; }
        }

        private static bool IsUsablePed(Ped ped)
        {
            try { return ped != null && ped.Exists() && !ped.IsDead; }
            catch { return false; }
        }

        private static float Distance(RDR2.Math.Vector3 a, RDR2.Math.Vector3 b)
        {
            return Distance(a.X, a.Y, a.Z, b.X, b.Y, b.Z);
        }

        private static float Distance(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            float dx = x1 - x2;
            float dy = y1 - y2;
            float dz = z1 - z2;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static int Elapsed(int now, int previous)
        {
            return unchecked(now - previous);
        }
    }
}
