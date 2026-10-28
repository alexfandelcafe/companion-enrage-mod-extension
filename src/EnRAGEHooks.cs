using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RDR2;
using RDR2.Math;
using RDR2.Native;

namespace EnRAGECompanionsExtension
{
    /// <summary>
    /// Runtime bridge into enRAGE. Method and field names here intentionally match the inspected
    /// NemesisSystem.NemesisMod build. Failures are logged instead of being allowed to crash a tick.
    /// </summary>
    internal static class EnRAGEHooks
    {
        private const string HarmonyId = "community.enrage.companions.extension";
        private const string KeyPrefix = "__enrage_extcomp_";
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Harmony _harmony;
        private static Type _mainType;
        private static MethodInfo _updateAsk;
        private static MethodInfo _missionGiverNear;
        private static MethodInfo _sendHome;
        private static MethodInfo _updateSocial;
        private static MethodInfo _socialTarget;
        private static MethodInfo _recordAt;
        private static MethodInfo _roleOf;
        private static MethodInfo _whoIs;
        private static MethodInfo _makePrompt;
        private static MethodInfo _promptValid;
        private static MethodInfo _interrogable;
        private static MethodInfo _updateInterrogation;
        private static MethodInfo _companionFight;
        private static MethodInfo _modelFemale;
        private static bool _loggedMissingModelGender;
        private static MethodInfo _uiDrawForeverString;
        private static Type _rosterMenuType;
        private static object _latestMainInstance;
        private static bool _installed;
        private static object _askContextInstance;
        private static int _genericPrompt;
        private static int _genericPromptFor;
        private static int _genericPromptGroup;
        private static bool _genericPromptArmed;
        private static Ped _genericPromptPed;
        private static Ped _preferredGenericPed;
        // Contextual medicine prompt: visible only while the player is aiming directly at a live
        // pedestrian that has a roster record created/managed by this extension.
        private static int _medicinePrompt;
        private static int _medicinePromptFor;
        private static int _medicinePromptGroup;
        private static bool _medicinePromptArmed;
        private static Ped _medicinePromptPed;
        private static int _maxCompanions = 3;
        // Default affiliation is Van der Linde for compatibility. Both settings can target an
        // existing faction in enRAGE; the player-friendly relationship group is independent of it.
        private static string _recruitGangId = "vdl";
        private static string _recruitRosterId = "vdl";
        private static bool _allowFriendlyFire = true;
        private static bool _preventTargeting;
        private static int _lastStoryRosterSyncAt = -100000;
        private static int _lastRelationshipMaintenanceAt = -100000;
        private static int _lastRecruitFriendshipMaintenanceAt = -100000;
        private static int _lastRecruitIdentityMaintenanceAt = -100000;
        private static int _lastRelationshipConfigAt = -100000;
        private const uint PlayerRelationshipGroupHash = 0x6F0783F5U; // PLAYER
        private static uint _persistentRecruitRelationshipGroup;
        private static uint _lastPlayerRelationshipGroup;
        private static readonly Dictionary<string, uint> ModelHashes = new Dictionary<string, uint>();
        private sealed class CachedRosterRecord
        {
            internal uint ModelHash;
            internal object Record;
            internal Ped Ped;
        }
        // A dismissed ambient ped keeps this association for the current session, so recruiting the
        // same entity again reuses its persistent sheet instead of creating "Aliado 2", "Aliado 3", etc.
        private static readonly Dictionary<int, CachedRosterRecord> RosterRecordsByPedHandle = new Dictionary<int, CachedRosterRecord>();
        private static readonly Dictionary<string, uint> RecruitedRosterModels = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        // RDR2 meta-ped outfit hashes are saved by stable roster ID. ModelHash alone is not enough:
        // several world outfit variants can share the same ped model.
        private static readonly Dictionary<string, uint> RecruitedOutfitHashes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private static bool _loggedOutfitGetterUnavailable;
        private static bool _loggedOutfitEquipUnavailable;
        private static readonly HashSet<string> OutfitCaptureFailureLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, string> AppliedPromptNames = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> CustomCompanionCombatTargets = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> CustomCombatSuppressedAt = new Dictionary<int, int>();
        // These recruited pedestrians have been released from companion control and should continue
        // their ambient AI instead of being held in the extension's passive-follower profile.
        private static readonly HashSet<int> AmbientReleasedRecruitPeds = new HashSet<int>();
        private static readonly Dictionary<int, int> AmbientReleasedWanderAt = new Dictionary<int, int>();
        // Persistent roster actions: queued healing and records permanently expelled from the configured
        // faction (plus legacy Van der Linde expulsions saved by earlier versions).
        private static readonly HashSet<string> PendingFullHealthIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ExiledRosterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _pendingKickRosterRecordId;
        private const int RecruitMedicineCostCents = 100; // $1.00
        // Use the distinct paper/interact control rather than the same key as Saludar/Provocar/
        // Dejar de seguirme, otherwise RDR2 may suppress this second prompt in the same target group.
        private const int RecruitMedicinePromptControl = -2074855166;

        internal static bool IsInstalled { get { return _installed; } }

        internal static bool TryInstall()
        {
            if (_installed)
                return true;

            try
            {
                Type found = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "NemesisSystem.NemesisMod"))
                    .FirstOrDefault(t => t != null);

                if (found == null)
                    return false; // enRAGE may not have been loaded yet.

                _mainType = found;
                _updateAsk = FindMethod(_mainType, "UpdateAsk", 1, p => IsPedType(p[0].ParameterType));
                _missionGiverNear = FindMethod(_mainType, "MissionGiverNear", 1, p => IsPedType(p[0].ParameterType));
                _sendHome = FindMethod(_mainType, "SendHome", 3,
                    p => p[0].ParameterType.Name == "Companion" && p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(bool));
                _updateSocial = FindMethod(_mainType, "UpdateSocial", 1, p => IsPedType(p[0].ParameterType));
                _socialTarget = FindMethod(_mainType, "SocialTarget", 1, p => IsPedType(p[0].ParameterType));
                _recordAt = FindMethod(_mainType, "RecordAt", 1, p => p[0].ParameterType == typeof(int));
                _roleOf = FindMethod(_mainType, "RoleOf", 1, p => p[0].ParameterType == typeof(int));
                _whoIs = FindMethod(_mainType, "WhoIs", 1, p => p[0].ParameterType == typeof(int));
                _makePrompt = FindMethod(_mainType, "MakePrompt", 4, p =>
                    (p[0].ParameterType.IsEnum || p[0].ParameterType == typeof(int)) &&
                    p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(bool) &&
                    p[3].ParameterType == typeof(int));
                _promptValid = FindMethod(_mainType, "PromptValid", 1, p => p[0].ParameterType == typeof(int));
                _interrogable = FindMethod(_mainType, "Interrogable", 3, p => IsPedType(p[0].ParameterType) && IsPedType(p[1].ParameterType) && p[2].ParameterType == typeof(float));
                _updateInterrogation = FindMethod(_mainType, "UpdateInterrogation", 1, p => IsPedType(p[0].ParameterType));
                _companionFight = FindMethod(_mainType, "CompanionFight", 2,
                    p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType));
                Type modelsType = _mainType.Assembly.GetType("NemesisSystem.Models", false);
                _modelFemale = FindMethod(modelsType, "Female", 1, p =>
                    p[0].ParameterType == typeof(uint) || p[0].ParameterType == typeof(int) ||
                    p[0].ParameterType == typeof(long) || p[0].ParameterType == typeof(ulong));
                _rosterMenuType = _mainType.Assembly.GetType("NemesisSystem.RosterMenu", false);

                MethodInfo updateCompanion = FindMethod(_mainType, "UpdateCompanion", 1, p => IsPedType(p[0].ParameterType));
                MethodInfo ask = FindMethod(_mainType, "Ask", 3, p => IsPedType(p[0].ParameterType) && p[1].ParameterType == typeof(string) && IsPedType(p[2].ParameterType));
                MethodInfo spawnCompanion = FindMethod(_mainType, "SpawnCompanion", 2, p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType));
                MethodInfo cleanup = FindMethod(_mainType, "Cleanup", 1, p => p[0].ParameterType.Name == "Companion");
                MethodInfo restoreReal = FindMethod(_mainType, "RestoreReal", 1, p => p[0].ParameterType.Name == "Companion");

                if (updateCompanion == null || _updateAsk == null || _updateSocial == null || _socialTarget == null ||
                    _makePrompt == null || !_makePrompt.IsStatic || _promptValid == null || !_promptValid.IsStatic ||
                    ask == null || spawnCompanion == null || _companionFight == null)
                {
                    Log("ERROR: Required enRAGE methods were not found. This build may have changed; no patches applied.");
                    _mainType = null;
                    return false;
                }

                ReadConfiguration();
                if (!IsConfiguredRecruitmentFactionAvailable())
                    Log("WARNING: configured enRAGE faction ID '" + _recruitGangId + "' was not found. Recruitment will be blocked until RecruitGangId is set to an existing faction ID.");
                if (!string.Equals(_recruitGangId, _recruitRosterId, StringComparison.OrdinalIgnoreCase) &&
                    !UsesVanDerLindeStoryRoster())
                    Log("WARNING: for a custom faction, set RecruitGangId and RecruitRosterId to the same enRAGE faction ID so roster actions target that faction consistently.");
                LoadPersistentRosterActions();
                CompanionSystems.Initialize(
                    ResolveScriptsFile("EnRAGECompanionsExtension.companions.json"),
                    ResolveScriptsFile("EnRAGECompanionsExtension.outfits.txt"),
                    ResolveScriptsFile("EnRAGECompanionsExtension.ini"),
                    Log);
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(updateCompanion,
                    prefix: Hook(nameof(CompanionCyclePrefix)),
                    postfix: Hook(nameof(CompanionCyclePostfix)));
                _harmony.Patch(_updateAsk,
                    prefix: Hook(nameof(UpdateAskPrefix)),
                    postfix: Hook(nameof(UpdateAskPostfix)));
                if (_missionGiverNear != null)
                    _harmony.Patch(_missionGiverNear,
                        prefix: Hook(nameof(MissionGiverNearPrefix)),
                        postfix: Hook(nameof(MissionGiverNearPostfix)));
                if (_sendHome != null)
                    _harmony.Patch(_sendHome, prefix: Hook(nameof(SendHomePrefix)));
                _harmony.Patch(_makePrompt, prefix: Hook(nameof(MakePromptPrefix)));
                if (_interrogable != null)
                    _harmony.Patch(_interrogable, prefix: Hook(nameof(InterrogablePrefix)));
                if (_updateInterrogation != null)
                    _harmony.Patch(_updateInterrogation, prefix: Hook(nameof(UpdateInterrogationPrefix)));
                if (_rosterMenuType != null)
                {
                    MethodInfo detailActions = FindMethod(_rosterMenuType, "DetailActions", 0);
                    MethodInfo activateDetail = FindMethod(_rosterMenuType, "ActivateDetail", 0);
                    MethodInfo handleDetailKey = FindMethod(_rosterMenuType, "HandleDetailKey", 1);
                    if (detailActions != null) _harmony.Patch(detailActions, postfix: Hook(nameof(DetailActionsPostfix)));
                    if (activateDetail != null) _harmony.Patch(activateDetail, prefix: Hook(nameof(ActivateDetailPrefix)));
                    if (handleDetailKey != null) _harmony.Patch(handleDetailKey, prefix: Hook(nameof(HandleDetailKeyPrefix)));
                }
                _harmony.Patch(ask, prefix: Hook(nameof(AskPrefix)));
                // enRAGE's social prompt uses the same context control and target group. When a
                // targeted pedestrian is recruitable, suppress the $5/hat-tip prompt so the
                // companion prompt is the sole action shown for that pedestrian.
                _harmony.Patch(_updateSocial, prefix: Hook(nameof(UpdateSocialPrefix)));
                if (_whoIs != null)
                    _harmony.Patch(_whoIs, postfix: Hook(nameof(WhoIsPostfix)));
                _harmony.Patch(spawnCompanion, prefix: Hook(nameof(SpawnCompanionPrefix)));
                // enRAGE's stock CompanionFight attacks every hated target around the player once
                // any nearby combat starts. Extension recruits use a strict retaliatory policy.
                _harmony.Patch(_companionFight, prefix: Hook(nameof(CompanionFightPrefix)));

                if (_recordAt != null)
                    _harmony.Patch(_recordAt, postfix: Hook(nameof(RecordAtPostfix)));

                if (cleanup != null)
                    _harmony.Patch(cleanup, postfix: Hook(nameof(CleanupPostfix)));
                if (restoreReal != null)
                    _harmony.Patch(restoreReal, postfix: Hook(nameof(RestoreRealPostfix)));

                _installed = true;
                Log("Installed. Target=" + _mainType.Assembly.GetName().Name + "; max companions=" + _maxCompanions +
                    "; gang=" + _recruitGangId + "; roster=" + _recruitRosterId +
                    "; friendly fire=" + _allowFriendlyFire + "; prevent targeting=" + _preventTargeting + ".");
                return true;
            }
            catch (Exception ex)
            {
                try { if (_harmony != null) _harmony.UnpatchAll(HarmonyId); } catch { }
                _harmony = null;
                _mainType = null;
                _installed = false;
                Log("Install failed: " + Unwrap(ex));
                return false;
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            MethodInfo hook = typeof(EnRAGEHooks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (hook == null)
                throw new MissingMethodException(typeof(EnRAGEHooks).FullName, name);
            return new HarmonyMethod(hook);
        }

        // Track the active UpdateAsk call so MakePrompt can relabel only the extension recruit's
        // dismissal action, without changing prompts used by enRAGE's own story companions.
        private static void UpdateAskPrefix(object __instance)
        {
            _askContextInstance = __instance;
        }

        private static void UpdateAskPostfix()
        {
            _askContextInstance = null;
        }

        private static void MissionGiverNearPrefix(object __instance)
        {
            _askContextInstance = __instance;
        }

        private static void MissionGiverNearPostfix()
        {
            _askContextInstance = null;
        }

        private static void MakePromptPrefix(object[] __args)
        {
            try
            {
                if (_askContextInstance == null || __args == null || __args.Length < 2)
                    return;
                string label = __args[1] as string;
                if (!string.Equals(label, "Mandar al campamento", StringComparison.OrdinalIgnoreCase))
                    return;

                object companion = GetField(_askContextInstance, "_askComp");
                string key = GetField(companion, "Key") as string;
                if (!string.IsNullOrEmpty(key) && key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    __args[1] = "Dejar de seguirme";
            }
            catch (Exception ex)
            {
                Log("Dismissal prompt label warning: " + Unwrap(ex));
            }
        }

        // The original enRAGE build uses this method to run a one-time shutdown of the old
        // companion system. Mark that one-time shutdown complete before its body runs, then
        // run the existing companion routines from its postfix.
        private static void CompanionCyclePrefix(object __instance)
        {
            _latestMainInstance = __instance;
            SetField(__instance, "_companionsOffDone", true);
        }

        private static void CompanionCyclePostfix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0 || !(__args[0] is Ped player) || player == null || !player.Exists())
                    return;

                // The built-in Van der Linde faction has a separate StoryRoster; normal/custom factions
                // appear through Roster filtered by each record's Gang ID. Exclusions are maintained for both.
                SyncVanDerLindeStoryRoster(__instance, false);
                UpdateCachedRecruitedPedDeaths(__instance);
                MaintainDismissedRecruitRelationships(__instance, player);
                MaintainRecruitedFriendship(__instance);
                MaintainRecruitedIdentity(__instance);

                // Keep enRAGE's original story-companion interaction. Generic NPCs use a separate
                // native prompt, rather than mutating _askPed/_askKey and recursively re-entering UpdateAsk.
                InvokeInstance(__instance, "UpdateAsk", player);
                UpdateGenericRecruitPrompt(__instance, player);
                UpdateRecruitMedicinePrompt(__instance, player);
                UpdateCompanionOrdersPrompt(__instance, player);

                foreach (KeyValuePair<int, CachedRosterRecord> tracked in RosterRecordsByPedHandle.ToArray())
                {
                    CachedRosterRecord cached = tracked.Value;
                    Ped trackedPed = cached == null ? null : cached.Ped;
                    string trackedId = cached == null || cached.Record == null ? null : GetMember(cached.Record, "Id") as string;
                    if (string.IsNullOrWhiteSpace(trackedId) || trackedPed == null || !trackedPed.Exists() || trackedPed.IsDead)
                        continue;
                    CompanionSystems.Tick(trackedId, trackedPed, player,
                        IsExtensionCompanionActiveForPed(__instance, trackedPed.Handle));
                }

                IList list = GetField(__instance, "_comps") as IList;
                if (list == null || list.Count == 0)
                    return;

                // Iterate over a snapshot: Ride/GoingHome/Cleanup can remove entries from _comps.
                object[] snapshot = list.Cast<object>().ToArray();
                foreach (object companion in snapshot)
                {
                    if (companion == null)
                        continue;

                    MarkRecruitedCompanionDeath(__instance, companion);
                    object companionRecord = GetField(companion, "Rec");
                    string companionRecordId = GetMember(companionRecord, "Id") as string;
                    if (CompanionSystems.ShouldOverrideEnRAGECycle(companionRecordId))
                    {
                        CompanionCombatStance stance = CompanionSystems.GetStance(companionRecordId);
                        if (stance == CompanionCombatStance.HoldPosition || stance == CompanionCombatStance.ProtectArea)
                            MaintainRetaliatoryCombat(__instance, companion, player);
                        continue;
                    }

                    int state = GetFieldInt(companion, "State", -1);
                    switch (state)
                    {
                        case 0: InvokeInstance(__instance, "WaitToJoin", companion, player); break;
                        case 1: InvokeInstance(__instance, "Ride", companion, player); break;
                        case 2: InvokeInstance(__instance, "GoingHome", companion, player); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Companion cycle error: " + Unwrap(ex));
            }
        }

        private static void RecordAtPostfix(int __0, ref object __result)
        {
            try
            {
                if (__0 == 0) return;
                CachedRosterRecord cached;
                if (RosterRecordsByPedHandle.TryGetValue(__0, out cached) && cached != null && cached.Record != null)
                {
                    __result = cached.Record;
                    // Keep retrying outfit capture if the native returned zero during initial loading.
                    CaptureRecruitOutfitIfMissing(cached.Record, cached.Ped);
                    return;
                }

                // Migration path for recruits created by older builds: after updating the extension,
                // enRAGE can still resolve their extcomp_/extvdl_ roster record. Capture the current
                // outfit before that world entity streams away, even if it predates the outfit file.
                object record = __result;
                string id = GetMember(record, "Id") as string;
                uint modelHash = ConvertToUInt32(GetMember(record, "ModelHash"));
                if (record == null || modelHash == 0U || string.IsNullOrWhiteSpace(id) ||
                    !(id.StartsWith("extcomp_", StringComparison.OrdinalIgnoreCase) ||
                      id.StartsWith("extvdl_", StringComparison.OrdinalIgnoreCase)))
                    return;

                Ped ped = new Ped(__0);
                if (ped == null || !ped.Exists() || ped.IsDead || unchecked((uint)ped.Model.Hash) != modelHash)
                    return;
                CacheRosterRecord(__0, modelHash, record, ped);
                CaptureRecruitOutfitIfMissing(record, ped);
            }
            catch (Exception ex) { Log("RecordAt roster/outfit migration warning: " + Unwrap(ex)); }
        }

        private static void WhoIsPostfix(int __0, ref string __result)
        {
            try
            {
                CachedRosterRecord cached;
                if (__0 != 0 && RosterRecordsByPedHandle.TryGetValue(__0, out cached) && cached != null && cached.Record != null)
                {
                    string name = GetMember(cached.Record, "Name") as string;
                    if (!string.IsNullOrWhiteSpace(name)) __result = name;
                }
            }
            catch (Exception ex) { Log("WhoIs name override warning: " + Unwrap(ex)); }
        }

        // enRAGE's own UpdateSocial creates "Darle unos dólares" / "Tocarse el sombrero"
        // using the same interaction control and entity prompt group as recruitment. If its
        // current interaction target is safely recruitable, retain the hat-animation update,
        // hide that social prompt, and let the companion-cycle postfix display our prompt.
        private static bool UpdateSocialPrefix(object __instance, object[] __args)
        {
            if (__args == null || __args.Length == 0 || !(__args[0] is Ped player) ||
                player == null || !player.Exists())
                return true;

            Ped target = null;
            try
            {
                object socialTuple = _socialTarget == null
                    ? null
                    : _socialTarget.Invoke(__instance, new object[] { player });
                target = TuplePed(socialTuple);
            }
            catch (Exception ex)
            {
                Log("Social target lookup warning: " + Unwrap(ex));
                // Do not block enRAGE's original social behavior when target discovery fails.
                return true;
            }

            // Recruited NPCs are members of the player's roster, not ambient strangers. Suppress
            // enRAGE's normal $5 / drink / hat / handshake actions for them even after dismissal.
            // This check must come before combat, prompt-blocking and companion-limit checks, or
            // the original social menu could reappear when those conditions change.
            if (target != null && IsCachedRecruitedPed(target.Handle))
            {
                _preferredGenericPed = null;
                InvokeInstance(__instance, "UpdateHatAnim", player);
                InvokeInstance(__instance, "HideSocialPrompt");
                return false;
            }

            if (player.IsInCombat || IsAskBlocked(__instance) || CountActiveCompanions(__instance) >= _maxCompanions)
                return true;

            try
            {
                if (target == null || !IsEligibleGenericCandidate(__instance, player, target, true))
                    return true;

                _preferredGenericPed = target;
                InvokeInstance(__instance, "UpdateHatAnim", player);
                InvokeInstance(__instance, "HideSocialPrompt");
                return false;
            }
            catch (Exception ex)
            {
                Log("Social/recruitment handoff failed: " + Unwrap(ex));
                return true;
            }
        }

        // enRAGE's interrogation prompt can win over the companion interaction for a recruited ped.
        // Such a character is a roster member, not a civilian/nemesis to interrogate.
        private static bool InterrogablePrefix(object[] __args, ref bool __result)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] is Ped ped && ped != null &&
                    ped.Exists() && IsCachedRecruitedPed(ped.Handle))
                {
                    __result = false;
                    return false;
                }
            }
            catch (Exception ex) { Log("Interrogation prompt suppression warning: " + Unwrap(ex)); }
            return true;
        }

        private static bool UpdateInterrogationPrefix(object __instance, object[] __args)
        {
            try
            {
                Ped candidate = GetField(__instance, "_interroCandidate") as Ped;
                Ped lastPromptPed = GetField(__instance, "_lastPromptPed") as Ped;
                Ped player = (__args != null && __args.Length > 0) ? __args[0] as Ped : null;
                bool recruitedTarget = (candidate != null && candidate.Exists() && IsCachedRecruitedPed(candidate.Handle)) ||
                    (lastPromptPed != null && lastPromptPed.Exists() && IsCachedRecruitedPed(lastPromptPed.Handle));
                if (!recruitedTarget) return true;

                // Clear the short-lived prompt cache too; otherwise enRAGE can replay a stale
                // "Interrogar" prompt for 0.7–1.2 seconds after Interrogable returned false.
                SetField(__instance, "_interroCandidate", null);
                SetField(__instance, "_lastPromptPed", null);
                SetField(__instance, "_lastPromptAt", 0);
                SetField(__instance, "_lastPromptMode", null);
                InvokeInstance(__instance, "HidePrompts");
                InvokeInstance(__instance, "HideLockPrompt");
                return false;
            }
            catch (Exception ex) { Log("Interrogation stale-prompt cleanup warning: " + Unwrap(ex)); return true; }
        }

        private static bool IsConfiguredRecruitmentGang(string gangId)
        {
            if (string.IsNullOrWhiteSpace(gangId)) return false;
            return string.Equals(gangId, (_recruitGangId ?? "vdl").Trim(), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(gangId, (_recruitRosterId ?? "vdl").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesVanDerLindeStoryRoster()
        {
            return string.Equals((_recruitGangId ?? "vdl").Trim(), "vdl", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((_recruitRosterId ?? "vdl").Trim(), "vdl", StringComparison.OrdinalIgnoreCase);
        }

        private static object FindEnRAGEGang(string gangId)
        {
            try
            {
                Type gangsType = _mainType == null ? null : _mainType.Assembly.GetType("NemesisSystem.Gangs", false);
                MethodInfo byId = FindMethod(gangsType, "ById", 1, p => p[0].ParameterType == typeof(string));
                if (byId == null || !byId.IsStatic || string.IsNullOrWhiteSpace(gangId)) return null;
                return byId.Invoke(null, new object[] { gangId });
            }
            catch (Exception ex)
            {
                Log("Faction lookup warning for '" + gangId + "': " + Unwrap(ex));
                return null;
            }
        }

        private static bool IsConfiguredRecruitmentFactionAvailable()
        {
            string id = (_recruitGangId ?? "vdl").Trim();
            if (string.IsNullOrWhiteSpace(id)) return false;
            Type gangsType = _mainType == null ? null : _mainType.Assembly.GetType("NemesisSystem.Gangs", false);
            MethodInfo byId = FindMethod(gangsType, "ById", 1, p => p[0].ParameterType == typeof(string));
            // If an enRAGE build changes this private API, do not prevent recruitment solely because
            // the compatibility check could not be performed. The roster writer still uses the ID.
            if (byId == null || !byId.IsStatic) return true;
            try { return byId.Invoke(null, new object[] { id }) != null; }
            catch { return true; }
        }

        private static string ConfiguredRecruitmentFactionName()
        {
            string id = (_recruitGangId ?? "vdl").Trim();
            object gang = FindEnRAGEGang(id);
            string name = GetMember(gang, "Name") as string;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return string.Equals(id, "vdl", StringComparison.OrdinalIgnoreCase) ? "Van der Linde" : id;
        }

        private static void DetailActionsPostfix(object __instance, ref List<string> __result)
        {
            try
            {
                if (__result == null) return;
                object record = GetField(__instance, "_detail");
                if (record == null) return;
                string id = GetMember(record, "Id") as string;
                if (string.IsNullOrWhiteSpace(id)) return;
                if (!string.IsNullOrWhiteSpace(_pendingKickRosterRecordId) &&
                    !string.Equals(_pendingKickRosterRecordId, id, StringComparison.OrdinalIgnoreCase))
                    _pendingKickRosterRecordId = null;

                // An expelled member must not quietly reappear as callable just because the old
                // session cache still identifies its record as an extension recruit.
                if (ExiledRosterIds.Contains(id))
                {
                    __result.Remove("Llamarlo");
                    __result.Remove("Echar de la banda");
                    __result.Remove("Confirmar expulsión");
                    __result.Remove("Cancelar expulsión");
                    return;
                }

                bool recruitedRecord = IsRecruitedRosterRecord(record);
                bool alreadyFollowing = recruitedRecord && IsRecruitedRecordAlreadyFollowing(_latestMainInstance, record);
                bool anyCompanionFollowing = IsRosterRecordCurrentlyFollowing(_latestMainInstance, record);
                if (recruitedRecord)
                {
                    __result.RemoveAll(x => x != null && x.StartsWith("Confianza: ", StringComparison.Ordinal));
                    InsertBeforeBack(__result, "Confianza: " + CompanionSystems.GetAffinity(id).ToString(CultureInfo.InvariantCulture) + "/100");
                    __result.Remove("Llamarlo");
                    if (!alreadyFollowing && (!(GetMember(record, "IsAlive") is bool alive) || alive))
                        InsertBeforeBack(__result, "Llamarlo");
                }

                // Any roster member currently attached to the companion system is protected from
                // expulsion, not just extension recruits. Once dismissed, the record can be removed.
                if (anyCompanionFollowing)
                {
                    __result.Remove("Echar de la banda");
                    __result.Remove("Confirmar expulsión");
                    __result.Remove("Cancelar expulsión");
                    return;
                }

                if (!IsKickableRosterRecord(_latestMainInstance, record)) return;
                if (string.Equals(_pendingKickRosterRecordId, id, StringComparison.OrdinalIgnoreCase))
                {
                    int kickIndex = __result.IndexOf("Echar de la banda");
                    if (kickIndex >= 0)
                        __result[kickIndex] = "Confirmar expulsión";
                    else
                        InsertBeforeBack(__result, "Confirmar expulsión");
                    InsertBeforeBack(__result, "Cancelar expulsión");
                }
                else
                {
                    __result.Remove("Confirmar expulsión");
                    __result.Remove("Cancelar expulsión");
                    InsertBeforeBack(__result, "Echar de la banda");
                }
            }
            catch (Exception ex) { Log("Roster action injection warning: " + Unwrap(ex)); }
        }

        private static void InsertBeforeBack(List<string> actions, string action)
        {
            if (actions == null || actions.Contains(action)) return;
            int backIndex = actions.IndexOf("Atrás");
            if (backIndex >= 0) actions.Insert(backIndex, action);
            else actions.Add(action);
        }

        private static void HandleDetailKeyPrefix(object __instance, object[] __args)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingKickRosterRecordId) || __args == null || __args.Length == 0)
                    return;
                // RosterMenu handles Keys.Back directly instead of going through ActivateDetail.
                // Treat that navigation as Cancel so the same member cannot reopen a stale confirm.
                if (!string.Equals(__args[0]?.ToString(), "Back", StringComparison.OrdinalIgnoreCase))
                    return;
                object record = GetField(__instance, "_detail");
                string id = GetMember(record, "Id") as string;
                if (string.Equals(id, _pendingKickRosterRecordId, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingKickRosterRecordId = null;
                    InvokeInstance(_latestMainInstance, "ShowSubtitle", "Expulsión cancelada.");
                }
            }
            catch (Exception ex) { Log("Roster confirmation navigation warning: " + Unwrap(ex)); }
        }

        private static bool ActivateDetailPrefix(object __instance)
        {
            try
            {
                object record = GetField(__instance, "_detail");
                if (record == null) return true;
                List<string> actions = InvokeInstance(__instance, "DetailActions") as List<string>;
                int selected = GetFieldInt(__instance, "_action", 0);
                if (actions == null || selected < 0 || selected >= actions.Count) return true;
                string action = actions[selected];
                string recordIdForConfirmation = GetMember(record, "Id") as string;
                if (action != null && action.StartsWith("Confianza: ", StringComparison.Ordinal) &&
                    IsRecruitedRosterRecord(record))
                {
                    string affinityName = (GetMember(record, "FullName") as string) ??
                        (GetMember(record, "Name") as string) ?? "Compañero";
                    InvokeInstance(_latestMainInstance, "ShowSubtitle",
                        affinityName + " — confianza: " +
                        CompanionSystems.GetAffinity(recordIdForConfirmation).ToString(CultureInfo.InvariantCulture) + "/100.");
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(_pendingKickRosterRecordId) &&
                    string.Equals(_pendingKickRosterRecordId, recordIdForConfirmation, StringComparison.OrdinalIgnoreCase) &&
                    action != "Confirmar expulsión" && action != "Cancelar expulsión" && action != "Echar de la banda")
                {
                    // Leaving the confirmation row or choosing another action abandons the pending
                    // decision; reopening the same sheet must not silently preserve old confirmation.
                    _pendingKickRosterRecordId = null;
                }

                bool recruitedRecord = IsRecruitedRosterRecord(record);
                bool alreadyFollowing = recruitedRecord && IsRecruitedRecordAlreadyFollowing(_latestMainInstance, record);
                bool anyCompanionFollowing = IsRosterRecordCurrentlyFollowing(_latestMainInstance, record);
                if (action == "Llamarlo" && recruitedRecord)
                {
                    if (alreadyFollowing)
                    {
                        InvokeInstance(_latestMainInstance, "ShowSubtitle", "Ese miembro ya te está siguiendo.");
                        return false;
                    }
                    CallRecruitedRosterMember(_latestMainInstance, __instance, record);
                    return false;
                }
                if (ExiledRosterIds.Contains(recordIdForConfirmation))
                    return true;
                if (anyCompanionFollowing && (action == "Echar de la banda" || action == "Confirmar expulsión"))
                {
                    InvokeInstance(_latestMainInstance, "ShowSubtitle", "No puedes expulsar a un compañero que te está siguiendo. Despídelo primero.");
                    return false;
                }

                if (!IsKickableRosterRecord(_latestMainInstance, record)) return true;
                string id = GetMember(record, "Id") as string;
                if (action == "Echar de la banda")
                {
                    _pendingKickRosterRecordId = id;
                    SetField(__instance, "_action", selected);
                    InvokeInstance(_latestMainInstance, "ShowSubtitle",
                        "Aviso: la expulsión es irreversible y podría tener consecuencias para ti en el futuro. Elige Confirmar expulsión para continuar.");
                    Log("Requested confirmation before expelling story roster member " + id + ".");
                    return false;
                }
                if (action == "Confirmar expulsión")
                {
                    if (!string.Equals(_pendingKickRosterRecordId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        InvokeInstance(_latestMainInstance, "ShowSubtitle", "La confirmación ya no es válida. Vuelve a seleccionar la expulsión.");
                        return false;
                    }
                    ExpelRosterMember(_latestMainInstance, record);
                    _pendingKickRosterRecordId = null;
                    SetField(__instance, "_detail", null);
                    SetField(__instance, "_action", 0);
                    return false;
                }
                if (action == "Cancelar expulsión")
                {
                    _pendingKickRosterRecordId = null;
                    SetField(__instance, "_action", 0);
                    InvokeInstance(_latestMainInstance, "ShowSubtitle", "Expulsión cancelada.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log("Roster action failed: " + Unwrap(ex));
            }
            return true;
        }

        private static bool IsRosterRecordCurrentlyFollowing(object instance, object record)
        {
            if (instance == null || record == null) return false;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return false;
            IList comps = GetField(instance, "_comps") as IList;
            if (comps == null) return false;
            foreach (object companion in comps)
            {
                if (companion == null || GetFieldInt(companion, "State", 2) == 2) continue;
                object companionRecord = GetField(companion, "Rec");
                string companionId = GetMember(companionRecord, "Id") as string;
                if (string.Equals(companionId, id, StringComparison.OrdinalIgnoreCase)) return true;
                // Some stock enRAGE companions have no Rec attached to their wrapper. Recover the
                // roster record from the live follower ped before deciding it can be expelled.
                Ped followerPed = GetField(companion, "Ped") as Ped;
                if (followerPed == null || !followerPed.Exists()) followerPed = GetField(companion, "Real") as Ped;
                if (followerPed != null && followerPed.Exists())
                {
                    object liveRecord = GetTrackedRecord(instance, followerPed.Handle);
                    string liveId = GetMember(liveRecord, "Id") as string;
                    if (string.Equals(liveId, id, StringComparison.OrdinalIgnoreCase)) return true;
                }
                string key = GetField(companion, "Key") as string;
                if (!string.IsNullOrWhiteSpace(key) && key.StartsWith(KeyPrefix + "roster_", StringComparison.Ordinal) &&
                    string.Equals(key.Substring((KeyPrefix + "roster_").Length), id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsRecruitedRecordAlreadyFollowing(object instance, object record)
        {
            if (instance == null || record == null) return false;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return false;
            IList comps = GetField(instance, "_comps") as IList;
            if (comps == null) return false;
            foreach (object companion in comps)
            {
                if (companion == null || GetFieldInt(companion, "State", 2) == 2) continue;
                string key = GetField(companion, "Key") as string;
                if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
                object companionRecord = GetField(companion, "Rec");
                string companionId = GetMember(companionRecord, "Id") as string;
                if (string.Equals(companionId, id, StringComparison.OrdinalIgnoreCase)) return true;
                if (key.StartsWith(KeyPrefix + "roster_", StringComparison.Ordinal) &&
                    string.Equals(key.Substring((KeyPrefix + "roster_").Length), id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsRecruitedRosterRecord(object record)
        {
            if (record == null) return false;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (RecruitedRosterModels.ContainsKey(id)) return true;
            // Records created by this extension have a stable prefix and retain ModelHash in enRAGE's save.
            return (id.StartsWith("extcomp_", StringComparison.OrdinalIgnoreCase) ||
                    id.StartsWith("extvdl_", StringComparison.OrdinalIgnoreCase)) &&
                   ConvertToUInt32(GetMember(record, "ModelHash")) != 0U;
        }

        private static bool IsKickableRosterRecord(object instance, object record)
        {
            if (instance == null || record == null) return false;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id) || ExiledRosterIds.Contains(id)) return false;

            // Never expel a member while its companion wrapper is active.
            if (IsRosterRecordCurrentlyFollowing(instance, record))
                return false;

            // Built-in story members remain kickable from the VDL story sheet, even when the
            // configured recruitment faction is a regular custom faction.
            IList storyRoster = GetMember(instance, "StoryRoster") as IList;
            if (storyRoster != null)
            {
                foreach (object member in storyRoster)
                {
                    if (object.ReferenceEquals(member, record) ||
                        string.Equals(GetMember(member, "Id") as string, id, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // For ordinary enRAGE factions (including user-defined factions), the roster screen
            // is derived from Roster entries whose Gang matches the faction's ID. Use both INI IDs
            // so a deliberately separate RecruitRosterId still works, while the usual configuration
            // keeps RecruitGangId and RecruitRosterId equal.
            string gangId = GetMember(record, "Gang") as string;
            if (!IsConfiguredRecruitmentGang(gangId))
                return false;

            IList roster = GetMember(instance, "Roster") as IList;
            if (roster == null) return false;
            foreach (object member in roster)
            {
                if (object.ReferenceEquals(member, record) ||
                    string.Equals(GetMember(member, "Id") as string, id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsExiledRosterRecord(object record)
        {
            string id = GetMember(record, "Id") as string;
            return !string.IsNullOrWhiteSpace(id) && ExiledRosterIds.Contains(id);
        }

        private static void GiveMedicineToRecruitedMember(object instance, object record, Ped targetedPed)
        {
            if (instance == null || record == null) return;
            string id = GetMember(record, "Id") as string;
            string name = (GetMember(record, "FullName") as string) ?? (GetMember(record, "Name") as string) ?? "Compañero";
            if (string.IsNullOrWhiteSpace(id) || !IsRecruitedRosterRecord(record)) return;
            if (IsExiledRosterRecord(record))
            {
                HideRecruitMedicinePrompt();
                InvokeInstance(instance, "ShowSubtitle", name + " ya fue expulsado de la banda y no puede recibir remedios.");
                return;
            }
            if (GetMember(record, "IsAlive") is bool alive && !alive)
            {
                InvokeInstance(instance, "ShowSubtitle", "No puedes curar a un miembro fallecido del roster.");
                return;
            }

            // The prompt is tied to the exact targeted entity. Do not charge for a stale target,
            // and do not silently heal a different pedestrian that happens to share the same model.
            CachedRosterRecord targetedCache;
            if (targetedPed == null || !targetedPed.Exists() || targetedPed.IsDead ||
                !RosterRecordsByPedHandle.TryGetValue(targetedPed.Handle, out targetedCache) ||
                targetedCache == null || targetedCache.Record == null ||
                !string.Equals(GetMember(targetedCache.Record, "Id") as string, id, StringComparison.OrdinalIgnoreCase))
            {
                InvokeInstance(instance, "ShowSubtitle", "No se pudo identificar al recluta apuntado; no se cobró el remedio.");
                return;
            }

            Ped ped = targetedPed;
            if (ped != null && ped.Health >= ped.MaxHealth)
            {
                if (PendingFullHealthIds.Remove(id))
                    SavePersistentIdSet("EnRAGECompanionsExtension.pending-heals.txt", PendingFullHealthIds);
                InvokeInstance(instance, "ShowSubtitle", name + " ya tiene la salud al máximo.");
                return;
            }

            int cash;
            try { cash = MONEY._MONEY_GET_CASH_BALANCE(); }
            catch (Exception ex)
            {
                Log("Medicine cash balance read failed: " + Unwrap(ex));
                InvokeInstance(instance, "ShowSubtitle", "No se pudo comprobar el dinero disponible.");
                return;
            }
            if (cash < RecruitMedicineCostCents)
            {
                InvokeInstance(instance, "ShowSubtitle", "Necesitas 1 dólar para comprar el remedio.");
                return;
            }

            try { MONEY._MONEY_DECREMENT_CASH_BALANCE(RecruitMedicineCostCents); }
            catch (Exception ex)
            {
                Log("Medicine payment failed: " + Unwrap(ex));
                InvokeInstance(instance, "ShowSubtitle", "No se pudo cobrar el remedio; no se aplicó la curación.");
                return;
            }

            HealPedToMaximum(ped);
            PendingFullHealthIds.Remove(id);
            SavePersistentIdSet("EnRAGECompanionsExtension.pending-heals.txt", PendingFullHealthIds);
            InvokeInstance(instance, "ShowSubtitle", name + " recuperó toda su salud. Coste: 1 dólar.");
            Log("Applied contextual medicine to " + name + " (record=" + id + ").");
        }

        private static Ped FindLivePedForRosterRecord(object instance, object record, string id)
        {
            foreach (KeyValuePair<int, CachedRosterRecord> pair in RosterRecordsByPedHandle.ToArray())
            {
                CachedRosterRecord cached = pair.Value;
                if (cached == null || cached.Record == null || cached.Ped == null) continue;
                string cachedId = GetMember(cached.Record, "Id") as string;
                if (!object.ReferenceEquals(cached.Record, record) &&
                    !string.Equals(cachedId, id, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (cached.Ped.Exists() && !cached.Ped.IsDead && cached.Ped.Handle == pair.Key)
                        return cached.Ped;
                }
                catch { }
            }

            IList comps = GetField(instance, "_comps") as IList;
            if (comps != null)
            {
                foreach (object companion in comps)
                {
                    object companionRecord = GetField(companion, "Rec");
                    string companionId = GetMember(companionRecord, "Id") as string;
                    if (!object.ReferenceEquals(companionRecord, record) &&
                        !string.Equals(companionId, id, StringComparison.OrdinalIgnoreCase)) continue;
                    Ped ped = GetField(companion, "Ped") as Ped;
                    if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
                    if (ped != null && ped.Exists() && !ped.IsDead) return ped;
                }
            }
            return null;
        }

        private static void HealPedToMaximum(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead) return;
            int maxHealth = ped.MaxHealth;
            if (maxHealth > 0) ped.Health = maxHealth;
        }

        private static void ApplyPendingFullHealthIfNeeded(object record, Ped ped)
        {
            if (record == null || ped == null || !ped.Exists() || ped.IsDead) return;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id) || !PendingFullHealthIds.Contains(id)) return;
            HealPedToMaximum(ped);
            PendingFullHealthIds.Remove(id);
            SavePersistentIdSet("EnRAGECompanionsExtension.pending-heals.txt", PendingFullHealthIds);
            Log("Applied queued full-health medicine to " + (GetMember(record, "Name") as string ?? id) + ".");
        }

        private static void LoadPersistentRosterActions()
        {
            LoadPersistentIdSet("EnRAGECompanionsExtension.pending-heals.txt", PendingFullHealthIds);
            LoadPersistentIdSet("EnRAGECompanionsExtension.exiled-roster.txt", ExiledRosterIds);
            // Compatibility with the legacy filename used by previous Van der Linde-only builds.
            LoadPersistentIdSet("EnRAGECompanionsExtension.exiled-story-roster.txt", ExiledRosterIds);
            LoadPersistentRecruitOutfits();
        }

        private static void LoadPersistentRecruitOutfits()
        {
            try
            {
                string path = ResolveScriptsFile("EnRAGECompanionsExtension.outfits.txt");
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int separator = line.IndexOf('|');
                    if (separator <= 0 || separator >= line.Length - 1) continue;
                    string id = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    uint hash;
                    if (id.Length > 0 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash) && hash != 0U)
                        RecruitedOutfitHashes[id] = hash;
                }
                Log("Loaded persistent outfit hashes: " + RecruitedOutfitHashes.Count + ".");
            }
            catch (Exception ex) { Log("Could not load persistent recruit outfits: " + Unwrap(ex)); }
        }

        private static void SavePersistentRecruitOutfits()
        {
            try
            {
                string path = ResolveScriptsFile("EnRAGECompanionsExtension.outfits.txt");
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                string[] lines = RecruitedOutfitHashes
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != 0U)
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "|" + pair.Value.ToString("X8", CultureInfo.InvariantCulture))
                    .ToArray();
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex) { Log("Could not save persistent recruit outfits: " + Unwrap(ex)); }
        }

        private static void PreserveRecruitOutfit(object record, Ped ped)
        {
            if (record == null || ped == null || !ped.Exists() || ped.IsDead) return;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return;

            uint savedHash;
            if (!RecruitedOutfitHashes.TryGetValue(id, out savedHash) || savedHash == 0U)
            {
                if (CompanionSystems.TryGetMetaOutfitHash(id, out savedHash) && savedHash != 0U)
                    RecruitedOutfitHashes[id] = savedHash;
            }
            if (savedHash != 0U)
            {
                ApplySavedRecruitOutfit(record, ped);
                return;
            }
            CaptureRecruitOutfitIfMissing(record, ped);
        }

        private static void CaptureRecruitOutfitIfMissing(object record, Ped ped)
        {
            if (record == null || ped == null || !ped.Exists() || ped.IsDead) return;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return;
            uint existing;
            if (RecruitedOutfitHashes.TryGetValue(id, out existing) && existing != 0U) return;

            uint outfitHash;
            if (!TryGetMetaPedOutfitHash(ped, out outfitHash) || outfitHash == 0U)
            {
                if (OutfitCaptureFailureLogged.Add(id))
                    Log("Could not capture the current meta-ped outfit for record " + id + "; native outfit hash was unavailable/zero. Will retry while enRAGE still resolves this recruit.");
                return;
            }

            OutfitCaptureFailureLogged.Remove(id);
            RecruitedOutfitHashes[id] = outfitHash;
            CompanionSystems.SetMetaOutfitHash(id, outfitHash);
            SavePersistentRecruitOutfits();
            Log("Saved outfit 0x" + outfitHash.ToString("X8", CultureInfo.InvariantCulture) + " for " +
                (GetMember(record, "Name") as string ?? id) + " (record=" + id + ").");
        }

        private static void ApplySavedRecruitOutfit(object record, Ped ped)
        {
            if (record == null || ped == null || !ped.Exists() || ped.IsDead) return;
            string id = GetMember(record, "Id") as string;
            uint outfitHash;
            if (string.IsNullOrWhiteSpace(id))
                return;
            if (!RecruitedOutfitHashes.TryGetValue(id, out outfitHash) || outfitHash == 0U)
            {
                if (!CompanionSystems.TryGetMetaOutfitHash(id, out outfitHash) || outfitHash == 0U)
                    return;
                RecruitedOutfitHashes[id] = outfitHash;
            }

            try
            {
                if (!TryEquipMetaPedOutfitHash(ped, outfitHash))
                {
                    Log("Could not restore saved meta-ped outfit for record " + id + "; compatible outfit native was unavailable.");
                    return;
                }
                Log("Restored outfit 0x" + outfitHash.ToString("X8", CultureInfo.InvariantCulture) + " for " +
                    (GetMember(record, "Name") as string ?? id) + " (ped=" + ped.Handle + ").");
            }
            catch (Exception ex) { Log("Recruit outfit restore warning for " + id + ": " + Unwrap(ex)); }
        }

        private static bool TryGetMetaPedOutfitHash(Ped ped, out uint outfitHash)
        {
            outfitHash = 0U;
            if (ped == null || !ped.Exists()) return false;
            MethodInfo getter = FindPedNative(new[] { "_GET_PED_META_OUTFIT_HASH", "GET_PED_META_OUTFIT_HASH" }, 1,
                p => IsNativeInteger(p[0].ParameterType));
            if (getter == null)
            {
                if (!_loggedOutfitGetterUnavailable)
                {
                    _loggedOutfitGetterUnavailable = true;
                    Log("ScriptHook native wrapper for _GET_PED_META_OUTFIT_HASH was not found; outfit persistence cannot capture new entries on this API build.");
                }
                return false;
            }
            try
            {
                object value = getter.Invoke(null, new[] { ConvertNativeInteger(ped.Handle, getter.GetParameters()[0].ParameterType) });
                outfitHash = NativeHashToUInt32(value);
                return outfitHash != 0U;
            }
            catch (Exception ex)
            {
                Log("_GET_PED_META_OUTFIT_HASH failed: " + Unwrap(ex));
                return false;
            }
        }

        private static bool TryEquipMetaPedOutfitHash(Ped ped, uint outfitHash)
        {
            MethodInfo equip = FindPedNative(new[] { "_EQUIP_META_PED_OUTFIT", "EQUIP_META_PED_OUTFIT", "_EQUIP_PED_BODY_COMPONENT" }, 2,
                p => IsNativeInteger(p[0].ParameterType) && IsNativeInteger(p[1].ParameterType));
            if (equip == null)
            {
                if (!_loggedOutfitEquipUnavailable)
                {
                    _loggedOutfitEquipUnavailable = true;
                    Log("ScriptHook native wrapper for _EQUIP_META_PED_OUTFIT was not found; outfit persistence cannot restore entries on this API build.");
                }
                return false;
            }

            ParameterInfo[] parameters = equip.GetParameters();
            equip.Invoke(null, new[]
            {
                ConvertNativeInteger(ped.Handle, parameters[0].ParameterType),
                ConvertNativeInteger(outfitHash, parameters[1].ParameterType)
            });

            MethodInfo update = FindPedNative(new[] { "_UPDATE_PED_VARIATION", "UPDATE_PED_VARIATION" }, 6,
                p => IsNativeInteger(p[0].ParameterType) && p.Skip(1).All(parameter => parameter.ParameterType == typeof(bool)));
            if (update != null)
            {
                ParameterInfo[] updateParameters = update.GetParameters();
                update.Invoke(null, new object[]
                {
                    ConvertNativeInteger(ped.Handle, updateParameters[0].ParameterType), false, true, true, true, false
                });
            }
            return true;
        }

        private static MethodInfo FindPedNative(string[] names, int parameterCount, Func<ParameterInfo[], bool> predicate)
        {
            Type nativeType = typeof(PED);
            return nativeType.GetMethods(StaticFlags)
                .Where(method => names.Contains(method.Name, StringComparer.OrdinalIgnoreCase))
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == parameterCount && predicate(parameters);
                });
        }

        private static bool IsNativeInteger(Type type)
        {
            return type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(short) || type == typeof(ushort) || type.IsEnum;
        }

        private static object ConvertNativeInteger(long value, Type targetType)
        {
            if (targetType.IsEnum) return Enum.ToObject(targetType, value);
            if (targetType == typeof(int)) return unchecked((int)value);
            if (targetType == typeof(uint)) return unchecked((uint)value);
            if (targetType == typeof(long)) return value;
            if (targetType == typeof(ulong)) return unchecked((ulong)value);
            if (targetType == typeof(short)) return unchecked((short)value);
            if (targetType == typeof(ushort)) return unchecked((ushort)value);
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private static uint NativeHashToUInt32(object value)
        {
            if (value == null) return 0U;
            if (value is uint unsignedValue) return unsignedValue;
            if (value is int signedValue) return unchecked((uint)signedValue);
            if (value is long longValue) return unchecked((uint)longValue);
            if (value is ulong ulongValue) return unchecked((uint)ulongValue);
            if (value.GetType().IsEnum) return unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture));
            try { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0U; }
        }

        private static void LoadPersistentIdSet(string fileName, HashSet<string> destination)
        {
            try
            {
                string path = ResolveScriptsFile(fileName);
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string value = (raw ?? string.Empty).Trim();
                    if (value.Length == 0 || value.StartsWith("#") || value.StartsWith(";")) continue;
                    destination.Add(value);
                }
            }
            catch (Exception ex) { Log("Could not load persistent roster actions from " + fileName + ": " + Unwrap(ex)); }
        }

        private static void SavePersistentIdSet(string fileName, HashSet<string> values)
        {
            try
            {
                string path = ResolveScriptsFile(fileName);
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(path, values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray());
            }
            catch (Exception ex) { Log("Could not save persistent roster actions to " + fileName + ": " + Unwrap(ex)); }
        }

        private static void ExpelRosterMember(object instance, object record)
        {
            if (!IsKickableRosterRecord(instance, record)) return;
            string id = GetMember(record, "Id") as string;
            string name = (GetMember(record, "Name") as string) ?? id;
            string currentGang = GetMember(record, "Gang") as string;
            bool inStoryRoster = IsRecordInList(GetMember(instance, "StoryRoster") as IList, record, id);
            string formerGang = !string.IsNullOrWhiteSpace(currentGang) ? currentGang :
                (inStoryRoster ? "vdl" : _recruitGangId);

            ExiledRosterIds.Add(id);
            SavePersistentIdSet("EnRAGECompanionsExtension.exiled-roster.txt", ExiledRosterIds);

            try
            {
                IList storyRoster = GetMember(instance, "StoryRoster") as IList;
                if (storyRoster != null)
                {
                    foreach (object member in storyRoster.Cast<object>().ToArray())
                    {
                        if (object.ReferenceEquals(member, record) ||
                            string.Equals(GetMember(member, "Id") as string, id, StringComparison.OrdinalIgnoreCase))
                        {
                            string memberGang = GetMember(member, "Gang") as string;
                            if (!string.IsNullOrWhiteSpace(memberGang)) SetMember(member, "FormerGang", memberGang);
                            else if (!string.IsNullOrWhiteSpace(formerGang)) SetMember(member, "FormerGang", formerGang);
                            SetMember(member, "Gang", null);
                            SetMember(member, "BossId", null);
                            SetMember(member, "CampId", null);
                            SetMember(member, "CampKnown", false);
                            SetMember(member, "Affiliation", null);
                            storyRoster.Remove(member);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(currentGang))
                    SetMember(record, "FormerGang", currentGang);
                else if (!string.IsNullOrWhiteSpace(formerGang))
                    SetMember(record, "FormerGang", formerGang);
                SetMember(record, "Gang", null);
                SetMember(record, "BossId", null);
                SetMember(record, "CampId", null);
                SetMember(record, "CampKnown", false);
                SetMember(record, "Affiliation", null);
                try { InvokeInstance(instance, "SaveRoster"); }
                catch (Exception ex) { Log("Save after expelling roster member failed: " + Unwrap(ex)); }
                EnforceExiledRosterMembers(instance);
                InvokeInstance(instance, "ShowSubtitle", name + " ha sido expulsado de la banda. La decisión no se puede deshacer.");
                Log("Permanently expelled roster member " + name + " (record=" + id + ", former gang=" + (formerGang ?? "(none)") + ").");
            }
            catch (Exception ex)
            {
                Log("Expelling roster member failed: " + Unwrap(ex));
                InvokeInstance(instance, "ShowSubtitle", "No se pudo completar la expulsión. Revisa el registro de la extensión.");
            }
        }

        private static bool IsRecordInList(IList list, object record, string id)
        {
            if (list == null) return false;
            foreach (object member in list)
            {
                if (object.ReferenceEquals(member, record) ||
                    (!string.IsNullOrWhiteSpace(id) && string.Equals(GetMember(member, "Id") as string, id, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        private static void EnforceExiledRosterMembers(object instance)
        {
            if (instance == null || ExiledRosterIds.Count == 0) return;
            bool rosterChanged = false;
            IList storyRoster = GetMember(instance, "StoryRoster") as IList;
            if (storyRoster != null)
            {
                foreach (object member in storyRoster.Cast<object>().ToArray())
                {
                    string id = GetMember(member, "Id") as string;
                    if (string.IsNullOrWhiteSpace(id) || !ExiledRosterIds.Contains(id)) continue;
                    string gangId = GetMember(member, "Gang") as string;
                    if (!string.IsNullOrWhiteSpace(gangId)) SetMember(member, "FormerGang", gangId);
                    else SetMember(member, "FormerGang", "vdl");
                    SetMember(member, "Gang", null);
                    SetMember(member, "BossId", null);
                    SetMember(member, "CampId", null);
                    SetMember(member, "CampKnown", false);
                    SetMember(member, "Affiliation", null);
                    storyRoster.Remove(member);
                    rosterChanged = true;
                }
            }

            IList roster = GetMember(instance, "Roster") as IList;
            if (roster != null)
            {
                foreach (object record in roster.Cast<object>().ToArray())
                {
                    string id = GetMember(record, "Id") as string;
                    if (string.IsNullOrWhiteSpace(id) || !ExiledRosterIds.Contains(id)) continue;
                    string gangId = GetMember(record, "Gang") as string;
                    if (!string.IsNullOrWhiteSpace(gangId))
                    {
                        SetMember(record, "FormerGang", gangId);
                        SetMember(record, "Gang", null);
                        SetMember(record, "BossId", null);
                        SetMember(record, "CampId", null);
                        SetMember(record, "CampKnown", false);
                        SetMember(record, "Affiliation", null);
                        rosterChanged = true;
                    }
                }
            }
            if (rosterChanged)
            {
                try { InvokeInstance(instance, "SaveRoster"); }
                catch (Exception ex) { Log("Persisting expelled roster members failed: " + Unwrap(ex)); }
            }
        }

        private static void CallRecruitedRosterMember(object instance, object menu, object record)
        {
            if (instance == null || record == null) return;
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;

            string id = GetMember(record, "Id") as string;
            string name = (GetMember(record, "FullName") as string) ?? (GetMember(record, "Name") as string) ?? "Compañero";
            uint modelHash = 0U;
            if (!string.IsNullOrWhiteSpace(id)) RecruitedRosterModels.TryGetValue(id, out modelHash);
            if (modelHash == 0U) modelHash = ConvertToUInt32(GetMember(record, "ModelHash"));
            if (modelHash == 0U)
            {
                InvokeInstance(instance, "ShowSubtitle", "No se pudo recuperar el modelo de este miembro.");
                return;
            }
            if (GetMember(record, "IsAlive") is bool isAlive && !isAlive)
            {
                InvokeInstance(instance, "ShowSubtitle", name + " figura como fallecido en el roster.");
                return;
            }

            IList comps = GetField(instance, "_comps") as IList;
            if (comps == null) return;
            foreach (object existing in comps)
            {
                object existingRecord = GetField(existing, "Rec");
                string existingId = GetMember(existingRecord, "Id") as string;
                if (string.Equals(existingId, id, StringComparison.OrdinalIgnoreCase) && GetFieldInt(existing, "State", 2) != 2)
                {
                    InvokeInstance(instance, "ShowSubtitle", name + " ya te está siguiendo.");
                    return;
                }
            }
            if (CountActiveCompanions(instance) >= _maxCompanions)
            {
                InvokeInstance(instance, "ShowSubtitle", "No puedes llevar más compañeros ahora.");
                return;
            }

            // First reuse the actual recruited entity if it is still alive in the world. The old
            // path always called SpawnCompanion, which cloned the same model and left the dismissed
            // original standing elsewhere. The session cache links the persistent record ID to the
            // real ped handle; PedSnapshot/RecordAt are a secondary recovery path for nearby peds.
            Ped existingPed = FindExistingRecruitedPed(instance, record, id, modelHash, player);
            if (existingPed != null) PreserveRecruitOutfit(record, existingPed);
            ApplyMaximumRecruitFriendship(record);

            Type companionType = _mainType.GetNestedType("Companion", BindingFlags.NonPublic);
            if (companionType == null) throw new MissingMemberException("NemesisMod.Companion");
            object companion = Activator.CreateInstance(companionType, true);
            string key = KeyPrefix + "roster_" + (string.IsNullOrWhiteSpace(id) ? modelHash.ToString("X8") : id);
            SetField(companion, "Key", key);
            SetField(companion, "Name", name);
            SetField(companion, "Model", "__enrage_model_" + modelHash.ToString("X8"));
            SetField(companion, "Rec", record);
            SetField(companion, "Camp", player.Position);
            SetField(companion, "LeftCamp", false);
            SetField(companion, "State", existingPed != null ? 0 : 1);
            SetField(companion, "AskedAt", Game.GameTime);
            SetField(companion, "Real", existingPed);
            SetField(companion, "IsReal", existingPed != null);
            SetField(companion, "Slot", CountActiveCompanions(instance));

            ModelHashes[key] = modelHash;
            comps.Add(companion);
            try
            {
                if (existingPed != null)
                {
                    // Adopt the same pedestrian through enRAGE's own path. Setup restores the
                    // active follower relationship and creates a horse for this companion object.
                    try { TASK.CLEAR_PED_TASKS(existingPed.Handle, true, false); } catch { }
                    try { existingPed.BlockPermanentEvents = false; } catch { }
                    MethodInfo adopt = FindMethod(_mainType, "Adopt", 3,
                        p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType) && IsPedType(p[2].ParameterType));
                    if (adopt == null)
                        throw new MissingMethodException(_mainType.FullName, "Adopt(Companion, Ped, Ped)");
                    adopt.Invoke(instance, new object[] { companion, existingPed, player });
                    try { PED.SET_PED_CONFIG_FLAG(existingPed.Handle, 297, true); } catch { }
                    try { PED.SET_PED_CONFIG_FLAG(existingPed.Handle, 301, false); } catch { }
                    ApplyTargetingPolicy(existingPed);
                    DetachFromLiveCamp(instance, existingPed);
                    CacheRosterRecord(existingPed.Handle, modelHash, record, existingPed);
                    ApplySavedRecruitOutfit(record, existingPed);
                    ApplyRecruitedPedIdentity(instance, record, existingPed);
                    try { CommitRosterRecord(instance, record, false, existingPed.Handle); }
                    catch (Exception ex) { Log("Roster save after rejoining existing ped warning: " + Unwrap(ex)); }
                    Log("Rejoined existing roster recruit " + name + " (ped=" + existingPed.Handle + ", record=" + id + ").");
                }
                else
                {
                    MethodInfo spawn = FindMethod(_mainType, "SpawnCompanion", 2,
                        p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType));
                    if (spawn == null || !(spawn.Invoke(instance, new object[] { companion, player }) is bool spawned) || !spawned)
                        throw new InvalidOperationException("enRAGE no pudo generar al compañero.");

                    Ped spawnedPed = GetField(companion, "Ped") as Ped;
                    if (spawnedPed != null && spawnedPed.Exists())
                    {
                        CacheRosterRecord(spawnedPed.Handle, modelHash, record, spawnedPed);
                        PreserveRecruitOutfit(record, spawnedPed);
                        ApplyRecruitedPedIdentity(instance, record, spawnedPed);
                        try { CommitRosterRecord(instance, record, false, spawnedPed.Handle); }
                        catch (Exception ex) { Log("Roster save after spawning recruit warning: " + Unwrap(ex)); }
                    }
                    Log("Spawned replacement roster recruit " + name + " (record=" + id + ", model=0x" + modelHash.ToString("X8") + ").");
                }

                Ped ped = GetField(companion, "Ped") as Ped;
                if (ped != null && ped.Exists()) ApplyRecruitedPedIdentity(instance, record, ped);
                Ped horse = GetField(companion, "Horse") as Ped;
                if (ped != null && ped.Exists() && horse != null && horse.Exists() && !horse.IsDead && !ped.IsOnMount)
                {
                    try { TASK.TASK_MOUNT_ANIMAL(ped.Handle, horse.Handle, 20000, -1, 2f, 1, 0UL, 0UL); } catch { }
                }
                ApplyMaximumRecruitFriendship(record);
                try { InvokeInstance(instance, "SaveRoster"); } catch { }
                InvokeInstance(instance, "ShowSubtitle", existingPed != null
                    ? name + " ha vuelto; viene hacia ti a caballo."
                    : name + " viene hacia ti a caballo.");
                InvokeInstance(menu, "Toggle");
            }
            catch
            {
                try { comps.Remove(companion); } catch { }
                ModelHashes.Remove(key);
                try { InvokeInstance(instance, "Cleanup", companion); } catch { }
                throw;
            }
        }

        private static Ped FindExistingRecruitedPed(object instance, object record, string id, uint modelHash, Ped player)
        {
            // Exact record-to-ped associations survive dismissal even when several NPCs share a model.
            foreach (KeyValuePair<int, CachedRosterRecord> pair in RosterRecordsByPedHandle.ToArray())
            {
                Ped cachedPed;
                if (TryGetMatchingCachedRecruit(pair.Value, record, id, modelHash, pair.Key, out cachedPed))
                    return cachedPed;
            }

            // Recovery path: enRAGE may already know the entity through RecordAt even if our cache
            // was lost earlier in the same session. Inspect the nearby snapshot, but never adopt an
            // arbitrary NPC solely because it shares the same model hash.
            MethodInfo snapshot = FindMethod(_mainType, "PedSnapshot", 1, p => IsPedType(p[0].ParameterType));
            if (snapshot == null || !snapshot.IsStatic || player == null || !player.Exists())
                return null;
            try
            {
                IEnumerable peds = snapshot.Invoke(null, new object[] { player }) as IEnumerable;
                if (peds == null) return null;
                foreach (object tuple in peds)
                {
                    Ped candidate = TuplePed(tuple);
                    if (candidate == null || !candidate.Exists() || candidate.IsDead ||
                        unchecked((uint)candidate.Model.Hash) != modelHash)
                        continue;

                    CachedRosterRecord cached;
                    if (RosterRecordsByPedHandle.TryGetValue(candidate.Handle, out cached))
                    {
                        Ped matched;
                        if (TryGetMatchingCachedRecruit(cached, record, id, modelHash, candidate.Handle, out matched))
                            return matched;
                    }

                    object tracked = GetTrackedRecord(instance, candidate.Handle);
                    string trackedId = GetMember(tracked, "Id") as string;
                    if (!string.IsNullOrWhiteSpace(id) && string.Equals(trackedId, id, StringComparison.OrdinalIgnoreCase) &&
                        ConvertToUInt32(GetMember(tracked, "ModelHash")) == modelHash)
                    {
                        CacheRosterRecord(candidate.Handle, modelHash, record, candidate);
                        PreserveRecruitOutfit(record, candidate);
                        return candidate;
                    }
                }
            }
            catch (Exception ex) { Log("Existing recruit scan warning: " + Unwrap(ex)); }
            return null;
        }

        private static bool TryGetMatchingCachedRecruit(CachedRosterRecord cached, object record, string id, uint modelHash, int handle, out Ped ped)
        {
            ped = null;
            if (cached == null || cached.Ped == null || cached.Record == null || cached.Ped.Handle != handle ||
                !cached.Ped.Exists() || cached.Ped.IsDead || cached.ModelHash != modelHash ||
                unchecked((uint)cached.Ped.Model.Hash) != modelHash)
                return false;
            if (GetMember(cached.Record, "IsAlive") is bool cachedAlive && !cachedAlive)
                return false;

            string cachedId = GetMember(cached.Record, "Id") as string;
            bool sameId = !string.IsNullOrWhiteSpace(id) && string.Equals(cachedId, id, StringComparison.OrdinalIgnoreCase);
            if (!sameId && !object.ReferenceEquals(cached.Record, record))
                return false;
            ped = cached.Ped;
            return true;
        }

        private static bool ApplyMaximumRecruitFriendship(object record)
        {
            if (record == null) return false;
            string id = GetMember(record, "Id") as string;
            if (IsBuiltInStoryRecordId(id) && GetMember(record, "Story") is bool story && story)
                return false; // Never overwrite the original story cast's relationship stats.

            bool changed = SetNumericMember(record, "Hate", 0);
            changed |= SetNumericMember(record, "Fear", 0);
            changed |= SetNumericMember(record, "Like", 100); // Optional on builds whose record type exposes a like score.
            changed |= SetEnumMember(record, "Disposition", "Friendly");
            return changed;
        }

        private static bool SetNumericMember(object record, string name, int desired)
        {
            object current = GetMember(record, name);
            if (current == null) return SetMember(record, name, desired);
            try { if (Convert.ToInt32(current) == desired) return false; }
            catch { }
            return SetMember(record, name, desired);
        }

        private static bool SetEnumMember(object record, string name, string desiredName)
        {
            if (record == null) return false;
            try
            {
                Type type = record.GetType();
                PropertyInfo property = type.GetProperty(name, InstanceFlags | StaticFlags);
                FieldInfo field = property == null ? type.GetField(name, InstanceFlags | StaticFlags) : null;
                Type memberType = property != null ? property.PropertyType : (field != null ? field.FieldType : null);
                if (memberType == null || !memberType.IsEnum || (property != null && !property.CanWrite)) return false;
                string definedName = Enum.GetNames(memberType).FirstOrDefault(n => string.Equals(n, desiredName, StringComparison.OrdinalIgnoreCase));
                if (definedName == null) return false;
                object desired = Enum.Parse(memberType, definedName, true);
                if (object.Equals(GetMember(record, name), desired)) return false;
                return SetMember(record, name, desired);
            }
            catch { return false; }
        }

        private static void MaintainRecruitedFriendship(object instance)
        {
            int now = Game.GameTime;
            if (now >= 0 && now - _lastRecruitFriendshipMaintenanceAt < 1000) return;
            _lastRecruitFriendshipMaintenanceAt = now;
            bool changed = false;
            foreach (CachedRosterRecord cached in RosterRecordsByPedHandle.Values.ToArray())
            {
                object record = cached == null ? null : cached.Record;
                if (record == null) continue;
                // Do not deduplicate by ID: older saves/session caches can hold separate record
                // objects with the same ID, and every live object must have its hate reset.
                changed |= ApplyMaximumRecruitFriendship(record);
            }
            IList comps = GetField(instance, "_comps") as IList;
            if (comps != null)
            {
                foreach (object companion in comps)
                {
                    string key = GetField(companion, "Key") as string;
                    if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
                    object record = GetField(companion, "Rec");
                    changed |= ApplyMaximumRecruitFriendship(record);
                }
            }
            if (changed)
            {
                try { InvokeInstance(instance, "SaveRoster"); }
                catch (Exception ex) { Log("Saving recruit friendship values warning: " + Unwrap(ex)); }
            }
        }

        private static void MaintainRecruitedIdentity(object instance)
        {
            int now = Game.GameTime;
            if (now >= 0 && now - _lastRecruitIdentityMaintenanceAt < 1500) return;
            _lastRecruitIdentityMaintenanceAt = now;

            foreach (KeyValuePair<int, CachedRosterRecord> pair in RosterRecordsByPedHandle.ToArray())
            {
                CachedRosterRecord cached = pair.Value;
                Ped ped = cached == null ? null : cached.Ped;
                if (ped == null || !ped.Exists() || ped.IsDead || cached.Record == null)
                {
                    if (ped == null || !ped.Exists()) AppliedPromptNames.Remove(pair.Key);
                    continue;
                }
                ApplyRecruitedPedIdentity(instance, cached.Record, ped);
            }
        }

        private static void ApplyRecruitedPedIdentity(object instance, object record, Ped ped)
        {
            if (ped == null || !ped.Exists() || record == null) return;
            string name = GetMember(record, "Name") as string;
            if (string.IsNullOrWhiteSpace(name)) return;

            // RDR2's focus/aim label is a separate native field from enRAGE's roster name card.
            // Adopt(Companion, realPed, player) does not reliably set it, unlike SpawnCompanion.
            try
            {
                string previouslyApplied;
                if (!AppliedPromptNames.TryGetValue(ped.Handle, out previouslyApplied) ||
                    !string.Equals(previouslyApplied, name, StringComparison.Ordinal))
                {
                    string stableName = GetPersistentPromptName(name);
                    Function.Call(5352729231319872684UL, new InputArgument[] { ped.Handle, stableName });
                    AppliedPromptNames[ped.Handle] = name;
                }
            }
            catch (Exception ex) { Log("Recruit prompt-name override warning: " + Unwrap(ex)); }

            // Mark the same ped as already known to enRAGE so its WhoIs/name-resolution path does
            // not fall back to an unrelated ambient alias or "a stranger" after re-adoption.
            try
            {
                IDictionary named = GetField(instance, "_named") as IDictionary;
                if (named != null)
                {
                    object namedRecord = named.Contains(ped.Handle) ? named[ped.Handle] : null;
                    Type namedType = _mainType.GetNestedType("Named", BindingFlags.Public | BindingFlags.NonPublic);
                    if (namedRecord == null || (namedType != null && !namedType.IsInstanceOfType(namedRecord)))
                    {
                        if (namedType != null) namedRecord = Activator.CreateInstance(namedType, true);
                    }
                    if (namedRecord != null)
                    {
                        bool isNewNamedRecord = !named.Contains(ped.Handle) ||
                            !string.Equals(GetField(namedRecord, "Name") as string, name, StringComparison.Ordinal);
                        SetField(namedRecord, "Name", name);
                        SetField(namedRecord, "Title", GetMember(record, "Title") as string ?? string.Empty);
                        SetField(namedRecord, "Met", true);
                        SetField(namedRecord, "Importance", 3);
                        if (isNewNamedRecord)
                        {
                            SetField(namedRecord, "SeenAt", Game.GameTime);
                            SetField(namedRecord, "NamedAt", Game.GameTime);
                        }
                        SetField(namedRecord, "Story", false);
                        SetField(namedRecord, "Affil", GetMember(record, "Gang") as string ?? (_recruitGangId ?? "vdl"));
                        named[ped.Handle] = namedRecord;
                    }
                }
            }
            catch (Exception ex) { Log("Recruit identity registration warning: " + Unwrap(ex)); }
        }

        private static string GetPersistentPromptName(string name)
        {
            try
            {
                if (_uiDrawForeverString == null)
                {
                    Type uiDraw = _mainType.Assembly.GetTypes().FirstOrDefault(t => t.Name == "UiDraw");
                    _uiDrawForeverString = FindMethod(uiDraw, "ForeverString", 1,
                        p => p[0].ParameterType == typeof(string));
                }
                if (_uiDrawForeverString != null && _uiDrawForeverString.IsStatic)
                {
                    object stable = _uiDrawForeverString.Invoke(null, new object[] { name });
                    if (stable is string stableText && stableText.Length > 0) return stableText;
                }
            }
            catch (Exception ex) { Log("Persistent prompt-name allocation warning: " + Unwrap(ex)); }
            return name;
        }

        // Generic NPCs use their own native enRAGE-style prompt. This avoids re-entering the
        // story-only UpdateAsk state machine, which only knows CampPeople keys.
        private static void UpdateGenericRecruitPrompt(object instance, Ped player)
        {
            try
            {
                if (player == null || !player.Exists() || player.IsInCombat || IsAskBlocked(instance) ||
                    CountActiveCompanions(instance) >= _maxCompanions)
                {
                    HideGenericRecruitPrompt();
                    return;
                }

                // enRAGE's own prompt wins whenever its current target is still in interaction range.
                Ped stockTarget = GetField(instance, "_askPed") as Ped;
                if (stockTarget != null && stockTarget.Exists() &&
                    stockTarget.Position.DistanceTo(player.Position) <= 3.5f)
                {
                    HideGenericRecruitPrompt();
                    return;
                }

                Ped candidate = null;
                if (_preferredGenericPed != null && _preferredGenericPed.Exists() &&
                    IsEligibleGenericCandidate(instance, player, _preferredGenericPed, true))
                    candidate = _preferredGenericPed;
                _preferredGenericPed = null;
                if (candidate == null)
                    candidate = FindCandidate(instance, player);
                if (candidate == null || !candidate.Exists())
                {
                    HideGenericRecruitPrompt();
                    return;
                }

                int group = HUD._UI_PROMPT_GET_GROUP_ID_FOR_TARGET_ENTITY(candidate.Handle);
                bool targetChanged = _genericPromptFor != candidate.Handle || _genericPromptGroup != group;
                if (_genericPrompt == 0 || targetChanged || !IsPromptValid(_genericPrompt))
                {
                    HideGenericRecruitPrompt();
                    object control = PromptControlArgument(_makePrompt.GetParameters()[0].ParameterType, 1980406895);
                    object result = _makePrompt.Invoke(null, new object[] { control, "Cabalga conmigo", true, group });
                    _genericPrompt = Convert.ToInt32(result);
                    _genericPromptFor = candidate.Handle;
                    _genericPromptGroup = group;
                    _genericPromptPed = candidate;
                    _genericPromptArmed = false;
                }
                else
                {
                    _genericPromptPed = candidate;
                }

                if (_genericPrompt == 0)
                    return;

                HUD._UI_PROMPT_SET_VISIBLE(_genericPrompt, true);
                HUD._UI_PROMPT_SET_ENABLED(_genericPrompt, true);

                if (!HUD._UI_PROMPT_HAS_HOLD_MODE_COMPLETED(_genericPrompt))
                {
                    _genericPromptArmed = true;
                    return;
                }
                if (!_genericPromptArmed)
                    return;

                Ped selected = _genericPromptPed;
                string key = KeyPrefix + candidate.Handle.ToString("X8");
                _genericPromptArmed = false;
                HideGenericRecruitPrompt();
                RecruitGeneric(instance, selected, key, player);
            }
            catch (Exception ex)
            {
                HideGenericRecruitPrompt();
                Log("Generic recruitment prompt failed: " + Unwrap(ex));
            }
        }

        // Shows a contextual treatment prompt when the player is actively aiming at one of the
        // extension's living recruits. The prompt is attached to the target's own prompt group.
        private static void UpdateRecruitMedicinePrompt(object instance, Ped player)
        {
            try
            {
                Ped target = FindAimedRecruitedPed(instance, player);
                if (target == null || !target.Exists() || !IsCachedRecruitedPed(target.Handle))
                {
                    HideRecruitMedicinePrompt();
                    return;
                }

                CachedRosterRecord cached;
                if (!RosterRecordsByPedHandle.TryGetValue(target.Handle, out cached) || cached == null ||
                    cached.Record == null || !IsRecruitedRosterRecord(cached.Record) ||
                    IsExiledRosterRecord(cached.Record))
                {
                    HideRecruitMedicinePrompt();
                    return;
                }

                int group = HUD._UI_PROMPT_GET_GROUP_ID_FOR_TARGET_ENTITY(target.Handle);
                bool targetChanged = _medicinePromptFor != target.Handle || _medicinePromptGroup != group;
                if (_medicinePrompt == 0 || targetChanged || !IsPromptValid(_medicinePrompt))
                {
                    HideRecruitMedicinePrompt();
                    object control = PromptControlArgument(_makePrompt.GetParameters()[0].ParameterType, RecruitMedicinePromptControl);
                    object result = _makePrompt.Invoke(null, new object[] { control, "Dar remedio ($1)", true, group });
                    _medicinePrompt = Convert.ToInt32(result);
                    _medicinePromptFor = target.Handle;
                    _medicinePromptGroup = group;
                    _medicinePromptPed = target;
                    _medicinePromptArmed = false;
                }
                else
                {
                    _medicinePromptPed = target;
                }

                if (_medicinePrompt == 0) return;
                HUD._UI_PROMPT_SET_VISIBLE(_medicinePrompt, true);
                HUD._UI_PROMPT_SET_ENABLED(_medicinePrompt, true);

                if (!HUD._UI_PROMPT_HAS_HOLD_MODE_COMPLETED(_medicinePrompt))
                {
                    _medicinePromptArmed = true;
                    return;
                }
                if (!_medicinePromptArmed) return;

                Ped selected = _medicinePromptPed;
                _medicinePromptArmed = false;
                HideRecruitMedicinePrompt();
                if (selected == null || !selected.Exists()) return;
                CachedRosterRecord selectedRecord;
                if (!RosterRecordsByPedHandle.TryGetValue(selected.Handle, out selectedRecord) ||
                    selectedRecord == null || selectedRecord.Record == null)
                    return;

                GiveMedicineToRecruitedMember(instance, selectedRecord.Record, selected);
            }
            catch (Exception ex)
            {
                HideRecruitMedicinePrompt();
                Log("Contextual medicine prompt failed: " + Unwrap(ex));
            }
        }

        private static Ped FindAimedRecruitedPed(object instance, Ped player)
        {
            if (player == null || !player.Exists()) return null;

            // Do not gate this on IS_PLAYER_FREE_AIMING: with assisted/lock-on targeting RDR2 can
            // report false while its ordinary Saludar/Provocar prompts are already targeting a ped.
            // Test each cached recruit against both the free-aim and lock-on targeting APIs.
            int playerHandle = Game.Player.Handle;
            foreach (KeyValuePair<int, CachedRosterRecord> pair in RosterRecordsByPedHandle.ToArray())
            {
                CachedRosterRecord cached = pair.Value;
                if (cached == null || cached.Record == null || cached.Ped == null ||
                    IsExiledRosterRecord(cached.Record) || !IsCachedRecruitedPed(pair.Key))
                    continue;

                Ped candidate = cached.Ped;
                bool targeted = false;
                try { targeted = PLAYER.IS_PLAYER_FREE_AIMING_AT_ENTITY(playerHandle, candidate.Handle); }
                catch { }
                if (!targeted)
                {
                    try { targeted = PLAYER.IS_PLAYER_TARGETTING_ENTITY(playerHandle, candidate.Handle, false); }
                    catch { }
                }
                if (targeted)
                    return candidate;
            }

            // Some enRAGE builds expose the current lock-on through its interaction-target native,
            // while its wrapper's free-aim predicate does not include the assisted-aim case.
            // The reflected social target is only accepted when it maps to the exact cached recruit.
            if (_socialTarget != null)
            {
                try
                {
                    object targetTuple = _socialTarget.Invoke(instance, new object[] { player });
                    Ped target = TuplePed(targetTuple);
                    CachedRosterRecord targetRecord;
                    if (target != null && target.Exists() && IsCachedRecruitedPed(target.Handle) &&
                        RosterRecordsByPedHandle.TryGetValue(target.Handle, out targetRecord) &&
                        targetRecord != null && targetRecord.Record != null && !IsExiledRosterRecord(targetRecord.Record))
                        return target;
                }
                catch (Exception ex) { Log("Social-target medicine fallback warning: " + Unwrap(ex)); }
            }
            return null;
        }

        private static void HideRecruitMedicinePrompt()
        {
            int prompt = _medicinePrompt;
            if (prompt != 0 && IsPromptValid(prompt))
            {
                try { HUD._UI_PROMPT_DELETE(prompt); } catch { }
            }
            _medicinePrompt = 0;
            _medicinePromptFor = 0;
            _medicinePromptGroup = 0;
            _medicinePromptArmed = false;
            _medicinePromptPed = null;
        }

        private static object PromptControlArgument(Type targetType, int control)
        {
            if (targetType.IsEnum)
                return Enum.ToObject(targetType, control);
            return Convert.ChangeType(control, targetType);
        }

        private static bool IsPromptValid(int prompt)
        {
            if (prompt == 0 || _promptValid == null)
                return false;
            try { return _promptValid.Invoke(null, new object[] { prompt }) is bool valid && valid; }
            catch { return false; }
        }

        private static void HideGenericRecruitPrompt()
        {
            int prompt = _genericPrompt;
            if (prompt != 0 && IsPromptValid(prompt))
            {
                try { HUD._UI_PROMPT_DELETE(prompt); } catch { }
            }
            _genericPrompt = 0;
            _genericPromptFor = 0;
            _genericPromptGroup = 0;
            _genericPromptArmed = false;
            _genericPromptPed = null;
        }

        private static bool SendHomePrefix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 3)
                    return true;
                object companion = __args[0];
                string key = GetField(companion, "Key") as string;
                if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    return true;

                string why = __args[1] as string;
                if (string.Equals(why, "back at camp", StringComparison.OrdinalIgnoreCase))
                {
                    // Extension recruits do not have a real camp. Prevent Ride from treating the
                    // recruitment location as a camp; this field is used here only as a distance marker.
                    SetField(companion, "Camp", new Vector3(100000f, 100000f, 0f));
                    SetField(companion, "LeftCamp", true);
                    return false;
                }

                // Any attempt to send this extension-only companion to a story camp is instead a
                // normal dismissal. The persistent roster record is deliberately retained.
                DismissRecruitedCompanion(__instance, companion);
                return false;
            }
            catch (Exception ex)
            {
                Log("SendHome interception warning: " + Unwrap(ex));
            }
            return true;
        }

        private static bool AskPrefix(object __instance, object[] __args)
        {
            if (__args == null || __args.Length < 3)
                return true;

            string key = __args[1] as string;
            if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                // Apply the configured total limit to enRAGE's original story-companion path too.
                if (CountActiveCompanions(__instance) >= _maxCompanions)
                {
                    InvokeInstance(__instance, "ShowSubtitle", "No puedes llevar más compañeros ahora.");
                    return false;
                }
                return true;
            }

            try
            {
                object existingCompanion = FindCompanionByKey(__instance, key);
                if (existingCompanion != null)
                {
                    DismissRecruitedCompanion(__instance, existingCompanion);
                    return false;
                }

                Ped ped = __args[0] as Ped;
                Ped player = __args[2] as Ped;
                RecruitGeneric(__instance, ped, key, player);
            }
            catch (Exception ex)
            {
                Log("Recruitment/dismissal failed: " + Unwrap(ex));
            }

            // Extension keys are not present in enRAGE's CampPeople dictionary, so the original
            // Ask handler must never receive them.
            return false;
        }

        private static void RecruitGeneric(object __instance, Ped ped, string key, Ped player)
        {
            if (ped == null || !ped.Exists() || ped.IsDead || player == null || !player.Exists())
                return;
            if (!IsMalePed(ped))
            {
                InvokeInstance(__instance, "ShowSubtitle", "Solo puedes reclutar NPC masculinos.");
                return;
            }
            if (!IsConfiguredRecruitmentFactionAvailable())
            {
                InvokeInstance(__instance, "ShowSubtitle", "La facción configurada '" + (_recruitGangId ?? "vdl") + "' no existe en enRAGE. Revisa RecruitGangId en el archivo INI.");
                Log("Recruitment blocked: unknown enRAGE faction ID '" + (_recruitGangId ?? "vdl") + "'.");
                return;
            }

            IList comps = GetField(__instance, "_comps") as IList;
            if (comps == null || CountActiveCompanions(__instance) >= _maxCompanions || AlreadyCompanion(comps, ped.Handle))
            {
                InvokeInstance(__instance, "ShowSubtitle", "No puedes llevar más compañeros ahora.");
                return;
            }

            uint modelHash = unchecked((uint)ped.Model.Hash);
            if (modelHash == 0)
                return;

            object trackedRecord = GetTrackedRecord(__instance, ped.Handle);
            CachedRosterRecord cachedPedRecord;
            if (RosterRecordsByPedHandle.TryGetValue(ped.Handle, out cachedPedRecord) && cachedPedRecord != null && cachedPedRecord.Record != null &&
                ExiledRosterIds.Contains(GetMember(cachedPedRecord.Record, "Id") as string))
            {
                InvokeInstance(__instance, "ShowSubtitle", "Este NPC fue expulsado permanentemente y no puede volver a ser reclutado.");
                return;
            }
            string trackedRecordId = GetMember(trackedRecord, "Id") as string;
            if (!string.IsNullOrWhiteSpace(trackedRecordId) && ExiledRosterIds.Contains(trackedRecordId))
            {
                InvokeInstance(__instance, "ShowSubtitle", "Este NPC fue expulsado permanentemente y no puede volver a ser reclutado.");
                return;
            }
            bool newlyCreatedRosterRecord = false;
            object rosterRecord = null;
            try
            {
                rosterRecord = PrepareRosterRecord(__instance, ped, modelHash, trackedRecord, out newlyCreatedRosterRecord);
            }
            catch (Exception ex)
            {
                Log("Roster record preparation warning: " + Unwrap(ex));
            }

            object namingRecord = rosterRecord ?? trackedRecord;
            ApplyMaximumRecruitFriendship(namingRecord);
            string name = GetMember(namingRecord, "Name") as string;
            if (string.IsNullOrWhiteSpace(name))
                name = FindRecordName(__instance, ped.Handle);
            if (string.IsNullOrWhiteSpace(name))
                name = "Aliado " + (CountActiveCompanions(__instance) + 1);

            object companion = null;
            bool added = false;
            try
            {
                Type companionType = _mainType.GetNestedType("Companion", BindingFlags.NonPublic);
                if (companionType == null)
                    throw new MissingMemberException("NemesisMod.Companion");

                companion = Activator.CreateInstance(companionType, true);
                SetField(companion, "Key", key);
                SetField(companion, "Name", name);
                // Placeholder string. Clone spawning is intercepted and uses the true entity model hash.
                SetField(companion, "Model", "__enrage_model_" + modelHash.ToString("X8"));
                SetField(companion, "Rec", rosterRecord ?? trackedRecord);
                SetField(companion, "Camp", player.Position);
                SetField(companion, "State", 0);
                SetField(companion, "AskedAt", Game.GameTime);
                SetField(companion, "Real", ped);
                SetField(companion, "Slot", CountActiveCompanions(__instance));

                ModelHashes[key] = modelHash;
                comps.Add(companion);
                added = true;

                MethodInfo adopt = FindMethod(_mainType, "Adopt", 3,
                    p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType) && IsPedType(p[2].ParameterType));
                if (adopt == null)
                    throw new MissingMethodException(_mainType.FullName, "Adopt(Companion, Ped, Ped)");

                // If this is a recruit that was dismissed earlier, cancel the ambient wander timer
                // and clear the old walk-away task BEFORE Adopt/Setup installs the companion AI.
                // Otherwise the old wander can keep overriding the new follow task.
                AmbientReleasedRecruitPeds.Remove(ped.Handle);
                AmbientReleasedWanderAt.Remove(ped.Handle);
                try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                adopt.Invoke(__instance, new object[] { companion, ped, player });
                // A previously dismissed member had non-temporary ambient events blocked to stop
                // panic/retaliation while off duty. Clear that passive state while this NPC is an
                // active companion; Setup owns his follow/combat tasks until dismissal again.
                try { ped.BlockPermanentEvents = false; } catch { }
                try { PED.SET_PED_CONFIG_FLAG(ped.Handle, 297, true); } catch { }
                try { PED.SET_PED_CONFIG_FLAG(ped.Handle, 301, false); } catch { }
                ApplyTargetingPolicy(ped);
                // Detach only after successful adoption, so a failed adoption leaves camp ownership intact.
                DetachFromLiveCamp(__instance, ped);
                try { CommitRosterRecord(__instance, rosterRecord ?? trackedRecord, newlyCreatedRosterRecord, ped.Handle); }
                catch (Exception ex) { Log("Roster membership update warning: " + Unwrap(ex)); }
                CacheRosterRecord(ped.Handle, modelHash, rosterRecord ?? trackedRecord, ped);
                PreserveRecruitOutfit(rosterRecord ?? trackedRecord, ped);
                ApplyRecruitedPedIdentity(__instance, rosterRecord ?? trackedRecord, ped);
                SetField(__instance, "_askPed", null);
                SetField(__instance, "_askKey", null);
                SetField(__instance, "_askComp", null);
                InvokeInstance(__instance, "HideAskPrompt");
                InvokeInstance(__instance, "ShowSubtitle", name + " ahora combate de tu lado.");
                Log("Recruited ped=" + ped.Handle + "; model=0x" + modelHash.ToString("X8") + "; role detached if needed.");
            }
            catch
            {
                if (added && companion != null)
                {
                    try { InvokeInstance(__instance, "Cleanup", companion); } catch { }
                    try { comps.Remove(companion); } catch { }
                }
                ModelHashes.Remove(key);
                throw;
            }
        }

        private static bool SpawnCompanionPrefix(object __instance, object[] __args, ref bool __result)
        {
            if (__args == null || __args.Length < 2)
                return true;

            object companion = __args[0];
            string key = GetField(companion, "Key") as string;
            if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                return true;

            Ped player = __args[1] as Ped;
            uint modelHash;
            if (player == null || !player.Exists() || !ModelHashes.TryGetValue(key, out modelHash))
            {
                __result = false;
                return false;
            }

            try
            {
                __result = SpawnGenericClone(__instance, companion, player, modelHash);
            }
            catch (Exception ex)
            {
                Log("Generic companion respawn failed: " + Unwrap(ex));
                __result = false;
            }
            return false;
        }

        private static bool SpawnGenericClone(object instance, object companion, Ped player, uint modelHash)
        {
            MethodInfo outOfSight = FindMethod(_mainType, "OutOfSightSpot", 5,
                p => IsPedType(p[0].ParameterType) && p[1].ParameterType == typeof(float) && p[2].ParameterType == typeof(float));
            if (outOfSight == null)
                return false;

            object spotValue = outOfSight.Invoke(instance, new object[] { player, 35f, 60f, false, true });
            if (!(spotValue is Vector3 spot) || spot == Vector3.Zero)
                return false;

            Model model = new Model(unchecked((int)modelHash));
            if (!model.IsValid || !model.IsPed)
                return false;

            MethodInfo load = FindMethod(_mainType, "LoadModelSafe", 2,
                p => p[0].ParameterType == typeof(Model) && p[1].ParameterType == typeof(int));
            if (load == null || !(load.Invoke(instance, new object[] { model, 3000 }) is bool loaded) || !loaded)
                return false;

            MethodInfo spawn = _mainType.GetMethods(StaticFlags).FirstOrDefault(m =>
                m.Name == "SpawnPed" && m.GetParameters().Length == 3 &&
                m.GetParameters()[1].ParameterType == typeof(Vector3) && m.GetParameters()[2].ParameterType == typeof(float));
            if (spawn == null)
                return false;

            Type modelParameter = spawn.GetParameters()[0].ParameterType;
            object nativeHash = modelParameter.IsEnum
                ? Enum.ToObject(modelParameter, modelHash)
                : Convert.ChangeType(modelHash, modelParameter);

            Ped spawned = spawn.Invoke(null, new object[] { nativeHash, spot, player.Heading }) as Ped;
            STREAMING.SET_MODEL_AS_NO_LONGER_NEEDED(modelHash);
            if (spawned == null || !spawned.Exists())
                return false;

            InvokeInstance(instance, "NoteModel", spawned);
            MethodInfo makeOurs = FindMethod(_mainType, "MakeOurs", 1, p => p[0].ParameterType == typeof(int));
            if (makeOurs != null)
                makeOurs.Invoke(null, new object[] { spawned.Handle });

            SetField(companion, "Ped", spawned);
            SetField(companion, "IsReal", false);

            Ped horse = GetField(companion, "Horse") as Ped;
            if (horse != null && horse.Exists() && !horse.IsDead)
                horse.Position = spot + new Vector3(2.5f, 0f, 0f);

            MethodInfo setup = FindMethod(_mainType, "Setup", 3,
                p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType) && IsPedType(p[2].ParameterType));
            if (setup == null)
                return false;

            setup.Invoke(instance, new object[] { companion, spawned, player });
            // enRAGE's spawn helper creates a fresh randomized meta-ped. Reapply the saved outfit
            // after Setup so the persistent roster identity uses the same clothing variant.
            PreserveRecruitOutfit(GetField(companion, "Rec"), spawned);
            return true;
        }

        private static void CleanupPostfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0)
                    return;
                object companion = __args[0];
                string key = GetField(companion, "Key") as string;
                if (key != null && key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    ModelHashes.Remove(key);
            }
            catch (Exception ex)
            {
                Log("Cleanup postfix warning: " + Unwrap(ex));
            }
        }

        private static bool CompanionFightPrefix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2 || __args[0] == null)
                    return true;
                object companion = __args[0];
                string key = GetField(companion, "Key") as string;
                if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    return true; // Preserve vanilla enRAGE combat for its original story companions.

                Ped player = __args[1] as Ped;
                if (player != null && player.Exists())
                    MaintainRetaliatoryCombat(__instance, companion, player);
                return false; // Never fall through to enRAGE's broad hated-target auto-attack.
            }
            catch (Exception ex)
            {
                Log("Retaliatory companion combat warning: " + Unwrap(ex));
                return false; // Fail closed: do not restore the aggressive stock behavior.
            }
        }

        private static void MaintainRetaliatoryCombat(object instance, object companion, Ped player)
        {
            // Match enRAGE's 800 ms combat scan interval; this patch replaces the original method,
            // so it must preserve the cadence rather than scanning/issuing tasks every frame.
            int now = Game.GameTime;
            if (now < GetFieldInt(companion, "FightAt", 0)) return;
            SetField(companion, "FightAt", now + 800);

            Ped companionPed = GetField(companion, "Ped") as Ped;
            if (companionPed == null || !companionPed.Exists() || companionPed.IsDead || player == null || !player.Exists())
                return;

            object trackedRecord = GetField(companion, "Rec");
            string trackedRecordId = GetMember(trackedRecord, "Id") as string;
            Ped threat = FindAttackerTarget(instance, companionPed, player, trackedRecordId);
            int companionHandle = companionPed.Handle;
            int previousTarget;
            bool hadPreviousTarget = CustomCompanionCombatTargets.TryGetValue(companionHandle, out previousTarget);

            if (threat == null)
            {
                // Keep idle companions passive. If an ambient AI reaction had already started a
                // fight, clear it unless its target qualifies as an active threat to the player.
                bool inCombat = false;
                try { inCombat = PED.IS_PED_IN_COMBAT(companionHandle, 0); } catch { }
                int lastSuppressedAt;
                bool suppressionCooldownElapsed = !CustomCombatSuppressedAt.TryGetValue(companionHandle, out lastSuppressedAt) || now - lastSuppressedAt >= 2500;
                if (hadPreviousTarget || (inCombat && suppressionCooldownElapsed))
                {
                    CustomCompanionCombatTargets.Remove(companionHandle);
                    CustomCombatSuppressedAt[companionHandle] = now;
                    try { TASK.CLEAR_PED_TASKS(companionHandle, true, false); } catch { }
                    try { InvokeInstance(instance, "CompanionIdle", companion, player); } catch { }
                }
                try { PED.SET_PED_COMBAT_ATTRIBUTES(companionHandle, 5, false); } catch { }
                try { PED.SET_PED_COMBAT_ATTRIBUTES(companionHandle, 46, false); } catch { }
                return;
            }

            int targetHandle = threat.Handle;
            CustomCombatSuppressedAt.Remove(companionHandle);
            bool sameTarget = hadPreviousTarget && previousTarget == targetHandle;
            bool currentlyFightingTarget = false;
            try { currentlyFightingTarget = PED.IS_PED_IN_COMBAT(companionHandle, targetHandle); } catch { }
            try { PED.SET_PED_COMBAT_ATTRIBUTES(companionHandle, 5, true); } catch { }
            try { PED.SET_PED_COMBAT_ATTRIBUTES(companionHandle, 46, true); } catch { }

            if (!sameTarget || !currentlyFightingTarget)
            {
                if (hadPreviousTarget && previousTarget != targetHandle)
                    try { TASK.CLEAR_PED_TASKS(companionHandle, true, false); } catch { }
                TASK.TASK_COMBAT_PED(companionHandle, targetHandle, 0, 16);
                CustomCompanionCombatTargets[companionHandle] = targetHandle;
                CompanionSystems.NotifyCombat(trackedRecordId, targetHandle);
            }
        }

        private static Ped FindAttackerTarget(object instance, Ped companion, Ped player, string recordId)
        {
            MethodInfo around = FindMethod(_mainType, "PedsAround", 1, p => IsPedType(p[0].ParameterType));
            if (around == null || !around.IsStatic) return null;

            IEnumerable nearby;
            try { nearby = around.Invoke(null, new object[] { player }) as IEnumerable; }
            catch { return null; }
            if (nearby == null) return null;

            Ped best = null;
            float bestDistance = 70f;
            foreach (object value in nearby)
            {
                Ped candidate = value as Ped;
                if (candidate == null || !candidate.Exists() || candidate.IsDead || !candidate.IsHuman ||
                    candidate.Handle == player.Handle || candidate.Handle == companion.Handle)
                    continue;

                float distance = candidate.Position.DistanceTo(player.Position);
                if (distance >= bestDistance || IsDisabledCombatTarget(instance, candidate)) continue;

                bool attackingPlayer = false;
                try { attackingPlayer = PED.IS_PED_IN_COMBAT(candidate.Handle, player.Handle); } catch { }
                // This intentionally excludes merely hostile / passing peds. A recruit responds
                // only after the candidate is actually in combat with the player, not merely hated.
                if (!attackingPlayer) continue;
                if (!CompanionSystems.IsTargetAllowed(recordId, companion, player, candidate, Game.GameTime)) continue;

                if (IsCachedRecruitedPed(candidate.Handle)) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private static bool IsDisabledCombatTarget(object instance, Ped target)
        {
            if (target == null || !target.Exists() || target.IsDead || target.IsHogtied || target.IsRagdoll)
                return true;
            try { if (TASK.IS_PED_IN_WRITHE(target.Handle)) return true; } catch { }
            try { if (TryStaticBool("LooksKnockedOut", target)) return true; } catch { }

            // enRAGE stores surrender state separately from the native ped flags, so check its
            // fighter table as well. A surrendered fighter must never be selected by our recruits.
            try
            {
                IDictionary fighters = GetField(instance, "_fighters") as IDictionary;
                if (fighters != null && fighters.Contains(target.Handle))
                {
                    object fighter = fighters[target.Handle];
                    bool done = GetField(fighter, "Done") is bool value && value;
                    string state = GetField(fighter, "State") as string ?? string.Empty;
                    if (done || string.Equals(state, "surrendered", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(state, "fled", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(state, "slipped away", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(state, "downed", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static void RestoreRealPostfix(object[] __args)
        {
            if (__args == null || __args.Length == 0)
                return;
            KeepRecruitedPedOnPlayerSide(__args[0]);
        }

        private static void KeepRecruitedPedOnPlayerSide(object companion)
        {
            try
            {
                string key = GetField(companion, "Key") as string;
                if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    return;

                Ped ped = GetField(companion, "Ped") as Ped;
                if (ped == null || !ped.Exists())
                    ped = GetField(companion, "Real") as Ped;
                Ped player = Game.Player.Character;
                if (ped == null || !ped.Exists() || ped.IsDead || player == null || !player.Exists())
                    return;

                try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                try { PED.REMOVE_PED_FROM_GROUP(ped.Handle); } catch { }
                ped.RelationshipGroup = EnsurePersistentRecruitRelationshipGroup(player);
                ApplyDismissedCombatProfile(ped);
            }
            catch (Exception ex)
            {
                Log("Persistent side switch warning: " + Unwrap(ex));
            }
        }

        private static uint EnsurePersistentRecruitRelationshipGroup(Ped player)
        {
            if (player == null || !player.Exists())
                return _persistentRecruitRelationshipGroup;

            if (_persistentRecruitRelationshipGroup == 0U)
                _persistentRecruitRelationshipGroup = World.AddRelationshipGroup("ENRAGE_PLAYER_SIDE_RECRUITS");

            int now = Game.GameTime;
            // Use both the player's actual group and the canonical PLAYER group. Some RDR2
            // threat-response scripts consult PLAYER rather than the current ped group.
            // Relationship 0 is Companion. Apply both directions every maintenance pass so a
            // threat event cannot leave one direction hostile.
            uint playerGroup = player.RelationshipGroup;
            World.SetRelationshipBetweenGroups(0, _persistentRecruitRelationshipGroup, playerGroup);
            World.SetRelationshipBetweenGroups(0, playerGroup, _persistentRecruitRelationshipGroup);
            World.SetRelationshipBetweenGroups(0, _persistentRecruitRelationshipGroup, PlayerRelationshipGroupHash);
            World.SetRelationshipBetweenGroups(0, PlayerRelationshipGroupHash, _persistentRecruitRelationshipGroup);
            _lastPlayerRelationshipGroup = playerGroup;
            _lastRelationshipConfigAt = now;
            return _persistentRecruitRelationshipGroup;
        }

        private static void ApplyDismissedCombatProfile(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead)
                return;

            // Off-duty recruits remain damageable and targetable, but must not react to ambient
            // threats as strangers. In NemesisMod.Setup, combat attrs 5/46 are enabled for active
            // followers; restore them to passive values after dismissal. Blocking non-temporary
            // events prevents the civilian panic/flee response to being aimed at or shot at.
            try { PED.SET_PED_COMBAT_ATTRIBUTES(ped.Handle, 5, false); } catch { }
            try { PED.SET_PED_COMBAT_ATTRIBUTES(ped.Handle, 46, false); } catch { }
            try { ped.BlockPermanentEvents = true; } catch { }

            // NemesisMod.Setup uses these RDR2 config flags. Keep the recruited member lock-on
            // targetable even while ambient reactions are blocked, without making him invulnerable.
            try { PED.SET_PED_CONFIG_FLAG(ped.Handle, 297, true); } catch { }
            try { PED.SET_PED_CONFIG_FLAG(ped.Handle, 301, false); } catch { }
            ApplyTargetingPolicy(ped);
        }

        private static void ApplyAmbientReleasedCombatProfile(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead) return;
            try { PED.SET_PED_COMBAT_ATTRIBUTES(ped.Handle, 5, false); } catch { }
            try { PED.SET_PED_COMBAT_ATTRIBUTES(ped.Handle, 46, false); } catch { }
            // The explicitly assigned TASK_WANDER_STANDARD continues to run, but block ambient
            // permanent events so staring, bumping, or incidental provocation does not turn the
            // former recruit into a hostile stranger. The relationship group is maintained separately.
            try { ped.BlockPermanentEvents = true; } catch { }
            try { PED.SET_PED_CONFIG_FLAG(ped.Handle, 297, true); } catch { }
            try { PED.SET_PED_CONFIG_FLAG(ped.Handle, 301, false); } catch { }
            ApplyTargetingPolicy(ped);
        }

        private static void ApplyTargetingPolicy(Ped ped)
        {
            if (ped == null || !ped.Exists()) return;
            bool blockPlayerTargeting = _preventTargeting || !_allowFriendlyFire;
            // These natives control whether the game lets the player select this ped as a target.
            // When blocked, RDR2 should show the disabled/grey target dot instead of merely absorbing damage.
            try { PED.SET_PED_CAN_BE_TARGETTED(ped.Handle, !blockPlayerTargeting); } catch (Exception ex) { Log("Set targetable warning: " + Unwrap(ex)); }
            try { PED.SET_PED_CAN_BE_TARGETTED_BY_PLAYER(ped.Handle, Game.Player.Character.Handle, !blockPlayerTargeting); } catch (Exception ex) { Log("Set player-targetable warning: " + Unwrap(ex)); }
        }

        private static void MaintainDismissedRecruitRelationships(object instance, Ped player)
        {
            int now = Game.GameTime;
            if (now >= 0 && now - _lastRelationshipMaintenanceAt < 500)
                return;
            _lastRelationshipMaintenanceAt = now;

            try
            {
                uint friendlyGroup = EnsurePersistentRecruitRelationshipGroup(player);
                if (friendlyGroup == 0U)
                    return;

                foreach (KeyValuePair<int, CachedRosterRecord> pair in RosterRecordsByPedHandle.ToArray())
                {
                    CachedRosterRecord cached = pair.Value;
                    Ped ped = cached == null ? null : cached.Ped;
                    if (ped == null || !ped.Exists() || ped.IsDead)
                        continue;
                    if (IsExtensionCompanionActiveForPed(instance, ped.Handle))
                        continue;

                    if (ped.RelationshipGroup != friendlyGroup)
                        ped.RelationshipGroup = friendlyGroup;
                    // Prevent enRAGE's social/procedural state from accumulating resentment while
                    // the former follower wanders independently.
                    ApplyMaximumRecruitFriendship(cached.Record);
                    bool ambientReleased = AmbientReleasedRecruitPeds.Contains(ped.Handle);
                    if (ambientReleased)
                    {
                        // Released recruits continue ordinary ambient behaviors and may walk away.
                        // Only interrupt an actual attack against the player, never an existing flee/wander task.
                        ApplyAmbientReleasedCombatProfile(ped);
                        int wanderAt;
                        if (AmbientReleasedWanderAt.TryGetValue(ped.Handle, out wanderAt) && now >= wanderAt)
                        {
                            if (ped.IsOnMount)
                            {
                                try { TASK.TASK_DISMOUNT_ANIMAL(ped.Handle, 0, 0UL, 0UL, 0UL, 0); } catch { }
                                AmbientReleasedWanderAt[ped.Handle] = now + 1800;
                            }
                            else
                            {
                                try { TASK.TASK_WANDER_STANDARD(ped.Handle, 10f, 10); } catch { }
                                AmbientReleasedWanderAt.Remove(ped.Handle);
                            }
                        }
                        bool fightingPlayer = false;
                        try { fightingPlayer = PED.IS_PED_IN_COMBAT(ped.Handle, player.Handle); } catch { }
                        if (fightingPlayer)
                        {
                            try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                            if (ped.RelationshipGroup != friendlyGroup) ped.RelationshipGroup = friendlyGroup;
                            ApplyAmbientReleasedCombatProfile(ped);
                            if (ped.IsOnMount)
                            {
                                try { TASK.TASK_DISMOUNT_ANIMAL(ped.Handle, 0, 0UL, 0UL, 0UL, 0); } catch { }
                                AmbientReleasedWanderAt[ped.Handle] = now + 1800;
                            }
                            else
                            {
                                try { TASK.TASK_WANDER_STANDARD(ped.Handle, 10f, 10); } catch { }
                            }
                        }
                        continue;
                    }

                    ApplyDismissedCombatProfile(ped);
                    bool dismissedFightingPlayer = false;
                    bool fleeing = false;
                    try { dismissedFightingPlayer = PED.IS_PED_IN_COMBAT(ped.Handle, player.Handle); } catch { }
                    try { fleeing = PED.IS_PED_FLEEING(ped.Handle); } catch { }

                    if (dismissedFightingPlayer || fleeing)
                    {
                        try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                        if (ped.RelationshipGroup != friendlyGroup)
                            ped.RelationshipGroup = friendlyGroup;
                        ApplyDismissedCombatProfile(ped);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Persistent recruited relationship maintenance warning: " + Unwrap(ex));
            }
        }

        private static bool IsCachedRecruitedPed(int handle)
        {
            CachedRosterRecord cached;
            if (handle == 0 || !RosterRecordsByPedHandle.TryGetValue(handle, out cached) ||
                cached == null || cached.Record == null || cached.Ped == null)
                return false;

            try
            {
                if (!cached.Ped.Exists() || cached.Ped.Handle != handle || cached.Ped.IsDead)
                    return false;
                uint actualModel = unchecked((uint)cached.Ped.Model.Hash);
                if (cached.ModelHash != 0U && actualModel != cached.ModelHash)
                    return false;
                if (GetMember(cached.Record, "IsAlive") is bool alive && !alive)
                    return false;
                return true;
            }
            catch { return false; }
        }

        private static bool IsExtensionCompanionActiveForPed(object instance, int handle)
        {
            IList comps = GetField(instance, "_comps") as IList;
            if (comps == null)
                return false;
            foreach (object companion in comps)
            {
                string key = GetField(companion, "Key") as string;
                if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    continue;
                Ped activePed = GetField(companion, "Ped") as Ped;
                Ped realPed = GetField(companion, "Real") as Ped;
                if ((activePed != null && activePed.Exists() && activePed.Handle == handle) ||
                    (realPed != null && realPed.Exists() && realPed.Handle == handle))
                    return true;
            }
            return false;
        }

        private static Ped FindCandidate(object instance, Ped player)
        {
            if (_roleOf == null || player == null || !player.Exists())
                return null;

            MethodInfo snapshotMethod = FindMethod(_mainType, "PedSnapshot", 1, p => IsPedType(p[0].ParameterType));
            if (snapshotMethod == null || !snapshotMethod.IsStatic)
                return null;

            object raw = snapshotMethod.Invoke(null, new object[] { player });
            IEnumerable snapshot = raw as IEnumerable;
            if (snapshot == null)
                return null;

            Ped best = null;
            float bestDistance = 3.5f;
            foreach (object tuple in snapshot)
            {
                Ped ped = TuplePed(tuple);
                if (ped == null || !ped.Exists())
                    continue;

                float distance = ped.Position.DistanceTo(player.Position);
                if (distance >= bestDistance || !IsEligibleGenericCandidate(instance, player, ped))
                    continue;

                best = ped;
                bestDistance = distance;
            }
            return best;
        }

        private static bool IsMalePed(Ped ped)
        {
            if (ped == null || !ped.Exists()) return false;
            try
            {
                if (_modelFemale == null && _mainType != null)
                {
                    Type modelsType = _mainType.Assembly.GetType("NemesisSystem.Models", false);
                    _modelFemale = FindMethod(modelsType, "Female", 1, p =>
                        p[0].ParameterType == typeof(uint) || p[0].ParameterType == typeof(int) ||
                        p[0].ParameterType == typeof(long) || p[0].ParameterType == typeof(ulong));
                }
                if (_modelFemale != null && _modelFemale.IsStatic)
                {
                    Type argumentType = _modelFemale.GetParameters()[0].ParameterType;
                    object argument = Convert.ChangeType(unchecked((uint)ped.Model.Hash), argumentType);
                    object result = _modelFemale.Invoke(null, new[] { argument });
                    if (result is bool female) return !female;
                }
            }
            catch (Exception ex) { Log("NPC gender check warning: " + Unwrap(ex)); }

            // Fail closed: if this enRAGE build no longer exposes its model-gender helper, do not
            // accidentally recruit a woman. This is safer than assuming a model is male.
            if (!_loggedMissingModelGender)
            {
                _loggedMissingModelGender = true;
                Log("WARNING: NemesisSystem.Models.Female(uint) could not be used. Generic recruitment is disabled until the gender helper can be resolved.");
            }
            return false;
        }

        private static bool IsEligibleGenericCandidate(object instance, Ped player, Ped ped, bool directInteractionTarget = false)
        {
            if (_roleOf == null || player == null || !player.Exists() || ped == null || !ped.Exists() ||
                ped.IsDead || !ped.IsHuman || !IsMalePed(ped) || ped.Handle == player.Handle || ped.IsHogtied ||
                ped.IsInVehicle || ped.IsOnMount || PED.IS_PED_IN_COMBAT(ped.Handle, 0))
                return false;

            if (ped.Position.DistanceTo(player.Position) > 3.5f)
                return false;
            if (AlreadyCompanion(GetField(instance, "_comps") as IList, ped.Handle))
                return false;
            // SocialTarget is backed by the game's current interaction target, so use that
            // authoritative selection instead of rejecting it on a separate LOS/group test.
            if (!directInteractionTarget)
            {
                if (!ENTITY.HAS_ENTITY_CLEAR_LOS_TO_ENTITY(ped.Handle, player.Handle, 17))
                    return false;

                int playerGroup = PED.GET_PED_GROUP_INDEX(player.Handle);
                if (playerGroup != 0 && PED.IS_PED_GROUP_MEMBER(ped.Handle, playerGroup, false))
                    return false;
            }

            string role;
            try { role = _roleOf.Invoke(instance, new object[] { ped.Handle })?.ToString() ?? ""; }
            catch { return false; }
            if (role != "None" && role != "Camp")
                return false;

            MethodInfo isStory = FindMethod(_mainType, "IsStoryPed", 1, p => IsPedType(p[0].ParameterType));
            MethodInfo gameEvent = FindMethod(_mainType, "GameEventPed", 1, p => IsPedType(p[0].ParameterType));
            Type kidsType = _mainType.Assembly.GetType("NemesisSystem.Kids", false);
            MethodInfo isChild = FindMethod(kidsType, "IsChild", 1, p => IsPedType(p[0].ParameterType));
            MethodInfo isLawman = FindMethod(_mainType, "IsLawman", 1, p => IsPedType(p[0].ParameterType));
            MethodInfo gameOwned = FindMethod(_mainType, "GameOwned", 1, p => IsPedType(p[0].ParameterType));
            if (isStory == null || !isStory.IsStatic || gameEvent == null || !gameEvent.IsStatic ||
                isChild == null || !isChild.IsStatic || isLawman == null || gameOwned == null)
                return false;

            if (TryStaticBool("IsStoryPed", ped) || TryStaticBool("GameEventPed", ped) ||
                TryStaticBoolOnType("NemesisSystem.Kids", "IsChild", ped))
                return false;

            if (role == "None" && (TryInstanceBool(instance, "IsLawman", ped) || TryInstanceBool(instance, "GameOwned", ped)))
                return false;

            // Do not reject IS_ENTITY_A_MISSION_ENTITY by itself. enRAGE can mark otherwise
            // ordinary, interactable civilians as mission entities; the explicit story/event,
            // lawman and GameOwned filters above are the safer exclusions.
            return true;
        }

        private static bool IsAskBlocked(object instance)
        {
            object menu = GetField(instance, "_menu");
            object isOpen = GetMember(menu, "IsOpen");
            if (isOpen is bool b && b)
                return true;
            return GetField(instance, "_convo") != null || GetField(instance, "_tg") != null;
        }

        private static int CountActiveCompanions(object instance)
        {
            IList list = GetField(instance, "_comps") as IList;
            if (list == null)
                return 0;

            int count = 0;
            foreach (object companion in list)
            {
                if (companion != null && GetFieldInt(companion, "State", 2) != 2)
                    count++;
            }
            return count;
        }

        private static bool AlreadyCompanion(IList list, int handle)
        {
            if (list == null)
                return false;
            foreach (object companion in list)
            {
                Ped real = GetField(companion, "Real") as Ped;
                Ped current = GetField(companion, "Ped") as Ped;
                if ((real != null && real.Handle == handle) || (current != null && current.Handle == handle))
                    return true;
            }
            return false;
        }

        private static void DetachFromLiveCamp(object instance, Ped ped)
        {
            object raw = GetField(instance, "_liveCamps");
            IDictionary camps = raw as IDictionary;
            if (camps == null)
                return;

            foreach (DictionaryEntry entry in camps)
            {
                object liveCamp = entry.Value;
                IList peds = GetField(liveCamp, "Peds") as IList;
                if (peds != null)
                    peds.Remove(ped);
            }
        }

        private static string FindRecordName(object instance, int handle)
        {
            if (_recordAt == null)
                return null;
            try
            {
                object record = _recordAt.Invoke(instance, new object[] { handle });
                if (record == null)
                    return null;
                object name = GetMember(record, "Name");
                return name as string;
            }
            catch { return null; }
        }

        private static object GetTrackedRecord(object instance, int handle)
        {
            if (_recordAt == null)
                return null;
            try { return _recordAt.Invoke(instance, new object[] { handle }); }
            catch (Exception ex) { Log("RecordAt lookup warning: " + Unwrap(ex)); return null; }
        }

        // Prepare a roster record without calling enRAGE's NewRecord factory. NewRecord indexes the
        // selected Gang's FirstNames/LastNames/Titles arrays directly; some otherwise valid factions
        // expose empty arrays and throw IndexOutOfRangeException. Construct the stable record fields
        // directly, then assign the existing enRAGE faction selected by RecruitGangId.
        private static object PrepareRosterRecord(object instance, Ped ped, uint modelHash, object existingRecord, out bool newlyCreated)
        {
            newlyCreated = false;
            string targetGang = (_recruitGangId ?? "vdl").Trim();
            if (targetGang.Length == 0) targetGang = "vdl";
            if (!IsConfiguredRecruitmentFactionAvailable())
            {
                Log("Cannot prepare roster record: enRAGE faction ID '" + targetGang + "' was not found. Set RecruitGangId to an existing enRAGE faction ID.");
                return null;
            }

            if (existingRecord == null)
                existingRecord = FindReusableRosterRecord(instance, ped, modelHash);

            if (existingRecord != null)
            {
                object isStory = GetMember(existingRecord, "Story");
                string recordId = GetMember(existingRecord, "Id") as string;
                if (isStory is bool story && story && IsBuiltInStoryRecordId(recordId))
                {
                    Log("Roster membership unchanged: refusing to rewrite built-in story record for ped " + ped.Handle + ".");
                    return existingRecord;
                }

                // The character is a recruited gang member, not one of enRAGE's scripted cast members.
                SetStoryFlag(existingRecord, false);
                EnsureRosterProfileData(instance, existingRecord);
                return existingRecord;
            }

            Type recordType = _mainType.Assembly.GetType("NemesisSystem.NemesisRecord", false);
            if (recordType == null)
            {
                recordType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "NemesisSystem.NemesisRecord"))
                    .FirstOrDefault(t => t != null);
            }
            if (recordType == null)
            {
                Log("Cannot create roster member: NemesisSystem.NemesisRecord type was not found.");
                return null;
            }

            object created;
            try { created = Activator.CreateInstance(recordType, true); }
            catch (Exception ex)
            {
                Log("Cannot construct NemesisRecord: " + Unwrap(ex));
                return null;
            }
            if (created == null)
                return null;

            string zone = InvokeInstance(instance, "GetZoneName", ped.Position) as string;
            if (string.IsNullOrWhiteSpace(zone))
                zone = string.Empty;

            string preferredName = FindNamedPedName(instance, ped.Handle);
            string name = UniqueRosterName(instance, preferredName);
            string id = null;
            MethodInfo newIdMethod = FindMethod(_mainType, "NewId", 1, p => p[0].ParameterType == typeof(int));
            if (newIdMethod != null)
            {
                try { id = newIdMethod.Invoke(instance, new object[] { 12 }) as string; }
                catch { /* Use a locally generated ID if this enRAGE build changes NewId. */ }
            }
            if (string.IsNullOrWhiteSpace(id))
                id = Guid.NewGuid().ToString("N").Substring(0, 12);

            // A faction-neutral extension ID keeps recruited characters distinct from enRAGE's
            // built-in vdl_<name> story-cast IDs regardless of the configured faction.
            id = "extcomp_" + id;

            bool coreFieldsSet = SetMember(created, "Id", id);
            coreFieldsSet = SetMember(created, "Name", name) && coreFieldsSet;
            coreFieldsSet = SetMember(created, "ModelHash", modelHash) && coreFieldsSet;
            coreFieldsSet = SetMember(created, "Gang", targetGang) && coreFieldsSet;
            coreFieldsSet = SetMember(created, "IsAlive", true) && coreFieldsSet;
            if (!coreFieldsSet)
            {
                Log("Cannot create roster member: one or more required NemesisRecord properties could not be set.");
                return null;
            }

            SetMember(created, "Title", string.Empty);
            SetMember(created, "Rank", 1);
            SetMember(created, "RankXp", 0);
            SetMember(created, "PresumedDead", false);
            SetMember(created, "KillLocation", zone);
            SetMember(created, "KilledPlayerAt", DateTime.Now);
            SetMember(created, "FormerGang", null);
            SetMember(created, "Affiliation", null);
            SetMember(created, "BossId", null);
            SetMember(created, "CampId", null);
            SetMember(created, "CampKnown", false);
            SetStoryFlag(created, false);
            EnsureRosterProfileData(instance, created);

            newlyCreated = true;
            Log("Prepared roster record " + name + " for faction '" + targetGang + "', model=0x" + modelHash.ToString("X8") + ".");
            return created;
        }

        private static object FindReusableRosterRecord(object instance, Ped ped, uint modelHash)
        {
            CachedRosterRecord cached;
            if (RosterRecordsByPedHandle.TryGetValue(ped.Handle, out cached) && cached != null &&
                cached.ModelHash == modelHash && cached.Record != null &&
                (!(GetMember(cached.Record, "IsAlive") is bool cachedAlive) || cachedAlive))
                return cached.Record;

            IList roster = GetMember(instance, "Roster") as IList;
            if (roster == null)
                return null;

            string preferredName = FindNamedPedName(instance, ped.Handle);
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                foreach (object record in roster)
                {
                    if (record == null || IsBuiltInStoryRecordId(GetMember(record, "Id") as string))
                        continue;
                    if (!string.Equals(GetMember(record, "Name") as string, preferredName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ConvertToUInt32(GetMember(record, "ModelHash")) != modelHash ||
                        !string.Equals(GetMember(record, "Gang") as string, (_recruitGangId ?? "vdl").Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (GetMember(record, "IsAlive") is bool alive && !alive)
                        continue;
                    if (IsRosterRecordCachedForOtherPed(record, ped.Handle))
                        continue;
                    Log("Reusing existing configured-faction roster record by character name: " + preferredName + ".");
                    return record;
                }
            }

            // Compatibility with records made by older extension builds, which used legacy extvdl_
            // IDs and may have lost their name association after dismissal. Only reuse records in
            // the configured faction, and never hand one live sheet to two different peds.
            foreach (object record in roster)
            {
                if (record == null || IsBuiltInStoryRecordId(GetMember(record, "Id") as string))
                    continue;
                if (!string.Equals(GetMember(record, "Gang") as string, (_recruitGangId ?? "vdl").Trim(), StringComparison.OrdinalIgnoreCase) ||
                    ConvertToUInt32(GetMember(record, "ModelHash")) != modelHash)
                    continue;
                if (GetMember(record, "IsAlive") is bool alive && !alive)
                    continue;
                if (IsRosterRecordCachedForOtherPed(record, ped.Handle))
                    continue;
                Log("Reusing existing configured-faction roster record for model 0x" + modelHash.ToString("X8") + ".");
                return record;
            }
            return null;
        }

        private static bool IsRosterRecordCachedForOtherPed(object record, int handle)
        {
            foreach (KeyValuePair<int, CachedRosterRecord> pair in RosterRecordsByPedHandle)
            {
                if (pair.Key != handle && pair.Value != null && object.ReferenceEquals(pair.Value.Record, record))
                    return true;
            }
            return false;
        }

        private static void CacheRosterRecord(int handle, uint modelHash, object record, Ped ped)
        {
            if (record == null)
                return;
            string id = GetMember(record, "Id") as string;
            if (!string.IsNullOrWhiteSpace(id) && modelHash != 0U)
                RecruitedRosterModels[id] = modelHash;
            if (handle != 0)
            {
                RosterRecordsByPedHandle[handle] = new CachedRosterRecord { ModelHash = modelHash, Record = record, Ped = ped };
                AmbientReleasedRecruitPeds.Remove(handle);
                AmbientReleasedWanderAt.Remove(handle);
                ApplyPendingFullHealthIfNeeded(record, ped);
            }
        }

        private static void UpdateCachedRecruitedPedDeaths(object instance)
        {
            bool changed = false;
            foreach (KeyValuePair<int, CachedRosterRecord> pair in RosterRecordsByPedHandle.ToArray())
            {
                CachedRosterRecord cached = pair.Value;
                if (cached == null || cached.Record == null || cached.Ped == null)
                    continue;
                if (!(GetMember(cached.Record, "IsAlive") is bool alive) || !alive)
                    continue;
                try
                {
                    if (!cached.Ped.Exists() || !cached.Ped.IsDead)
                        continue;
                    SetMember(cached.Record, "IsAlive", false);
                    SetMember(cached.Record, "PresumedDead", false);
                    AppliedPromptNames.Remove(pair.Key);
                    CustomCompanionCombatTargets.Remove(pair.Key);
                    CustomCombatSuppressedAt.Remove(pair.Key);
                    string zone = InvokeInstance(instance, "GetZoneName", cached.Ped.Position) as string;
                    if (!string.IsNullOrWhiteSpace(zone))
                        SetMember(cached.Record, "LastDefeatLocation", zone);
                    Log("Marked recruited faction member dead: " +
                        (GetMember(cached.Record, "Name") as string ?? pair.Key.ToString()) + ".");
                    changed = true;
                }
                catch { /* A streamed-out or recycled entity handle is not enough evidence of death. */ }
            }
            if (changed)
            {
                try { InvokeInstance(instance, "SaveRoster"); }
                catch (Exception ex) { Log("Saving dead recruited members failed: " + Unwrap(ex)); }
            }
        }

        private static uint ConvertToUInt32(object value)
        {
            if (value == null)
                return 0;
            try { return Convert.ToUInt32(value); }
            catch { return 0; }
        }

        private static bool IsBuiltInStoryRecordId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && id.StartsWith("vdl_", StringComparison.OrdinalIgnoreCase);
        }

        // Use the same initialization path as enRAGE's own NewRecord factory, without calling
        // NewRecord itself (that method can index empty gang name arrays). This fills the character's
        // personality, traits, mind and backstory. Revealing the generated past is appropriate because
        // this is a member who has been recruited into the player's own gang.
        private static bool EnsureRosterProfileData(object instance, object record)
        {
            if (instance == null || record == null || IsBuiltInStoryRecordId(GetMember(record, "Id") as string))
                return false;

            SetStoryFlag(record, false);
            bool pastRolled = GetMember(record, "PastRolled") is bool past && past;
            bool mindRolled = GetMember(record, "MindRolled") is bool mind && mind;
            bool traitsRolled = GetMember(record, "TraitsRolled") is bool traits && traits;
            bool hasPersonality = GetMember(record, "Personality") != null;
            bool needsInitialization = !pastRolled || !mindRolled || !traitsRolled || !hasPersonality;
            if (!needsInitialization)
                return RevealPastEntries(record);

            bool initialized = false;
            try
            {
                MethodInfo rollNewLife = FindMethod(_mainType, "RollNewLife", 4, p =>
                    p[0].ParameterType.Name == "NemesisRecord" && p[2].ParameterType == typeof(bool) && p[3].ParameterType == typeof(int));
                IList roster = GetMember(instance, "Roster") as IList;
                if (rollNewLife != null && !rollNewLife.IsStatic && roster != null)
                {
                    rollNewLife.Invoke(instance, new object[] { record, roster, true, 2 });
                    initialized = true;
                }
            }
            catch (Exception ex)
            {
                Log("Roster profile initialization warning for " + (GetMember(record, "Name") as string ?? "recruit") + ": " + Unwrap(ex));
            }

            // Fallbacks are safe when RollNewLife is missing or only partly completed.
            try { InvokeInstance(instance, "EnsurePast", record); } catch { }
            try { InvokeInstance(instance, "EnsureMind", record); } catch { }
            bool pastRevealed = RevealPastEntries(record);

            bool nowPast = GetMember(record, "PastRolled") is bool pastNow && pastNow;
            bool nowMind = GetMember(record, "MindRolled") is bool mindNow && mindNow;
            bool nowTraits = GetMember(record, "TraitsRolled") is bool traitsNow && traitsNow;
            if (!(nowPast && nowMind && nowTraits))
            {
                Log("Roster profile remains partly incomplete for " + (GetMember(record, "Name") as string ?? "recruit") +
                    " (past=" + nowPast + ", mind=" + nowMind + ", traits=" + nowTraits + ").");
            }
            else if (initialized || needsInitialization)
            {
                Log("Initialized mind, backstory and traits for recruited faction member " +
                    (GetMember(record, "Name") as string ?? "(unnamed)") + ".");
            }
            return pastRevealed || (!pastRolled && nowPast) || (!mindRolled && nowMind) ||
                (!traitsRolled && nowTraits) || (!hasPersonality && GetMember(record, "Personality") != null);
        }

        private static bool RevealPastEntries(object record)
        {
            bool changed = false;
            IEnumerable pastEntries = GetMember(record, "Past") as IEnumerable;
            if (pastEntries == null)
                return false;
            foreach (object entry in pastEntries)
            {
                if (entry == null)
                    continue;
                if (!(GetMember(entry, "Known") is bool known && known))
                    changed |= SetMember(entry, "Known", true);
                if (!(GetMember(entry, "Public") is bool isPublic && isPublic))
                    changed |= SetMember(entry, "Public", true);
            }
            return changed;
        }

        private static string FindNamedPedName(object instance, int handle)
        {
            try
            {
                IDictionary named = GetField(instance, "_named") as IDictionary;
                if (named == null || !named.Contains(handle))
                    return null;
                return GetMember(named[handle], "Name") as string;
            }
            catch { return null; }
        }

        private static string UniqueRosterName(object instance, string preferredName)
        {
            if (!string.IsNullOrWhiteSpace(preferredName) && !RosterNameExists(instance, preferredName))
                return preferredName;

            for (int i = 1; i <= 9999; i++)
            {
                string candidate = "Aliado " + i;
                if (!RosterNameExists(instance, candidate))
                    return candidate;
            }
            return "Aliado " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static bool RosterNameExists(object instance, string name)
        {
            foreach (string collectionName in new[] { "Roster", "StoryRoster" })
            {
                IEnumerable records = GetMember(instance, collectionName) as IEnumerable;
                if (records == null)
                    continue;
                foreach (object record in records)
                {
                    string existingName = GetMember(record, "Name") as string;
                    if (string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static void CommitRosterRecord(object instance, object record, bool newlyCreated, int handle)
        {
            if (record == null)
            {
                Log("Roster record unavailable for recruited ped " + handle + ".");
                return;
            }

            object isStory = GetMember(record, "Story");
            if (isStory is bool story && story)
            {
                Log("Roster membership unchanged: refusing to rewrite story record for ped " + handle + ".");
                return;
            }

            string targetGang = (_recruitGangId ?? "vdl").Trim();
            if (targetGang.Length == 0) targetGang = "vdl";
            string oldGang = GetMember(record, "Gang") as string;
            SetStoryFlag(record, IsBuiltInStoryRecordId(GetMember(record, "Id") as string));
            EnsureRosterProfileData(instance, record);
            ApplyMaximumRecruitFriendship(record);

            // Regular enRAGE factions are represented by Roster records whose Gang field holds the
            // configured faction ID. The built-in VDL story-only faction additionally needs StoryRoster.
            if (!string.Equals(oldGang, targetGang, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(oldGang))
                    SetMember(record, "FormerGang", oldGang);
                if (!SetMember(record, "Gang", targetGang))
                    throw new InvalidOperationException("NemesisRecord.Gang could not be set.");
            }
            SetMember(record, "BossId", null);
            SetMember(record, "CampId", null);
            SetMember(record, "CampKnown", false);
            if (string.Equals(GetMember(record, "Affiliation") as string, targetGang, StringComparison.OrdinalIgnoreCase))
                SetMember(record, "Affiliation", null);

            IList roster = GetMember(instance, "Roster") as IList;
            if (newlyCreated)
            {
                if (roster == null)
                    throw new InvalidOperationException("NemesisMod.Roster list was not accessible.");
                if (!roster.Contains(record))
                    roster.Add(record);
            }
            EnsureRecruitmentHistory(instance, record);

            MethodInfo saveRoster = FindMethod(_mainType, "SaveRoster", 0);
            if (saveRoster == null)
                throw new MissingMethodException(_mainType.FullName, "SaveRoster");
            saveRoster.Invoke(instance, null);

            // Only the built-in Van der Linde faction uses enRAGE's special StoryRoster. Custom and
            // regular factions automatically show these Roster records in their normal faction pages.
            if (UsesVanDerLindeStoryRoster())
                EnsureVanDerLindeStoryRosterEntry(instance, record);

            Log((newlyCreated ? "Added new roster member " : "Updated roster member ") +
                (GetMember(record, "Name") as string ?? "(unnamed)") + " in gang '" + targetGang + "'.");
        }

        private static void EnsureRecruitmentHistory(object instance, object record)
        {
            IList history = GetMember(record, "History") as IList;
            if (history != null && history.Count > 0)
                return;
            MethodInfo addHistory = FindMethod(_mainType, "History", 3, p =>
                p[0].ParameterType.Name == "NemesisRecord" && p[1].ParameterType == typeof(string));
            if (addHistory == null)
                return;
            try
            {
                addHistory.Invoke(instance, new object[] { record, "SE INCORPORÓ a la facción " + ConfiguredRecruitmentFactionName() + " tras ser reclutado", null });
            }
            catch (Exception ex)
            {
                Log("Could not add recruitment history for " + (GetMember(record, "Name") as string ?? "recruit") + ": " + Unwrap(ex));
            }
        }

        private static void SyncVanDerLindeStoryRoster(object instance, bool force)
        {
            if (instance == null) return;
            // Apply permanent exclusions even when recruitment configuration no longer targets VDL.
            EnforceExiledRosterMembers(instance);
            if (!UsesVanDerLindeStoryRoster())
                return;

            int now = Game.GameTime;
            if (!force && now >= 0 && now - _lastStoryRosterSyncAt < 5000)
                return;
            _lastStoryRosterSyncAt = now;

            try
            {
                IEnumerable roster = GetMember(instance, "Roster") as IEnumerable;
                if (roster == null)
                    return;

                int added = 0;
                int profilesUpdated = 0;
                foreach (object record in roster.Cast<object>().ToArray())
                {
                    if (record == null) continue;
                    string recordId = GetMember(record, "Id") as string;
                    // EnforceExiledRosterMembers already removed exiled IDs from the story
                    // view and cleared their gang affiliation from the persistent roster.
                    if (!string.IsNullOrWhiteSpace(recordId) && ExiledRosterIds.Contains(recordId))
                        continue;
                    if (!string.Equals(GetMember(record, "Gang") as string, "vdl", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool builtInStoryRecord = IsBuiltInStoryRecordId(recordId);
                    SetStoryFlag(record, builtInStoryRecord);
                    if (!builtInStoryRecord && EnsureRosterProfileData(instance, record))
                        profilesUpdated++;
                    if (EnsureVanDerLindeStoryRosterEntry(instance, record))
                        added++;
                }

                if (added > 0 || profilesUpdated > 0)
                {
                    MethodInfo saveRoster = FindMethod(_mainType, "SaveRoster", 0);
                    if (saveRoster != null)
                        saveRoster.Invoke(instance, null);
                }
                if (added > 0)
                    Log("Synchronized " + added + " Van der Linde member(s) with StoryRoster for the F11 view.");
                if (profilesUpdated > 0)
                    Log("Initialized profile data for " + profilesUpdated + " existing Van der Linde member(s).");
            }
            catch (Exception ex)
            {
                Log("Van der Linde StoryRoster sync warning: " + Unwrap(ex));
            }
        }

        private static bool EnsureVanDerLindeStoryRosterEntry(object instance, object record)
        {
            string id = record == null ? null : GetMember(record, "Id") as string;
            if (instance == null || record == null ||
                (!string.IsNullOrWhiteSpace(id) && ExiledRosterIds.Contains(id)) ||
                !string.Equals(GetMember(record, "Gang") as string, "vdl", StringComparison.OrdinalIgnoreCase))
                return false;

            IList storyRoster = GetMember(instance, "StoryRoster") as IList;
            if (storyRoster == null)
                return false;

            // Only the game's built-in vdl_<name> records are story-cast records. Recruits are
            // placed in the same F11 list, but remain ordinary NemesisRecord sheets so their mind,
            // backstory, traits and known past render through the normal character profile.
            SetStoryFlag(record, IsBuiltInStoryRecordId(GetMember(record, "Id") as string));
            if (storyRoster.Contains(record))
                return false;

            storyRoster.Add(record);
            return true;
        }

        private static void SetStoryFlag(object record, bool value)
        {
            if (SetMember(record, "Story", value))
                return;

            // Handle builds where Story is backed by a differently named private field.
            foreach (string fieldName in new[] { "_story", "story", "isStory", "_isStory", "<Story>k__BackingField" })
            {
                if (SetField(record, fieldName, value))
                    return;
            }
        }

        private static object FindCompanionByKey(object instance, string key)
        {
            IList comps = GetField(instance, "_comps") as IList;
            if (comps == null || string.IsNullOrEmpty(key))
                return null;
            foreach (object companion in comps)
            {
                if (string.Equals(GetField(companion, "Key") as string, key, StringComparison.Ordinal))
                    return companion;
            }
            return null;
        }

        private static void DismissRecruitedCompanion(object instance, object companion)
        {
            if (companion == null) return;
            string name = GetField(companion, "Name") as string ?? "Tu aliado";
            string key = GetField(companion, "Key") as string;
            string rosterRecordId = GetMember(GetField(companion, "Rec"), "Id") as string;
            Ped ped = GetField(companion, "Ped") as Ped;
            if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
            bool preserveHorse = false;
            Ped horse = GetField(companion, "Horse") as Ped;
            Ped player = Game.Player.Character;
            try { preserveHorse = ped != null && ped.Exists() && ped.IsOnMount && horse != null && horse.Exists(); } catch { }

            // Restore the NPC's pre-companion health, weapons, flags and combat settings before
            // removing enRAGE's companion wrapper. A clone is explicitly detached from Cleanup's
            // deletion path: only the wrapper, blip and idle reservations are cleaned up.
            try { InvokeInstance(instance, "RestoreReal", companion); }
            catch (Exception ex) { Log("Dismissal ped restoration warning: " + Unwrap(ex)); }

            if (preserveHorse)
                SetField(companion, "Horse", null); // Let the departing NPC dismount; do not delete his current mount.
            SetField(companion, "Ped", null);
            SetField(companion, "Real", null);
            try { InvokeInstance(instance, "Cleanup", companion); }
            catch (Exception ex) { Log("Companion wrapper cleanup warning: " + Unwrap(ex)); }

            if (ped != null && ped.Exists() && !ped.IsDead)
            {
                try { ENTITY.SET_ENTITY_INVINCIBLE(ped.Handle, false); } catch { }
                try { PED.REMOVE_PED_FROM_GROUP(ped.Handle); } catch { }
                try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                try { ped.RelationshipGroup = EnsurePersistentRecruitRelationshipGroup(player); } catch { }
                ApplyAmbientReleasedCombatProfile(ped);
                AmbientReleasedRecruitPeds.Remove(ped.Handle);
                AmbientReleasedWanderAt.Remove(ped.Handle);
                CustomCompanionCombatTargets.Remove(ped.Handle);
                CustomCombatSuppressedAt.Remove(ped.Handle);
                CompanionSystems.OnDismissed(rosterRecordId, ped);
            }

            IList comps = GetField(instance, "_comps") as IList;
            if (comps != null)
            {
                try { comps.Remove(companion); } catch { }
            }
            ModelHashes.Remove(key);
            SetField(instance, "_askPed", null);
            SetField(instance, "_askKey", null);
            SetField(instance, "_askComp", null);
            InvokeInstance(instance, "HideAskPrompt");
            InvokeInstance(instance, "ShowSubtitle", name + " deja de seguirte y vuelve a comportarse como un NPC del mundo.");
            Log("Dismissed extension companion " + name + "; pedestrian preserved and ambient AI resumed.");
        }

        private static void MarkRecruitedCompanionDeath(object instance, object companion)
        {
            try
            {
                string key = GetField(companion, "Key") as string;
                if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    return;

                object record = GetField(companion, "Rec");
                if (record == null || !(GetMember(record, "IsAlive") is bool alive) || !alive)
                    return;

                Ped ped = GetField(companion, "Ped") as Ped;
                if (ped == null || !ped.Exists())
                    ped = GetField(companion, "Real") as Ped;
                if (ped == null || !ped.Exists() || !ped.IsDead)
                    return;

                SetMember(record, "IsAlive", false);
                SetMember(record, "PresumedDead", false);
                string zone = InvokeInstance(instance, "GetZoneName", ped.Position) as string;
                if (!string.IsNullOrWhiteSpace(zone))
                    SetMember(record, "LastDefeatLocation", zone);
                InvokeInstance(instance, "SaveRoster");
                Log("Marked recruited roster member dead: " + (GetMember(record, "Name") as string ?? key) + ".");
            }
            catch (Exception ex)
            {
                Log("Recruited companion death tracking warning: " + Unwrap(ex));
            }
        }

        private static Ped TuplePed(object tuple)
        {
            if (tuple == null)
                return null;
            object value = GetMember(tuple, "Item1");
            return value as Ped;
        }

        private static bool TryInstanceBool(object instance, string methodName, Ped ped)
        {
            MethodInfo method = FindMethod(_mainType, methodName, 1, p => IsPedType(p[0].ParameterType));
            if (method == null)
                return false;
            try { return method.Invoke(instance, new object[] { ped }) is bool result && result; }
            catch { return false; }
        }

        private static bool TryStaticBool(string methodName, Ped ped)
        {
            MethodInfo method = FindMethod(_mainType, methodName, 1, p => IsPedType(p[0].ParameterType));
            if (method == null || !method.IsStatic)
                return false;
            try { return method.Invoke(null, new object[] { ped }) is bool result && result; }
            catch { return false; }
        }

        private static bool TryStaticBoolOnType(string typeName, string methodName, Ped ped)
        {
            try
            {
                Type type = _mainType.Assembly.GetType(typeName, false);
                if (type == null)
                    return false;
                MethodInfo method = FindMethod(type, methodName, 1, p => IsPedType(p[0].ParameterType));
                return method != null && method.IsStatic && method.Invoke(null, new object[] { ped }) is bool result && result;
            }
            catch { return false; }
        }

        private static MethodInfo FindMethod(Type type, string name, int parameterCount, Func<ParameterInfo[], bool> predicate = null)
        {
            if (type == null)
                return null;
            return type.GetMethods(InstanceFlags | StaticFlags)
                .Where(m => m.Name == name)
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] parameters = m.GetParameters();
                    return parameters.Length == parameterCount && (predicate == null || predicate(parameters));
                });
        }

        private static bool IsPedType(Type type)
        {
            return type != null && type.FullName == typeof(Ped).FullName;
        }

        private static Type SafeGetType(Assembly assembly, string name)
        {
            try { return assembly.GetType(name, false); }
            catch { return null; }
        }

        private static object InvokeInstance(object instance, string name, params object[] args)
        {
            if (instance == null)
                return null;
            MethodInfo method = FindMethod(instance.GetType(), name, args == null ? 0 : args.Length,
                p => args == null || p.Length == args.Length);
            if (method == null)
                return null;
            try { return method.Invoke(instance, args); }
            catch (Exception ex) { Log(name + " invocation failed: " + Unwrap(ex)); return null; }
        }

        private static object GetField(object obj, string name)
        {
            if (obj == null)
                return null;
            for (Type t = obj.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(name, InstanceFlags | StaticFlags | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try { return field.GetValue(field.IsStatic ? null : obj); }
                    catch { return null; }
                }
            }
            return null;
        }

        private static int GetFieldInt(object obj, string name, int fallback)
        {
            object value = GetField(obj, name);
            return value == null ? fallback : Convert.ToInt32(value);
        }

        private static bool SetField(object obj, string name, object value)
        {
            if (obj == null)
                return false;
            for (Type t = obj.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(name, InstanceFlags | StaticFlags | BindingFlags.DeclaredOnly);
                if (field == null)
                    continue;
                try
                {
                    object converted = value;
                    if (value != null && !field.FieldType.IsInstanceOfType(value))
                    {
                        if (field.FieldType.IsEnum)
                            converted = Enum.ToObject(field.FieldType, value);
                        else
                            converted = Convert.ChangeType(value, field.FieldType);
                    }
                    field.SetValue(field.IsStatic ? null : obj, converted);
                    return true;
                }
                catch (Exception ex)
                {
                    Log("Could not set " + name + ": " + Unwrap(ex));
                    return false;
                }
            }
            return false;
        }

        private static bool SetMember(object obj, string name, object value)
        {
            if (obj == null)
                return false;

            try
            {
                Type type = obj.GetType();
                PropertyInfo property = type.GetProperty(name, InstanceFlags | StaticFlags);
                if (property != null && property.CanWrite)
                {
                    object converted = value;
                    if (value != null && !property.PropertyType.IsInstanceOfType(value))
                    {
                        if (property.PropertyType.IsEnum)
                            converted = Enum.ToObject(property.PropertyType, value);
                        else
                            converted = Convert.ChangeType(value, property.PropertyType);
                    }
                    property.SetValue(property.GetSetMethod(true).IsStatic ? null : obj, converted, null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("Could not set property " + name + ": " + Unwrap(ex));
                return false;
            }

            return SetField(obj, name, value);
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null)
                return null;
            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(name, InstanceFlags | StaticFlags);
            if (property != null)
            {
                try { return property.GetValue(obj, null); } catch { }
            }
            return GetField(obj, name);
        }

        private static string GetScriptsDirectory()
        {
            string baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            DirectoryInfo info = new DirectoryInfo(baseDirectory);
            if (info.Name.Equals("scripts", StringComparison.OrdinalIgnoreCase))
                return info.FullName;
            return Path.Combine(baseDirectory, "scripts");
        }

        private static string ResolveScriptsFile(string fileName)
        {
            string scriptsDirectory = GetScriptsDirectory();
            Directory.CreateDirectory(scriptsDirectory);
            string correctPath = Path.Combine(scriptsDirectory, fileName);

            // Migrate files created by the previous path logic when ScriptHook's AppDomain
            // base already pointed to the game's scripts directory (scripts\scripts\file).
            string oldPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", fileName));
            correctPath = Path.GetFullPath(correctPath);
            if (!oldPath.Equals(correctPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                try
                {
                    if (!File.Exists(correctPath))
                    {
                        File.Move(oldPath, correctPath);
                    }
                    else if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    {
                        // Preserve the log accumulated in the accidental nested scripts folder.
                        string oldText = File.ReadAllText(oldPath);
                        string currentText = File.ReadAllText(correctPath);
                        File.WriteAllText(correctPath, oldText + Environment.NewLine + currentText);
                        File.Delete(oldPath);
                    }
                    else
                    {
                        // Keep the correctly located INI as authoritative, then remove the duplicate.
                        File.Delete(oldPath);
                    }
                }
                catch { /* Logging/configuration must never break a game tick. */ }

                try
                {
                    string legacyDirectory = Path.GetDirectoryName(oldPath);
                    if (Directory.Exists(legacyDirectory) && !Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
                        Directory.Delete(legacyDirectory, false);
                }
                catch { }
            }
            return correctPath;
        }

        private static string ReadConfiguration()
        {
            string path = ResolveScriptsFile("EnRAGECompanionsExtension.ini");
            try
            {
                if (File.Exists(path))
                {
                    string[] existingLines = File.ReadAllLines(path);
                    HashSet<string> existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string existingLine in existingLines)
                    {
                        string candidateLine = existingLine.Trim();
                        int candidateSplit = candidateLine.IndexOf('=');
                        if (candidateSplit > 0) existingKeys.Add(candidateLine.Substring(0, candidateSplit).Trim());
                    }
                    List<string> missingSettings = new List<string>();
                    if (!existingKeys.Contains("RecruitGangId") && !existingKeys.Contains("PersistentNemesisGangId")) missingSettings.Add("RecruitGangId=vdl");
                    if (!existingKeys.Contains("RecruitRosterId")) missingSettings.Add("RecruitRosterId=vdl");
                    if (!existingKeys.Contains("AllowFriendlyFire")) missingSettings.Add("AllowFriendlyFire=true");
                    if (!existingKeys.Contains("PreventTargeting")) missingSettings.Add("PreventTargeting=false");
                    if (missingSettings.Count > 0)
                    {
                        File.AppendAllText(path, Environment.NewLine + "; Added settings for recruitment and targeting" + Environment.NewLine + string.Join(Environment.NewLine, missingSettings) + Environment.NewLine);
                    }
                    foreach (string raw in existingLines)
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                            continue;
                        int split = line.IndexOf('=');
                        if (split <= 0)
                            continue;
                        string key = line.Substring(0, split).Trim();
                        string value = line.Substring(split + 1).Trim();
                        if (key.Equals("MaxCompanions", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int max))
                            _maxCompanions = Math.Max(1, Math.Min(7, max));
                        else if (key.Equals("PersistentNemesisGangId", StringComparison.OrdinalIgnoreCase) && !existingKeys.Contains("RecruitGangId"))
                            _recruitGangId = string.IsNullOrWhiteSpace(value) ? "vdl" : value;
                        else if (key.Equals("RecruitGangId", StringComparison.OrdinalIgnoreCase))
                            _recruitGangId = string.IsNullOrWhiteSpace(value) ? "vdl" : value;
                        else if (key.Equals("RecruitRosterId", StringComparison.OrdinalIgnoreCase))
                            _recruitRosterId = string.IsNullOrWhiteSpace(value) ? "vdl" : value;
                        else if (key.Equals("AllowFriendlyFire", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool friendlyFire))
                            _allowFriendlyFire = friendlyFire;
                        else if (key.Equals("PreventTargeting", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool preventTargeting))
                            _preventTargeting = preventTargeting;
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path,
                        "; EnRAGE Companions Extension settings\r\n" +
                        "MaxCompanions=3\r\n" +
                        "; Existing enRAGE faction ID assigned to recruited NPCs; default vdl = Van der Linde.\r\n" +
                        "RecruitGangId=vdl\r\n" +
                        "; Faction/roster ID used for roster actions. For custom factions, set this to the same ID as RecruitGangId.\r\n" +
                        "; Only vdl uses enRAGE's special Van der Linde StoryRoster; normal custom factions use Roster/Gang.\r\n" +
                        "RecruitRosterId=vdl\r\n" +
                        "; True allows player damage/friendly fire; false blocks player targeting.\r\n" +
                        "AllowFriendlyFire=true\r\n" +
                        "; Force the grey/disabled target dot even if friendly fire is enabled.\r\n" +
                        "PreventTargeting=false\r\n");
                }
            }
            catch (Exception ex)
            {
                Log("Config warning: " + Unwrap(ex));
            }
            return path;
        }

        private static string Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException tie && tie.InnerException != null)
                ex = tie.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static void Log(string message)
        {
            try
            {
                string path = ResolveScriptsFile("EnRAGECompanionsExtension.log");
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
