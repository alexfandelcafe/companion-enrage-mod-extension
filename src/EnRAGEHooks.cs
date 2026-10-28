using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        // Keep distinct physical actions for orders: INPUT_ENTER maps to E, while the second
        // control is a different action/key (R on the default layout). Do not register two E actions
        // into the same entity prompt group: RDR2 can swallow or replace other prompts in that group.
        private const int IndividualOrderPromptControl = unchecked((int)0xCEFD9220); // E
        private const int IndividualRestPromptControl = unchecked((int)0x6D1319BE); // R
        private const int IncapacitatedPromptControl = unchecked((int)0xCEFD9220); // E while orders are hidden
        private const int CombatStancePromptControl = -2074855166; // H; medicine prompt is hidden during combat
        private static int _incapacitatedPrompt;
        private static int _incapacitatedPromptFor;
        private static int _incapacitatedPromptGroup;
        private static bool _incapacitatedPromptArmed;
        private static Ped _incapacitatedPromptPed;
        private static int _combatStancePrompt;
        private static int _combatStancePromptFor;
        private static int _combatStancePromptGroup;
        private static bool _combatStancePromptArmed;
        private static Ped _combatStancePromptPed;
        private static string _combatStancePromptLabel;
        private static int _individualOrderPrompt;
        private static int _individualOrderPromptFor;
        private static int _individualOrderPromptGroup;
        private static bool _individualOrderPromptArmed;
        private static Ped _individualOrderPromptPed;
        private static string _individualOrderPromptLabel;
        private static int _individualRestPrompt;
        private static int _individualRestPromptFor;
        private static int _individualRestPromptGroup;
        private static bool _individualRestPromptArmed;
        private static Ped _individualRestPromptPed;
        private static int _maxCompanions = 3;
        // Default affiliation is Van der Linde for compatibility. Both settings can target an
        // existing faction in enRAGE; the player-friendly relationship group is independent of it.
        private static string _recruitGangId = "vdl";
        private static string _recruitRosterId = "vdl";
        private static bool _allowFriendlyFire = true;
        private static bool _preventTargeting;
        private static bool _enableAffinity = true;
        private static int _affinityTravelIntervalSeconds = 300;
        private static int _affinityTravelGain = 1;
        private static int _affinityCombatGain = 2;
        private static int _affinityTreatmentGain = 3;
        private static bool _enableAppearancePersistence = true;
        private static bool _preserveSavedOutfit = true;
        private static bool _enableIndividualOrders = true;
        private static bool _enableWaitMode = true;
        private static float _waitRadius = 8.0f;
        private static int _restDurationSeconds = 60;
        private static bool _enableInjurySystem = true;
        private static int _injuryDamageThresholdPercent = 10;
        private static int _injuryRecoverySeconds = 180;
        private static int _incapacitatedHealthPercent = 35;
        private static int _medicineHealingPercent = 100;
        private static int _medicineRecoveryReductionSeconds = 60;
        private static bool _restHealsInjuries = true;
        private static int _recruitMedicineCostCents = 100;
        private static readonly Dictionary<string, int> LastIndividualOrderTaskAt =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastObservedHealthById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastDamageAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastHealthSampleAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastInjuryRecoveryAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastMedicineTransactionAt =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RestScenarioStartedIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastPlayerFireAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastResurrectionAttemptAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static Type _uiDrawType;
        private static MethodInfo _uiDrawRect;
        private static MethodInfo _uiDrawText;
        private sealed class AffinityTravelTracker
        {
            internal int StartedAt;
            internal Vector3 StartPosition;
        }
        private static readonly Dictionary<string, AffinityTravelTracker> AffinityTravelTrackers =
            new Dictionary<string, AffinityTravelTracker>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastAffinityCombatAt =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
        // The treatment prompt is H by default and remains separate from enRAGE's G interaction.
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
                _harmony = new Harmony(HarmonyId);
                TryPatchExtensionRecruitIntent();
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
                    MethodInfo drawRoster = FindMethod(_rosterMenuType, "Draw", 0);
                    if (drawRoster != null) _harmony.Patch(drawRoster, postfix: Hook(nameof(RosterMenuDrawPostfix)));
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

        private static void TryPatchExtensionRecruitIntent()
        {
            try
            {
                Type recordType = _mainType.Assembly.GetType("NemesisSystem.NemesisRecord", false);
                MethodInfo intentOf = recordType == null ? null : FindMethod(_mainType, "IntentOf", 1,
                    p => p[0].ParameterType == recordType);
                if (intentOf == null || !intentOf.ReturnType.IsEnum || !Enum.IsDefined(intentOf.ReturnType, "None"))
                {
                    Log("Intent override unavailable; enRAGE's own recruitment intent remains unchanged.");
                    return;
                }

                // Build a prefix with the exact runtime enum type without taking a compile-time
                // reference to NemesisMod.dll. Intent.None is enum value 3 in the inspected build.
                Type intentType = intentOf.ReturnType;
                DynamicMethod prefix = new DynamicMethod(
                    "ExtensionRecruitIntentNonePrefix",
                    typeof(bool),
                    new[] { recordType, intentType.MakeByRefType() },
                    typeof(EnRAGEHooks),
                    true);
                prefix.DefineParameter(1, ParameterAttributes.None, "__0");
                prefix.DefineParameter(2, ParameterAttributes.None, "__result");
                ILGenerator il = prefix.GetILGenerator();
                Label runOriginal = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, typeof(EnRAGEHooks).GetMethod(nameof(IsRecruitedRosterRecord), StaticFlags));
                il.Emit(OpCodes.Brfalse_S, runOriginal);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(Enum.Parse(intentType, "None"), CultureInfo.InvariantCulture));
                il.Emit(OpCodes.Stind_I4);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(runOriginal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);

                _harmony.Patch(intentOf, prefix: new HarmonyMethod(prefix));
                Log("Installed targeted Intent.None override for extension-recruited roster records.");
            }
            catch (Exception ex)
            {
                Log("Extension recruit intent override failed; neutral relationship values remain as fallback: " + Unwrap(ex));
            }
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
                MaintainAffinityTravel(__instance, player);

                // enRAGE's stock G prompt is deliberately suspended only while its current recruit
                // target is incapacitated. The roster association remains intact and the prompt is
                // rebuilt by enRAGE after the companion gets up.
                Ped stockAskTarget = GetField(__instance, "_askPed") as Ped;
                if (stockAskTarget != null && stockAskTarget.Exists() && IsPedIncapacitatedRecruit(stockAskTarget))
                    InvokeInstance(__instance, "HideAskPrompt");
                else
                    InvokeInstance(__instance, "UpdateAsk", player);

                UpdateGenericRecruitPrompt(__instance, player);
                UpdateIncapacitatedPrompt(__instance, player);
                UpdateCombatStancePrompt(__instance, player);
                UpdateRecruitMedicinePrompt(__instance, player);
                UpdateIndividualOrderPrompts(__instance, player);
                UpdateIndividualOrderPrompts(__instance, player);

                IList list = GetField(__instance, "_comps") as IList;
                if (list == null || list.Count == 0)
                    return;

                // Iterate over a snapshot: Ride/GoingHome/Cleanup can remove entries from _comps.
                object[] snapshot = list.Cast<object>().ToArray();
                foreach (object companion in snapshot)
                {
                    if (companion == null)
                        continue;

                    // Inspect health/down state before the normal death marker. In enRAGE,
                    // IsDead can coexist with IsIncapacitated; that combination is not a confirmed death.
                    if (MaintainCompanionCondition(__instance, companion, player))
                        continue;
                    MarkRecruitedCompanionDeath(__instance, companion);
                    // Persistent individual orders must take precedence over enRAGE's default
                    // WaitToJoin/Ride tasks, otherwise those tasks overwrite the hold/rest task.
                    if (MaintainIndividualCompanionOrder(__instance, companion, player))
                        continue;
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
                // Released recruits remain eligible for a direct "call back" prompt.
                _preferredGenericPed = target;
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

        private static void RosterMenuDrawPostfix(object __instance)
        {
            try
            {
                if (!_enableAffinity || __instance == null) return;
                object record = GetField(__instance, "_detail");
                if (record == null || !IsRecruitedRosterRecord(record)) return;
                CompanionPersistentState state = GetPersistentState(record, true);
                if (state == null) return;

                if (_uiDrawType == null && _mainType != null)
                    _uiDrawType = _mainType.Assembly.GetTypes().FirstOrDefault(t => t.Name == "UiDraw");
                if (_uiDrawType == null) return;

                if (_uiDrawRect == null)
                    _uiDrawRect = FindMethod(_uiDrawType, "Rect", 8, p =>
                        p[0].ParameterType == typeof(float) && p[1].ParameterType == typeof(float) &&
                        p[2].ParameterType == typeof(float) && p[3].ParameterType == typeof(float) &&
                        p.Skip(4).All(x => x.ParameterType == typeof(int)));
                if (_uiDrawText == null)
                    _uiDrawText = FindMethod(_uiDrawType, "Text", 8, p =>
                        p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(float) &&
                        p[2].ParameterType == typeof(float) && p[3].ParameterType == typeof(float) &&
                        p.Skip(4).All(x => x.ParameterType == typeof(int)));
                if (_uiDrawRect == null || _uiDrawText == null) return;

                int affinity = Math.Max(0, Math.Min(100, state.Affinity));
                // The roster sheet has two columns: POSICIÓN/SENTIMIENTOS is on the left (x=.09),
                // while NATURALEZA/INTENCIÓN/HISTORIAL is on the right (x=.52). Stay in the left
                // feelings header band; drawing at x>=.52 places the bar over history/intention.
                float x = 0.315f;
                float width = 0.155f;
                _uiDrawText.Invoke(null, new object[] { "AFINIDAD " + affinity + "/100", x, 0.345f, 0.16f, 235, 230, 220, 255 });
                _uiDrawRect.Invoke(null, new object[] { x, 0.366f, width, 0.0055f, 26, 24, 20, 220 });
                if (affinity > 0)
                    _uiDrawRect.Invoke(null, new object[] { x, 0.366f, width * affinity / 100f, 0.0055f, 189, 153, 83, 245 });
            }
            catch (Exception ex) { Log("Affinity meter draw warning: " + Unwrap(ex)); }
        }

        private static CompanionPersistentState GetPersistentState(object record, bool create)
        {
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return null;
            CompanionStateStore store = CompanionStateRuntime.Store;
            if (store == null) return null;
            CompanionPersistentState state;
            if (store.TryGet(id, out state)) return state;
            if (!create) return null;
            state = store.GetOrCreate(id);
            if (store.IsDirty) store.SaveIfDirty();
            return state;
        }

        private static void SavePersistentState(CompanionPersistentState state)
        {
            if (state == null) return;
            CompanionStateStore store = CompanionStateRuntime.Store;
            if (store == null) return;
            try
            {
                store.MarkChanged(state);
                store.SaveIfDirty();
            }
            catch (Exception ex) { Log("Companion state save warning: " + Unwrap(ex)); }
        }

        private static object FindActiveExtensionCompanion(object instance, string recordId)
        {
            if (instance == null || string.IsNullOrWhiteSpace(recordId)) return null;
            IList companions = GetField(instance, "_comps") as IList;
            if (companions == null) return null;

            foreach (object companion in companions)
            {
                string key = GetField(companion, "Key") as string;
                if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    continue;
                object record = GetField(companion, "Rec");
                string currentId = GetMember(record, "Id") as string;
                if (string.Equals(currentId, recordId, StringComparison.OrdinalIgnoreCase))
                    return companion;
                string stableKey = KeyPrefix + "roster_" + recordId;
                if (string.Equals(key, stableKey, StringComparison.OrdinalIgnoreCase))
                    return companion;
            }
            return null;
        }

        private static void SetPersistentFollowState(object record, CompanionFollowState followState, Ped anchorPed)
        {
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null) return;

            state.FollowState = followState;
            if (followState == CompanionFollowState.Waiting || followState == CompanionFollowState.Resting)
            {
                Ped anchor = anchorPed;
                if (anchor == null || !anchor.Exists())
                    anchor = Game.Player.Character;
                if (anchor != null && anchor.Exists())
                {
                    var position = anchor.Position;
                    state.SavedPosition.HasValue = true;
                    state.SavedPosition.X = position.X;
                    state.SavedPosition.Y = position.Y;
                    state.SavedPosition.Z = position.Z;
                    try { state.SavedPosition.Heading = anchor.Heading; } catch { state.SavedPosition.Heading = 0f; }
                }
                if (followState == CompanionFollowState.Resting)
                    state.RestStartedUtcTicks = DateTime.UtcNow.Ticks;
                else
                    state.RestStartedUtcTicks = 0;
            }
            else
            {
                if (state.SavedPosition != null) state.SavedPosition.HasValue = false;
                state.RestStartedUtcTicks = 0;
            }
            SavePersistentState(state);
        }

        private static void SetIndividualCompanionOrder(object instance, object record, CompanionFollowState order)
        {
            if (instance == null || record == null || !_enableIndividualOrders) return;
            if (order == CompanionFollowState.Waiting && !_enableWaitMode)
            {
                InvokeInstance(instance, "ShowSubtitle", "El modo de espera está desactivado en el archivo INI.");
                return;
            }

            string id = GetMember(record, "Id") as string;
            object companion = FindActiveExtensionCompanion(instance, id);
            if (companion == null)
            {
                InvokeInstance(instance, "ShowSubtitle", "Primero debes llamar a este compañero desde el roster.");
                return;
            }

            Ped ped = GetField(companion, "Ped") as Ped;
            if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
            if (ped == null || !ped.Exists() || ped.IsDead)
            {
                InvokeInstance(instance, "ShowSubtitle", "No se pudo localizar al compañero para aplicar la orden.");
                return;
            }

            SetPersistentFollowState(record, order, ped);
            LastIndividualOrderTaskAt.Remove(id ?? string.Empty);
            if (order == CompanionFollowState.Following)
            {
                RestScenarioStartedIds.Remove(id ?? string.Empty);
                try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                SetField(companion, "State", 0);
                SetField(companion, "AskedAt", Game.GameTime);
                InvokeInstance(instance, "ShowSubtitle", (GetMember(record, "Name") as string ?? "Tu compañero") + " vuelve a seguirte.");
            }
            else
            {
                RestScenarioStartedIds.Remove(id ?? string.Empty);
                try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                if (ped.IsOnMount)
                {
                    try { TASK.TASK_DISMOUNT_ANIMAL(ped.Handle, 0, 0UL, 0UL, 0UL, 0); } catch { }
                }
                // Do not reset enRAGE's State/AskedAt for Waiting or Resting: that state is still
                // needed by its G prompt. Our order controller suppresses the movement task itself.
                InvokeInstance(instance, "ShowSubtitle", order == CompanionFollowState.Waiting
                    ? (GetMember(record, "Name") as string ?? "Tu compañero") + " esperará aquí."
                    : (GetMember(record, "Name") as string ?? "Tu compañero") + " descansará durante " + _restDurationSeconds + " segundos.");
            }
        }

        private static bool MaintainIndividualCompanionOrder(object instance, object companion, Ped player)
        {
            if (!_enableIndividualOrders || companion == null) return false;
            string key = GetField(companion, "Key") as string;
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                return false;

            object record = GetField(companion, "Rec");
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return false;
            CompanionPersistentState state = GetPersistentState(record, false);
            if (state == null) return false;

            if (state.FollowState == CompanionFollowState.Waiting && !_enableWaitMode)
            {
                state.FollowState = CompanionFollowState.Following;
                if (state.SavedPosition != null) state.SavedPosition.HasValue = false;
                SavePersistentState(state);
                return false;
            }

            if (state.FollowState != CompanionFollowState.Waiting && state.FollowState != CompanionFollowState.Resting)
                return false;

            Ped ped = GetField(companion, "Ped") as Ped;
            if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
            if (ped == null || !ped.Exists() || ped.IsDead)
                return true; // Do not let enRAGE's stock task run over a persisted hold/rest order.

            if (state.FollowState == CompanionFollowState.Resting)
            {
                if (state.RestStartedUtcTicks <= 0)
                {
                    state.RestStartedUtcTicks = DateTime.UtcNow.Ticks;
                    SavePersistentState(state);
                }
                double elapsed = (DateTime.UtcNow.Ticks - state.RestStartedUtcTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsed >= _restDurationSeconds)
                {
                    state.FollowState = CompanionFollowState.Following;
                    state.RestStartedUtcTicks = 0;
                    if (state.SavedPosition != null) state.SavedPosition.HasValue = false;
                    if (_restHealsInjuries && state.Injury != null)
                    {
                        state.Injury.IsInjured = false;
                        state.Injury.Severity = 0;
                        state.Injury.RecoverySecondsRemaining = 0;
                        state.Injury.FirstInjuredUtcTicks = 0;
                    }
                    SavePersistentState(state);
                    if (_restHealsInjuries) HealPedToMaximum(ped);
                    RestScenarioStartedIds.Remove(id);
                    LastIndividualOrderTaskAt.Remove(id);
                    SetField(companion, "State", 0);
                    SetField(companion, "AskedAt", Game.GameTime);
                    try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                    InvokeInstance(instance, "ShowSubtitle", (GetMember(record, "Name") as string ?? "Tu compañero") + " terminó de descansar y vuelve a seguirte.");
                    return false;
                }
            }

            MaintainAnchoredCompanionTask(id, ped, state);
            return true;
        }

        private static void MaintainAnchoredCompanionTask(string id, Ped ped, CompanionPersistentState state)
        {
            if (ped == null || !ped.Exists() || state == null) return;
            if (state.SavedPosition == null) state.SavedPosition = new CompanionSavedPosition();
            if (!state.SavedPosition.HasValue)
            {
                var position = ped.Position;
                state.SavedPosition.HasValue = true;
                state.SavedPosition.X = position.X;
                state.SavedPosition.Y = position.Y;
                state.SavedPosition.Z = position.Z;
                try { state.SavedPosition.Heading = ped.Heading; } catch { state.SavedPosition.Heading = 0f; }
                SavePersistentState(state);
            }

            int now = Game.GameTime;
            int last;
            if (LastIndividualOrderTaskAt.TryGetValue(id, out last) && now >= last && now - last < 1400)
                return;
            LastIndividualOrderTaskAt[id] = now;

            if (ped.IsOnMount)
            {
                RestScenarioStartedIds.Remove(id);
                try { TASK.TASK_DISMOUNT_ANIMAL(ped.Handle, 0, 0UL, 0UL, 0UL, 0); } catch { }
                return;
            }

            float dx = ped.Position.X - state.SavedPosition.X;
            float dy = ped.Position.Y - state.SavedPosition.Y;
            float dz = ped.Position.Z - state.SavedPosition.Z;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > _waitRadius)
            {
                RestScenarioStartedIds.Remove(id);
                if (!TryInvokeTaskNative("TASK_GO_STRAIGHT_TO_COORD", new object[] {
                    ped.Handle, state.SavedPosition.X, state.SavedPosition.Y, state.SavedPosition.Z,
                    1.0f, 10000, state.SavedPosition.Heading, 1.0f }))
                    Log("The TASK_GO_STRAIGHT_TO_COORD wrapper was unavailable; held companion " + id + " could not return to its saved position.");
            }
            else if (state.FollowState == CompanionFollowState.Resting)
            {
                // Start a genuine in-world rest scenario once. Reissuing STAND_STILL every tick
                // cancels scenario tasks, which made Descansar look as though it did nothing.
                if (!RestScenarioStartedIds.Contains(id))
                {
                    if (TryStartCompanionRestScenario(ped))
                        RestScenarioStartedIds.Add(id);
                    else
                    {
                        Log("No compatible resting scenario wrapper was found for " + id + "; using a passive wait task.");
                        if (!TryInvokeTaskNative("TASK_STAND_STILL", new object[] { ped.Handle, -1 }))
                            TryInvokeTaskNative("TASK_PAUSE", new object[] { ped.Handle, -1 });
                    }
                }
            }
            else
            {
                RestScenarioStartedIds.Remove(id);
                if (!TryInvokeTaskNative("TASK_STAND_STILL", new object[] { ped.Handle, -1 }))
                    TryInvokeTaskNative("TASK_PAUSE", new object[] { ped.Handle, -1 });
            }
        }

        private static bool TryStartCompanionRestScenario(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead) return false;
            // ScriptHook API versions differ on optional scenario parameters. Try known wrapper
            // signatures reflectively; unsupported APIs degrade to a passive wait, not recruitment loss.
            object[] fourArgs = new object[] { ped.Handle, "WORLD_HUMAN_SIT_GROUND", -1, true };
            if (TryInvokeNativeWrapper(typeof(TASK), "TASK_START_SCENARIO_IN_PLACE", fourArgs))
                return true;
            object[] threeArgs = new object[] { ped.Handle, "WORLD_HUMAN_SIT_GROUND", -1 };
            if (TryInvokeNativeWrapper(typeof(TASK), "TASK_START_SCENARIO_IN_PLACE", threeArgs))
                return true;
            return false;
        }

        private static bool TryInvokeNativeWrapper(Type nativeType, string methodName, object[] args)
        {
            try
            {
                if (nativeType == null) return false;
                MethodInfo method = nativeType.GetMethods(StaticFlags).FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase) &&
                    candidate.GetParameters().Length == args.Length);
                if (method == null) return false;

                ParameterInfo[] parameters = method.GetParameters();
                object[] converted = new object[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    object value = args[i];
                    Type targetType = parameters[i].ParameterType;
                    if (value == null || targetType.IsInstanceOfType(value))
                        converted[i] = value;
                    else if (targetType.IsEnum)
                        converted[i] = Enum.ToObject(targetType, value);
                    else
                        converted[i] = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                }
                method.Invoke(null, converted);
                return true;
            }
            catch (Exception ex)
            {
                Log("Native wrapper invocation failed for " + methodName + ": " + Unwrap(ex));
                return false;
            }
        }

        private static bool TryInvokeTaskNative(string methodName, object[] args)
        {
            try
            {
                MethodInfo method = typeof(TASK).GetMethods(StaticFlags).FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase) &&
                    candidate.GetParameters().Length == args.Length);
                if (method == null) return false;

                ParameterInfo[] parameters = method.GetParameters();
                object[] converted = new object[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    object value = args[i];
                    Type targetType = parameters[i].ParameterType;
                    if (value == null || targetType.IsInstanceOfType(value))
                        converted[i] = value;
                    else if (targetType.IsEnum)
                        converted[i] = Enum.ToObject(targetType, value);
                    else
                        converted[i] = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                }
                method.Invoke(null, converted);
                return true;
            }
            catch (Exception ex)
            {
                Log("Task invocation failed for " + methodName + ": " + Unwrap(ex));
                return false;
            }
        }

        private static int ChangeAffinity(object record, int delta)
        {
            if (!_enableAffinity) return -1;
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null) return -1;
            state.Affinity = Math.Max(0, Math.Min(100, state.Affinity + delta));
            SavePersistentState(state);
            return state.Affinity;
        }

        private static void AwardAffinity(object record, int gain, string reason)
        {
            if (!_enableAffinity || gain <= 0 || record == null) return;
            int affinity = ChangeAffinity(record, gain);
            if (affinity >= 0)
                Log("Affinity +" + gain + " (" + reason + ") for " +
                    ((GetMember(record, "Name") as string) ?? (GetMember(record, "Id") as string)) + "; now " + affinity + "/100.");
        }

        private static void MaintainAffinityTravel(object instance, Ped player)
        {
            if (!_enableAffinity || _affinityTravelGain <= 0 || _affinityTravelIntervalSeconds <= 0 ||
                instance == null || player == null || !player.Exists()) return;

            int now = Game.GameTime;
            IList companions = GetField(instance, "_comps") as IList;
            if (companions == null) return;
            HashSet<string> activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (object companion in companions.Cast<object>().ToArray())
            {
                string key = GetField(companion, "Key") as string;
                if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal) ||
                    GetFieldInt(companion, "State", 2) == 2) continue;

                object record = GetField(companion, "Rec");
                string id = GetMember(record, "Id") as string;
                if (string.IsNullOrWhiteSpace(id)) continue;

                Ped ped = GetField(companion, "Ped") as Ped;
                if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
                if (ped == null || !ped.Exists() || ped.IsDead || ped.Position.DistanceTo(player.Position) > 60f)
                {
                    AffinityTravelTrackers.Remove(id);
                    continue;
                }

                activeIds.Add(id);
                AffinityTravelTracker tracker;
                if (!AffinityTravelTrackers.TryGetValue(id, out tracker))
                {
                    AffinityTravelTrackers[id] = new AffinityTravelTracker { StartedAt = now, StartPosition = player.Position };
                    continue;
                }

                if (now < tracker.StartedAt || now - tracker.StartedAt < _affinityTravelIntervalSeconds * 1000)
                    continue;

                bool travelled = player.Position.DistanceTo(tracker.StartPosition) >= 100f;
                tracker.StartedAt = now;
                tracker.StartPosition = player.Position;
                if (travelled) AwardAffinity(record, _affinityTravelGain, "travel");
            }

            foreach (string id in AffinityTravelTrackers.Keys.ToArray())
                if (!activeIds.Contains(id)) AffinityTravelTrackers.Remove(id);
        }

        private static void AdjustAffinityFromRoster(object instance, object record, int delta)
        {
            int affinity = ChangeAffinity(record, delta);
            if (affinity < 0)
            {
                InvokeInstance(instance, "ShowSubtitle", "La afinidad está desactivada en el archivo INI.");
                return;
            }
            string name = GetMember(record, "Name") as string ?? "Tu compañero";
            InvokeInstance(instance, "ShowSubtitle", name + ": afinidad " + affinity + "/100.");
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
                    __result.RemoveAll(action => action != null &&
                        action.IndexOf("afinidad", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (_enableAffinity)
                    {
                        InsertBeforeBack(__result, "Mejorar afinidad (+10)");
                        InsertBeforeBack(__result, "Reducir afinidad (-10)");
                    }

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
                if (action == "Mejorar afinidad (+10)" || action == "Reducir afinidad (-10)")
                {
                    AdjustAffinityFromRoster(_latestMainInstance, record, action == "Mejorar afinidad (+10)" ? 10 : -10);
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
            if (cash < _recruitMedicineCostCents)
            {
                InvokeInstance(instance, "ShowSubtitle", "Necesitas " +
                    (_recruitMedicineCostCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                    " dólares para comprar el remedio.");
                return;
            }

            int now = Game.GameTime;
            int lastTransaction;
            if (LastMedicineTransactionAt.TryGetValue(id, out lastTransaction) &&
                now >= lastTransaction && now - lastTransaction < 2000)
            {
                InvokeInstance(instance, "ShowSubtitle", "El remedio ya se está procesando; no se cobró otra vez.");
                return;
            }
            // Claim this record before debiting cash. If a duplicate prompt fires while the native
            // call completes, it cannot charge twice during the same transaction window.
            LastMedicineTransactionAt[id] = now;
            try { if (_recruitMedicineCostCents > 0) MONEY._MONEY_DECREMENT_CASH_BALANCE(_recruitMedicineCostCents); }
            catch (Exception ex)
            {
                Log("Medicine payment failed: " + Unwrap(ex));
                InvokeInstance(instance, "ShowSubtitle", "No se pudo confirmar el cobro; espera un momento antes de volver a intentarlo.");
                return;
            }

            ApplyConfiguredMedicineHealing(ped);
            CompanionPersistentState injuryState = GetPersistentState(record, true);
            if (injuryState != null && injuryState.Injury != null && injuryState.Injury.IsInjured)
            {
                injuryState.Injury.RecoverySecondsRemaining = Math.Max(0,
                    injuryState.Injury.RecoverySecondsRemaining - _medicineRecoveryReductionSeconds);
                if (injuryState.Injury.RecoverySecondsRemaining <= 0 && !injuryState.Injury.IsIncapacitated)
                {
                    injuryState.Injury.IsInjured = false;
                    injuryState.Injury.Severity = 0;
                    injuryState.Injury.FirstInjuredUtcTicks = 0;
                }
                SavePersistentState(injuryState);
            }
            AwardAffinity(record, _affinityTreatmentGain, "treatment");
            PendingFullHealthIds.Remove(id);
            SavePersistentIdSet("EnRAGECompanionsExtension.pending-heals.txt", PendingFullHealthIds);
            InvokeInstance(instance, "ShowSubtitle", name + " recibió el remedio. Curación: " +
                _medicineHealingPercent + "%. Coste: " +
                (_recruitMedicineCostCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " dólares.");
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

        private static void ApplyConfiguredMedicineHealing(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead) return;
            int maxHealth = Math.Max(1, ped.MaxHealth);
            int amount = Math.Max(1, (int)Math.Ceiling(maxHealth * _medicineHealingPercent / 100.0));
            ped.Health = Math.Min(maxHealth, Math.Max(1, ped.Health) + amount);
        }

        private static void HealPedToMaximum(Ped ped)
        {
            if (ped == null || !ped.Exists() || (ped.IsDead && !ped.IsIncapacitated)) return;
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
            if (!_enableAppearancePersistence || record == null || ped == null || !ped.Exists() || ped.IsDead) return;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return;

            uint savedHash;
            if (_preserveSavedOutfit && TryGetSavedRecruitOutfitHash(record, out savedHash) && savedHash != 0U)
            {
                ApplySavedRecruitOutfit(record, ped);
                return;
            }
            CaptureRecruitOutfitIfMissing(record, ped);
        }

        private static void CaptureRecruitOutfitIfMissing(object record, Ped ped, bool overwriteExisting = false)
        {
            if (!_enableAppearancePersistence || record == null || ped == null || !ped.Exists() || ped.IsDead) return;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return;
            uint existing;
            if (!overwriteExisting && TryGetSavedRecruitOutfitHash(record, out existing) && existing != 0U)
            {
                // The full outfit hash may exist while individual variation slots were not captured
                // by an older build. Capture those standard native component slots additively.
                CaptureRecruitClothingComponents(record, ped, false);
                return;
            }

            uint outfitHash;
            bool hasOutfitHash = TryGetMetaPedOutfitHash(ped, out outfitHash) && outfitHash != 0U;
            if (!hasOutfitHash)
            {
                CaptureRecruitClothingComponents(record, ped, overwriteExisting);
                if (OutfitCaptureFailureLogged.Add(id))
                    Log("Could not capture the current meta-ped outfit hash for record " + id +
                        "; individual component variations were still captured when the installed API exposed them.");
                return;
            }

            OutfitCaptureFailureLogged.Remove(id);
            RecruitedOutfitHashes[id] = outfitHash;
            CompanionPersistentState savedState = GetPersistentState(record, true);
            if (savedState != null)
            {
                if (savedState.Appearance == null) savedState.Appearance = new CompanionAppearanceState();
                savedState.Appearance.HasLegacyOutfitHash = true;
                savedState.Appearance.LegacyOutfitHash = outfitHash;
                SavePersistentState(savedState);
            }
            CaptureRecruitClothingComponents(record, ped, overwriteExisting);
            SavePersistentRecruitOutfits();
            Log("Saved meta-ped outfit 0x" + outfitHash.ToString("X8", CultureInfo.InvariantCulture) +
                " and available component variations for " + (GetMember(record, "Name") as string ?? id) +
                " (record=" + id + ").");
        }

        private static bool TryGetSavedRecruitOutfitHash(object record, out uint outfitHash)
        {
            outfitHash = 0U;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (RecruitedOutfitHashes.TryGetValue(id, out outfitHash) && outfitHash != 0U)
                return true;

            CompanionPersistentState state = GetPersistentState(record, false);
            if (state == null || state.Appearance == null || !state.Appearance.HasLegacyOutfitHash ||
                state.Appearance.LegacyOutfitHash == 0U)
                return false;
            outfitHash = state.Appearance.LegacyOutfitHash;
            RecruitedOutfitHashes[id] = outfitHash;
            return true;
        }

        private static void ApplySavedRecruitOutfit(object record, Ped ped)
        {
            if (!_enableAppearancePersistence || !_preserveSavedOutfit || record == null || ped == null || !ped.Exists() || ped.IsDead) return;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return;

            try
            {
                uint outfitHash;
                bool hasHash = TryGetSavedRecruitOutfitHash(record, out outfitHash) && outfitHash != 0U;
                if (hasHash && !TryEquipMetaPedOutfitHash(ped, outfitHash))
                    Log("Could not restore saved meta-ped outfit for record " + id + "; compatible outfit native was unavailable.");
                bool componentsApplied = ApplySavedRecruitClothingComponents(record, ped);
                if (hasHash)
                    Log("Restored meta-ped outfit 0x" + outfitHash.ToString("X8", CultureInfo.InvariantCulture) + " for " +
                        (GetMember(record, "Name") as string ?? id) + " (ped=" + ped.Handle + ").");
                else if (!componentsApplied)
                    return;

                // If a hash is present but no slots were saved by an earlier revision, capture the
                // visible components after the outfit has been applied so future recalls keep them.
                CompanionPersistentState appearanceState = GetPersistentState(record, false);
                if (appearanceState == null || appearanceState.Appearance == null ||
                    appearanceState.Appearance.ClothingComponents == null || appearanceState.Appearance.ClothingComponents.Count == 0)
                    CaptureRecruitClothingComponents(record, ped, false);
            }
            catch (Exception ex) { Log("Recruit outfit restore warning for " + id + ": " + Unwrap(ex)); }
        }

        private static void CaptureRecruitClothingComponents(object record, Ped ped, bool overwriteExisting)
        {
            if (!_enableAppearancePersistence || record == null || ped == null || !ped.Exists() || ped.IsDead)
                return;
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null) return;
            if (state.Appearance == null) state.Appearance = new CompanionAppearanceState();
            if (!overwriteExisting && state.Appearance.ClothingComponents != null &&
                state.Appearance.ClothingComponents.Count > 0)
                return;

            List<CompanionClothingComponent> snapshot = new List<CompanionClothingComponent>();
            for (int component = 0; component < 16; component++)
            {
                int drawable, texture, palette;
                if (!TryGetPedComponentVariation(ped, component, out drawable, out texture, out palette))
                    continue;
                if (drawable < 0 || texture < 0 || palette < 0) continue;
                snapshot.Add(new CompanionClothingComponent
                {
                    ComponentId = component,
                    DrawableHash = unchecked((uint)drawable),
                    TextureHash = unchecked((uint)texture),
                    PaletteHash = unchecked((uint)palette)
                });
            }

            if (snapshot.Count == 0) return;
            state.Appearance.ClothingComponents = snapshot;
            SavePersistentState(state);
            Log("Saved " + snapshot.Count + " native clothing component variation(s) for " +
                (GetMember(record, "Name") as string ?? GetMember(record, "Id") as string) + ".");
        }

        private static bool TryGetPedComponentVariation(Ped ped, int componentId,
            out int drawable, out int texture, out int palette)
        {
            drawable = texture = palette = -1;
            if (ped == null || !ped.Exists()) return false;
            MethodInfo getDrawable = FindPedNative(new[] { "GET_PED_DRAWABLE_VARIATION" }, 2,
                p => IsNativeInteger(p[0].ParameterType) && IsNativeInteger(p[1].ParameterType));
            MethodInfo getTexture = FindPedNative(new[] { "GET_PED_TEXTURE_VARIATION" }, 2,
                p => IsNativeInteger(p[0].ParameterType) && IsNativeInteger(p[1].ParameterType));
            MethodInfo getPalette = FindPedNative(new[] { "GET_PED_PALETTE_VARIATION" }, 2,
                p => IsNativeInteger(p[0].ParameterType) && IsNativeInteger(p[1].ParameterType));
            if (getDrawable == null || getTexture == null || getPalette == null) return false;

            try
            {
                drawable = ReadPedComponentIndex(getDrawable, ped.Handle, componentId);
                texture = ReadPedComponentIndex(getTexture, ped.Handle, componentId);
                palette = ReadPedComponentIndex(getPalette, ped.Handle, componentId);
                return drawable >= 0 && texture >= 0 && palette >= 0;
            }
            catch (Exception ex)
            {
                Log("Ped component variation read failed for slot " + componentId + ": " + Unwrap(ex));
                return false;
            }
        }

        private static int ReadPedComponentIndex(MethodInfo getter, int pedHandle, int componentId)
        {
            ParameterInfo[] parameters = getter.GetParameters();
            object result = getter.Invoke(null, new[]
            {
                ConvertNativeInteger(pedHandle, parameters[0].ParameterType),
                ConvertNativeInteger(componentId, parameters[1].ParameterType)
            });
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }

        private static bool ApplySavedRecruitClothingComponents(object record, Ped ped)
        {
            if (!_enableAppearancePersistence || record == null || ped == null || !ped.Exists()) return false;
            CompanionPersistentState state = GetPersistentState(record, false);
            List<CompanionClothingComponent> components = state == null || state.Appearance == null
                ? null : state.Appearance.ClothingComponents;
            if (components == null || components.Count == 0) return false;

            MethodInfo setter = FindPedNative(new[] { "SET_PED_COMPONENT_VARIATION" }, 5,
                p => p.All(parameter => IsNativeInteger(parameter.ParameterType)));
            if (setter == null)
            {
                Log("ScriptHook wrapper for SET_PED_COMPONENT_VARIATION unavailable; saved per-slot outfit data was retained but not applied.");
                return false;
            }

            int applied = 0;
            try
            {
                ParameterInfo[] parameters = setter.GetParameters();
                foreach (CompanionClothingComponent component in components)
                {
                    if (component == null || component.ComponentId < 0 || component.ComponentId > 15) continue;
                    setter.Invoke(null, new object[]
                    {
                        ConvertNativeInteger(ped.Handle, parameters[0].ParameterType),
                        ConvertNativeInteger(component.ComponentId, parameters[1].ParameterType),
                        ConvertNativeInteger(component.DrawableHash, parameters[2].ParameterType),
                        ConvertNativeInteger(component.TextureHash, parameters[3].ParameterType),
                        ConvertNativeInteger(component.PaletteHash, parameters[4].ParameterType)
                    });
                    applied++;
                }

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
                return applied > 0;
            }
            catch (Exception ex)
            {
                Log("Applying saved ped component variations failed: " + Unwrap(ex));
                return applied > 0;
            }
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
                    // Capture/restore appearance before adopting the same entity; no clone or second
                    // roster record should be created for a dismissed recruit.
                    PreserveRecruitOutfit(record, existingPed);
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

                // Do not issue TASK_MOUNT_ANIMAL here. It interrupts enRAGE's WaitToJoin/Ride
                // follow-up task, leaving a recalled NPC mounted but idle. The normal companion
                // cycle owns mounting and movement after Adopt/SpawnCompanion.
                ApplyMaximumRecruitFriendship(record);
                SetPersistentFollowState(record, CompanionFollowState.Following, null);
                object activeCompanion = FindActiveExtensionCompanion(instance, id);
                // An adopted live pedestrian must re-enter WaitToJoin; when SpawnCompanion created
                // a replacement, preserve enRAGE's own initial state (normally Ride/state 1).
                if (activeCompanion != null && existingPed != null)
                {
                    SetField(activeCompanion, "State", 0);
                    SetField(activeCompanion, "AskedAt", Game.GameTime);
                }
                try { InvokeInstance(instance, "SaveRoster"); } catch { }
                InvokeInstance(instance, "ShowSubtitle", existingPed != null
                    ? name + " ha vuelto y vuelve a seguirte."
                    : name + " fue llamado y se acerca para seguirte.");
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

        private static bool NormalizeExtensionRecruitHistory(object record)
        {
            if (record == null) return false;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id) ||
                !(id.StartsWith("extcomp_", StringComparison.OrdinalIgnoreCase) ||
                  id.StartsWith("extvdl_", StringComparison.OrdinalIgnoreCase))) return false;

            int kills = 0;
            try { kills = Convert.ToInt32(GetMember(record, "TimesKilledPlayer"), CultureInfo.InvariantCulture); } catch { }
            // Only repair the default/fabricated kill marker when the actual kill counter is zero.
            // Records with a genuine recorded player kill are left untouched.
            if (kills != 0) return false;

            bool changed = false;
            object origin = GetMember(record, "Origin");
            if (!string.Equals(origin == null ? string.Empty : origin.ToString(), "GangMember", StringComparison.OrdinalIgnoreCase))
                changed |= SetEnumMember(record, "Origin", "GangMember");
            string location = GetMember(record, "KillLocation") as string;
            if (!string.IsNullOrEmpty(location)) changed |= SetMember(record, "KillLocation", string.Empty);
            object killedAt = GetMember(record, "KilledPlayerAt");
            if (killedAt is DateTime timestamp && timestamp != DateTime.MinValue)
                changed |= SetMember(record, "KilledPlayerAt", DateTime.MinValue);
            if (GetMember(record, "KilledByPlayer") is bool killedByPlayer && killedByPlayer)
                changed |= SetMember(record, "KilledByPlayer", false);
            return changed;
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
                changed |= NormalizeExtensionRecruitHistory(record);
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
                    changed |= NormalizeExtensionRecruitHistory(record);
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
                bool hasPreferredTarget = _preferredGenericPed != null && _preferredGenericPed.Exists();
                if (hasPreferredTarget)
                {
                    if (IsCachedRecruitedPed(_preferredGenericPed.Handle))
                    {
                        if (IsEligibleReleasedRecruitForRecall(instance, player, _preferredGenericPed))
                            candidate = _preferredGenericPed;
                    }
                    else if (IsEligibleGenericCandidate(instance, player, _preferredGenericPed, true))
                    {
                        candidate = _preferredGenericPed;
                    }
                }
                _preferredGenericPed = null;
                if (candidate == null && !hasPreferredTarget)
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
                    string promptLabel = IsCachedRecruitedPed(candidate.Handle) ? "Llamarlo" : "Cabalga conmigo";
                    object result = _makePrompt.Invoke(null, new object[] { control, promptLabel, true, group });
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
                CachedRosterRecord recalledRecord;
                if (RosterRecordsByPedHandle.TryGetValue(candidate.Handle, out recalledRecord) && recalledRecord != null && recalledRecord.Record != null)
                {
                    string recalledId = GetMember(recalledRecord.Record, "Id") as string;
                    if (!string.IsNullOrWhiteSpace(recalledId)) key = KeyPrefix + "roster_" + recalledId;
                }
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

        // While aiming at an active extension companion, expose the individual orders as world
        // prompts. These are deliberately separate from the roster sheet and from Dejar de seguirme.
        private static void UpdateIndividualOrderPrompts(object instance, Ped player)
        {
            try
            {
                if (!_enableIndividualOrders || player == null || !player.Exists() || player.IsInCombat)
                {
                    HideIndividualOrderPrompts();
                    return;
                }

                Ped target = FindAimedRecruitedPed(instance, player);
                CachedRosterRecord cached;
                if (target == null || !target.Exists() ||
                    !RosterRecordsByPedHandle.TryGetValue(target.Handle, out cached) || cached == null ||
                    cached.Record == null || !IsRecruitedRosterRecord(cached.Record) ||
                    IsExiledRosterRecord(cached.Record) || !IsRosterRecordCurrentlyFollowing(instance, cached.Record))
                {
                    HideIndividualOrderPrompts();
                    return;
                }

                CompanionPersistentState state = GetPersistentState(cached.Record, true);
                if (IsPedIncapacitatedRecruit(target) ||
                    (state != null && state.Injury != null && state.Injury.IsIncapacitated))
                {
                    HideIndividualOrderPrompts();
                    return;
                }
                CompanionFollowState current = state == null ? CompanionFollowState.Following : state.FollowState;
                CompanionFollowState primaryOrder = current == CompanionFollowState.Following
                    ? CompanionFollowState.Waiting : CompanionFollowState.Following;
                string primaryLabel = primaryOrder == CompanionFollowState.Waiting ? "Quédate aquí" : "Sígueme";
                int group = HUD._UI_PROMPT_GET_GROUP_ID_FOR_TARGET_ENTITY(target.Handle);
                UpdateIndividualOrderPrompt(instance, target, group, primaryLabel, IndividualOrderPromptControl,
                    primaryOrder, true, cached.Record);

                if (current == CompanionFollowState.Resting)
                    HideIndividualOrderPrompt(false);
                else
                    UpdateIndividualOrderPrompt(instance, target, group, "Descansar", IndividualRestPromptControl,
                        CompanionFollowState.Resting, false, cached.Record);
            }
            catch (Exception ex)
            {
                HideIndividualOrderPrompts();
                Log("Individual order prompt failed: " + Unwrap(ex));
            }
        }

        private static void UpdateIndividualOrderPrompt(object instance, Ped target, int group, string label,
            int control, CompanionFollowState order, bool primary, object record)
        {
            int prompt = primary ? _individualOrderPrompt : _individualRestPrompt;
            int promptFor = primary ? _individualOrderPromptFor : _individualRestPromptFor;
            int promptGroup = primary ? _individualOrderPromptGroup : _individualRestPromptGroup;
            bool armed = primary ? _individualOrderPromptArmed : _individualRestPromptArmed;
            Ped promptPed = primary ? _individualOrderPromptPed : _individualRestPromptPed;

            if (prompt == 0 || promptFor != target.Handle || promptGroup != group || !IsPromptValid(prompt) ||
                (primary && !string.Equals(_individualOrderPromptLabel, label, StringComparison.Ordinal)))
            {
                if (primary) HideIndividualOrderPrompt(true); else HideIndividualOrderPrompt(false);
                object controlArg = PromptControlArgument(_makePrompt.GetParameters()[0].ParameterType, control);
                prompt = Convert.ToInt32(_makePrompt.Invoke(null, new object[] { controlArg, label, true, group }));
                promptFor = target.Handle;
                promptGroup = group;
                armed = false;
                if (primary) _individualOrderPromptLabel = label;
            }
            promptPed = target;
            if (prompt != 0)
            {
                HUD._UI_PROMPT_SET_VISIBLE(prompt, true);
                HUD._UI_PROMPT_SET_ENABLED(prompt, true);
                if (!HUD._UI_PROMPT_HAS_HOLD_MODE_COMPLETED(prompt))
                {
                    armed = true;
                    SaveIndividualOrderPromptState(primary, prompt, promptFor, promptGroup, armed, promptPed);
                    return;
                }
                if (armed)
                {
                    Ped selected = promptPed;
                    SaveIndividualOrderPromptState(primary, prompt, promptFor, promptGroup, false, promptPed);
                    if (selected != null && selected.Exists() && selected.Handle == target.Handle)
                        SetIndividualCompanionOrder(instance, record, order);
                    return;
                }
            }
            SaveIndividualOrderPromptState(primary, prompt, promptFor, promptGroup, armed, promptPed);
        }

        private static void SaveIndividualOrderPromptState(bool primary, int prompt, int promptFor,
            int promptGroup, bool armed, Ped promptPed)
        {
            if (primary)
            {
                _individualOrderPrompt = prompt; _individualOrderPromptFor = promptFor;
                _individualOrderPromptGroup = promptGroup; _individualOrderPromptArmed = armed;
                _individualOrderPromptPed = promptPed;
            }
            else
            {
                _individualRestPrompt = prompt; _individualRestPromptFor = promptFor;
                _individualRestPromptGroup = promptGroup; _individualRestPromptArmed = armed;
                _individualRestPromptPed = promptPed;
            }
        }

        private static void HideIndividualOrderPrompt(bool primary)
        {
            int prompt = primary ? _individualOrderPrompt : _individualRestPrompt;
            if (prompt != 0 && IsPromptValid(prompt))
                try { HUD._UI_PROMPT_DELETE(prompt); } catch { }
            SaveIndividualOrderPromptState(primary, 0, 0, 0, false, null);
            if (primary) _individualOrderPromptLabel = null;
        }

        private static void HideIndividualOrderPrompts()
        {
            HideIndividualOrderPrompt(true);
            HideIndividualOrderPrompt(false);
        }

        private static void UpdateIncapacitatedPrompt(object instance, Ped player)
        {
            try
            {
                if (player == null || !player.Exists() || player.IsInCombat)
                {
                    HideIncapacitatedPrompt();
                    return;
                }

                Ped target = FindAimedRecruitedPed(instance, player);
                CachedRosterRecord cached;
                if (target == null || !target.Exists() ||
                    !RosterRecordsByPedHandle.TryGetValue(target.Handle, out cached) || cached == null ||
                    cached.Record == null || !IsRecruitedRosterRecord(cached.Record) ||
                    IsExiledRosterRecord(cached.Record) ||
                    (!(GetMember(cached.Record, "IsAlive") is bool alive) || !alive))
                {
                    HideIncapacitatedPrompt();
                    return;
                }

                CompanionPersistentState state = GetPersistentState(cached.Record, true);
                bool down = target.IsIncapacitated ||
                    (state != null && state.Injury != null && state.Injury.IsIncapacitated);
                if (!down)
                {
                    HideIncapacitatedPrompt();
                    return;
                }

                HideIndividualOrderPrompts();
                HideRecruitMedicinePrompt();
                HideCombatStancePrompt();

                int group = HUD._UI_PROMPT_GET_GROUP_ID_FOR_TARGET_ENTITY(target.Handle);
                if (_incapacitatedPrompt == 0 || _incapacitatedPromptFor != target.Handle ||
                    _incapacitatedPromptGroup != group || !IsPromptValid(_incapacitatedPrompt))
                {
                    HideIncapacitatedPrompt();
                    object control = PromptControlArgument(_makePrompt.GetParameters()[0].ParameterType, IncapacitatedPromptControl);
                    _incapacitatedPrompt = Convert.ToInt32(_makePrompt.Invoke(null,
                        new object[] { control, "Ayudar a levantarse", true, group }));
                    _incapacitatedPromptFor = target.Handle;
                    _incapacitatedPromptGroup = group;
                    _incapacitatedPromptArmed = false;
                    _incapacitatedPromptPed = target;
                }
                else
                {
                    _incapacitatedPromptPed = target;
                }

                if (_incapacitatedPrompt == 0) return;
                HUD._UI_PROMPT_SET_VISIBLE(_incapacitatedPrompt, true);
                HUD._UI_PROMPT_SET_ENABLED(_incapacitatedPrompt, true);
                if (!HUD._UI_PROMPT_HAS_HOLD_MODE_COMPLETED(_incapacitatedPrompt))
                {
                    _incapacitatedPromptArmed = true;
                    return;
                }
                if (!_incapacitatedPromptArmed) return;

                Ped selected = _incapacitatedPromptPed;
                _incapacitatedPromptArmed = false;
                HideIncapacitatedPrompt();
                if (selected != null && selected.Exists() && selected.Handle == target.Handle)
                    HelpIncapacitatedCompanion(instance, cached.Record, selected);
            }
            catch (Exception ex)
            {
                HideIncapacitatedPrompt();
                Log("Incapacitated companion prompt failed: " + Unwrap(ex));
            }
        }

        private static void HideIncapacitatedPrompt()
        {
            int prompt = _incapacitatedPrompt;
            if (prompt != 0 && IsPromptValid(prompt))
                try { HUD._UI_PROMPT_DELETE(prompt); } catch { }
            _incapacitatedPrompt = 0;
            _incapacitatedPromptFor = 0;
            _incapacitatedPromptGroup = 0;
            _incapacitatedPromptArmed = false;
            _incapacitatedPromptPed = null;
        }

        private static void HelpIncapacitatedCompanion(object instance, object record, Ped ped)
        {
            if (instance == null || record == null || ped == null || !ped.Exists() ||
                IsExiledRosterRecord(record) ||
                (GetMember(record, "IsAlive") is bool alive && !alive))
                return;

            if (ped.IsDead)
            {
                if (!TryInvokeNativeWrapper(typeof(PED), "RESURRECT_PED", new object[] { ped.Handle }))
                {
                    InvokeInstance(instance, "ShowSubtitle", "No se pudo reanimar al compañero con los nativos disponibles.");
                    Log("RESURRECT_PED wrapper unavailable while helping " + (GetMember(record, "Id") as string ?? "companion") + ".");
                    return;
                }
            }

            int maxHealth = 0;
            try { maxHealth = ped.MaxHealth; } catch { }
            if (maxHealth <= 0) maxHealth = 100;
            int recoveryHealth = Math.Max(1, (int)Math.Ceiling(maxHealth * _incapacitatedHealthPercent / 100.0));
            try { ped.Health = Math.Max(ped.Health, recoveryHealth); } catch { }

            try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
            bool stood = TryInvokeTaskNative("TASK_STAND_UP", new object[] { ped.Handle, true });
            if (!stood) stood = TryInvokeTaskNative("TASK_STAND_UP", new object[] { ped.Handle });
            if (!stood)
            {
                // Keep the record and roster membership even on API builds without TASK_STAND_UP.
                TryInvokeTaskNative("TASK_STAND_STILL", new object[] { ped.Handle, 900 });
                Log("TASK_STAND_UP wrapper unavailable; companion health was restored but the get-up animation may be omitted.");
            }

            string id = GetMember(record, "Id") as string;
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state != null && state.Injury != null)
            {
                state.Injury.IsIncapacitated = false;
                state.Injury.IncapacitatedSinceUtcTicks = 0;
                state.Injury.IsInjured = state.Injury.Severity > 0 || state.Injury.RecoverySecondsRemaining > 0;
                SavePersistentState(state);
            }
            SetMember(record, "IsAlive", true);
            SetMember(record, "PresumedDead", false);

            object companion = FindActiveExtensionCompanion(instance, id);
            if (companion != null && state != null && state.FollowState == CompanionFollowState.Following)
            {
                SetField(companion, "State", 0);
                SetField(companion, "AskedAt", Game.GameTime);
            }
            if (!string.IsNullOrWhiteSpace(id))
            {
                LastObservedHealthById[id] = ped.Health;
                LastHealthSampleAtById[id] = Game.GameTime;
                LastDamageAtById.Remove(id);
                LastInjuryRecoveryAtById[id] = Game.GameTime;
                RestScenarioStartedIds.Remove(id);
            }
            try { InvokeInstance(instance, "SaveRoster"); } catch { }
            InvokeInstance(instance, "ShowSubtitle", (GetMember(record, "Name") as string ?? "Tu compañero") +
                " se ha levantado. La herida seguirá recuperándose con el tiempo o al descansar.");
        }

        private static void UpdateCombatStancePrompt(object instance, Ped player)
        {
            try
            {
                if (!_enableIndividualOrders || player == null || !player.Exists())
                {
                    HideCombatStancePrompt();
                    return;
                }

                Ped target = FindAimedRecruitedPed(instance, player);
                CachedRosterRecord cached;
                if (target == null || !target.Exists() ||
                    !RosterRecordsByPedHandle.TryGetValue(target.Handle, out cached) || cached == null ||
                    cached.Record == null || !IsRecruitedRosterRecord(cached.Record) ||
                    IsExiledRosterRecord(cached.Record) ||
                    !IsRosterRecordCurrentlyFollowing(instance, cached.Record))
                {
                    HideCombatStancePrompt();
                    return;
                }

                if (IsPedIncapacitatedRecruit(target) || FindAttackerTarget(instance, target, player) == null)
                {
                    HideCombatStancePrompt();
                    return;
                }

                CompanionPersistentState state = GetPersistentState(cached.Record, true);
                if (state == null) { HideCombatStancePrompt(); return; }
                string label = "Postura: " + CombatStanceLabel(state.CombatStance);
                int group = HUD._UI_PROMPT_GET_GROUP_ID_FOR_TARGET_ENTITY(target.Handle);
                if (_combatStancePrompt == 0 || _combatStancePromptFor != target.Handle ||
                    _combatStancePromptGroup != group || !IsPromptValid(_combatStancePrompt) ||
                    !string.Equals(_combatStancePromptLabel, label, StringComparison.Ordinal))
                {
                    HideCombatStancePrompt();
                    object control = PromptControlArgument(_makePrompt.GetParameters()[0].ParameterType, CombatStancePromptControl);
                    _combatStancePrompt = Convert.ToInt32(_makePrompt.Invoke(null,
                        new object[] { control, label, true, group }));
                    _combatStancePromptFor = target.Handle;
                    _combatStancePromptGroup = group;
                    _combatStancePromptLabel = label;
                    _combatStancePromptArmed = false;
                    _combatStancePromptPed = target;
                }
                else
                {
                    _combatStancePromptPed = target;
                }

                if (_combatStancePrompt == 0) return;
                HUD._UI_PROMPT_SET_VISIBLE(_combatStancePrompt, true);
                HUD._UI_PROMPT_SET_ENABLED(_combatStancePrompt, true);
                if (!HUD._UI_PROMPT_HAS_HOLD_MODE_COMPLETED(_combatStancePrompt))
                {
                    _combatStancePromptArmed = true;
                    return;
                }
                if (!_combatStancePromptArmed) return;

                _combatStancePromptArmed = false;
                Ped selected = _combatStancePromptPed;
                HideCombatStancePrompt();
                if (selected != null && selected.Exists() && selected.Handle == target.Handle)
                    AdvanceCombatStance(instance, cached.Record, selected);
            }
            catch (Exception ex)
            {
                HideCombatStancePrompt();
                Log("Combat stance prompt failed: " + Unwrap(ex));
            }
        }

        private static string CombatStanceLabel(CompanionCombatStance stance)
        {
            switch (stance)
            {
                case CompanionCombatStance.HoldPosition: return "Mantener posición";
                case CompanionCombatStance.CoverPlayer: return "Cubrir al jugador";
                case CompanionCombatStance.DefendArea: return "Defender la zona";
                case CompanionCombatStance.FireOnSignal: return "Fuego a mi señal";
                default: return "Seguir órdenes";
            }
        }

        private static void AdvanceCombatStance(object instance, object record, Ped ped)
        {
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null || ped == null || !ped.Exists()) return;
            int next = ((int)state.CombatStance + 1) % 5;
            state.CombatStance = (CompanionCombatStance)next;
            if (state.CombatStance == CompanionCombatStance.DefendArea)
            {
                var pos = ped.Position;
                state.SavedPosition.HasValue = true;
                state.SavedPosition.X = pos.X;
                state.SavedPosition.Y = pos.Y;
                state.SavedPosition.Z = pos.Z;
                try { state.SavedPosition.Heading = ped.Heading; } catch { state.SavedPosition.Heading = 0f; }
            }
            SavePersistentState(state);
            InvokeInstance(instance, "ShowSubtitle", (GetMember(record, "Name") as string ?? "Tu compañero") +
                ": postura " + CombatStanceLabel(state.CombatStance) + ".");
        }

        private static void HideCombatStancePrompt()
        {
            int prompt = _combatStancePrompt;
            if (prompt != 0 && IsPromptValid(prompt))
                try { HUD._UI_PROMPT_DELETE(prompt); } catch { }
            _combatStancePrompt = 0;
            _combatStancePromptFor = 0;
            _combatStancePromptGroup = 0;
            _combatStancePromptArmed = false;
            _combatStancePromptPed = null;
            _combatStancePromptLabel = null;
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
                    IsExiledRosterRecord(cached.Record) || IsPedIncapacitatedRecruit(target))
                {
                    HideRecruitMedicinePrompt();
                    return;
                }

                // During a real encounter H is reserved for the contextual combat-stance prompt.
                if (FindAttackerTarget(instance, target, player) != null)
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
                    string promptLabel = "Dar remedio ($" +
                        (_recruitMedicineCostCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + ")";
                    object result = _makePrompt.Invoke(null, new object[] { control, promptLabel, true, group });
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

                // Snapshot the live world's outfit before enRAGE's Adopt routine changes the ped.
                PreserveRecruitOutfit(rosterRecord ?? trackedRecord, ped);

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
                // Both first-time recruitment and prompt-based recall must leave the persistent
                // individual order in Following; a dismissed state must not survive re-adoption.
                SetPersistentFollowState(rosterRecord ?? trackedRecord, CompanionFollowState.Following, null);
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

            Ped threat = FindAttackerTarget(instance, companionPed, player);
            int companionHandle = companionPed.Handle;
            int previousTarget;
            bool hadPreviousTarget = CustomCompanionCombatTargets.TryGetValue(companionHandle, out previousTarget);
            object stanceRecord = GetField(companion, "Rec");
            string stanceId = GetMember(stanceRecord, "Id") as string;
            CompanionPersistentState stanceState = GetPersistentState(stanceRecord, false);
            CompanionCombatStance stance = stanceState == null
                ? CompanionCombatStance.FollowOrders : stanceState.CombatStance;

            bool playerFiring = false;
            try { playerFiring = PED.IS_PED_SHOOTING(player.Handle); } catch { }
            if (playerFiring && !string.IsNullOrWhiteSpace(stanceId))
                LastPlayerFireAtById[stanceId] = now;

            if (stance == CompanionCombatStance.HoldPosition)
            {
                CustomCompanionCombatTargets.Remove(companionHandle);
                CustomCombatSuppressedAt[companionHandle] = now;
                if (hadPreviousTarget)
                    try { TASK.CLEAR_PED_TASKS(companionHandle, true, false); } catch { }
                try { PED.SET_PED_COMBAT_ATTRIBUTES(companionHandle, 5, false); } catch { }
                try { PED.SET_PED_COMBAT_ATTRIBUTES(companionHandle, 46, false); } catch { }
                TryInvokeTaskNative("TASK_STAND_STILL", new object[] { companionHandle, 900 });
                return;
            }

            if (stance == CompanionCombatStance.DefendArea && stanceState != null &&
                stanceState.SavedPosition != null && stanceState.SavedPosition.HasValue && threat != null)
            {
                float dx = threat.Position.X - stanceState.SavedPosition.X;
                float dy = threat.Position.Y - stanceState.SavedPosition.Y;
                float dz = threat.Position.Z - stanceState.SavedPosition.Z;
                float radius = Math.Max(10.0f, _waitRadius * 2.0f);
                if ((float)Math.Sqrt(dx * dx + dy * dy + dz * dz) > radius)
                    threat = null;
            }

            if (stance == CompanionCombatStance.FireOnSignal)
            {
                int lastPlayerFire;
                bool recentPlayerFire = playerFiring ||
                    (!string.IsNullOrWhiteSpace(stanceId) &&
                     LastPlayerFireAtById.TryGetValue(stanceId, out lastPlayerFire) &&
                     now >= lastPlayerFire && now - lastPlayerFire <= 2500);
                if (!recentPlayerFire)
                    threat = null;
            }

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
            object affinityRecord = GetField(companion, "Rec");
            string affinityId = GetMember(affinityRecord, "Id") as string;
            int lastCombatAt;
            if (!string.IsNullOrWhiteSpace(affinityId) &&
                (!LastAffinityCombatAt.TryGetValue(affinityId, out lastCombatAt) || now < lastCombatAt || now - lastCombatAt >= 30000))
            {
                LastAffinityCombatAt[affinityId] = now;
                AwardAffinity(affinityRecord, _affinityCombatGain, "combat");
            }
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
            }
        }

        private static Ped FindAttackerTarget(object instance, Ped companion, Ped player)
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

        private static bool IsEligibleReleasedRecruitForRecall(object instance, Ped player, Ped ped)
        {
            if (instance == null || player == null || !player.Exists() || ped == null || !ped.Exists() ||
                ped.IsDead || !ped.IsHuman || ped.Handle == player.Handle || ped.IsHogtied ||
                ped.IsInVehicle || ped.IsOnMount || ped.Position.DistanceTo(player.Position) > 3.5f ||
                player.IsInCombat || IsAskBlocked(instance) || CountActiveCompanions(instance) >= _maxCompanions ||
                AlreadyCompanion(GetField(instance, "_comps") as IList, ped.Handle) ||
                !IsCachedRecruitedPed(ped.Handle) || IsExtensionCompanionActiveForPed(instance, ped.Handle) ||
                PED.IS_PED_IN_COMBAT(ped.Handle, 0))
                return false;

            CachedRosterRecord cached;
            if (!RosterRecordsByPedHandle.TryGetValue(ped.Handle, out cached) || cached == null || cached.Record == null)
                return false;
            string id = GetMember(cached.Record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id) || ExiledRosterIds.Contains(id) || IsExiledRosterRecord(cached.Record))
                return false;
            if (GetMember(cached.Record, "IsAlive") is bool alive && !alive)
                return false;
            return IsMalePed(ped);
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
                NormalizeExtensionRecruitHistory(existingRecord);
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
            // A new recruit has no player-kill history. NemesisRecord's enum default is Killed,
            // so leaving Origin untouched fabricates the grudge text "te mató" on the character sheet.
            SetMember(created, "KillLocation", string.Empty);
            SetMember(created, "KilledPlayerAt", DateTime.MinValue);
            SetMember(created, "TimesKilledPlayer", 0);
            SetMember(created, "KilledByPlayer", false);
            SetEnumMember(created, "Origin", "GangMember");
            SetMember(created, "FormerGang", null);
            SetMember(created, "Affiliation", null);
            SetMember(created, "BossId", null);
            SetMember(created, "CampId", null);
            SetMember(created, "CampKnown", false);
            SetStoryFlag(created, false);
            EnsureRosterProfileData(instance, created);
            NormalizeExtensionRecruitHistory(created);

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
                if (ped != null && ped.Exists() && !string.IsNullOrWhiteSpace(id))
                {
                    if (!LastObservedHealthById.ContainsKey(id))
                        LastObservedHealthById[id] = ped.Health;
                    if (!LastHealthSampleAtById.ContainsKey(id))
                        LastHealthSampleAtById[id] = Game.GameTime;
                }
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
                try
                {
                    Ped ped = cached.Ped;
                    if (!ped.Exists()) continue;
                    string id = GetMember(cached.Record, "Id") as string;
                    if (!ped.IsDead && !ped.IsIncapacitated) continue;

                    int health = 0;
                    try { health = ped.Health; } catch { }
                    int previousHealth;
                    if (!string.IsNullOrWhiteSpace(id) &&
                        LastObservedHealthById.TryGetValue(id, out previousHealth) && health < previousHealth)
                        LastDamageAtById[id] = Game.GameTime;

                    // In RDR2 a ped can report IsDead while still in the incapacitated state.
                    // Try the recovery path before mutating the roster's IsAlive flag.
                    if (IsLikelyIncapacitated(cached.Record, ped, id))
                    {
                        SetMember(cached.Record, "IsAlive", true);
                        SetMember(cached.Record, "PresumedDead", false);
                        TryEnterIncapacitated(instance, cached.Record, ped);
                        continue;
                    }

                    if (!(GetMember(cached.Record, "IsAlive") is bool alive) || !alive)
                        continue;
                    SetMember(cached.Record, "IsAlive", false);
                    SetMember(cached.Record, "PresumedDead", false);
                    AppliedPromptNames.Remove(pair.Key);
                    CustomCompanionCombatTargets.Remove(pair.Key);
                    CustomCombatSuppressedAt.Remove(pair.Key);
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        RestScenarioStartedIds.Remove(id);
                        LastObservedHealthById.Remove(id);
                    }
                    string zone = InvokeInstance(instance, "GetZoneName", ped.Position) as string;
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
            Ped ped = GetField(companion, "Ped") as Ped;
            if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
            bool preserveHorse = false;
            Ped horse = GetField(companion, "Horse") as Ped;
            Ped player = Game.Player.Character;
            try { preserveHorse = ped != null && ped.Exists() && ped.IsOnMount && horse != null && horse.Exists(); } catch { }
            // Capture the current outfit before RestoreReal. If the native returns zero, keep any
            // previously saved non-zero hash rather than replacing it with an invalid value.
            if (ped != null && ped.Exists()) CaptureRecruitOutfitIfMissing(GetField(companion, "Rec"), ped, true);
            SetPersistentFollowState(GetField(companion, "Rec"), CompanionFollowState.Dismissed, null);
            LastIndividualOrderTaskAt.Remove(GetMember(GetField(companion, "Rec"), "Id") as string ?? string.Empty);

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
                AmbientReleasedRecruitPeds.Add(ped.Handle);
                CustomCompanionCombatTargets.Remove(ped.Handle);
                CustomCombatSuppressedAt.Remove(ped.Handle);
                try
                {
                    if (ped.IsOnMount)
                    {
                        TASK.TASK_DISMOUNT_ANIMAL(ped.Handle, 0, 0UL, 0UL, 0UL, 0);
                        AmbientReleasedWanderAt[ped.Handle] = Game.GameTime + 1800;
                    }
                    else
                    {
                        TASK.TASK_WANDER_STANDARD(ped.Handle, 10f, 10);
                        AmbientReleasedWanderAt.Remove(ped.Handle);
                    }
                }
                catch (Exception ex) { Log("Ambient release task warning: " + Unwrap(ex)); }
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

        private static bool MaintainCompanionCondition(object instance, object companion, Ped player)
        {
            if (!_enableIndividualOrders || companion == null) return false;
            string key = GetField(companion, "Key") as string;
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
            object record = GetField(companion, "Rec");
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id) || record == null || IsExiledRosterRecord(record) ||
                (GetMember(record, "IsAlive") is bool alive && !alive)) return false;

            Ped ped = GetField(companion, "Ped") as Ped;
            if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
            if (ped == null || !ped.Exists()) return false;
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null || state.Injury == null) return false;

            int now = Game.GameTime;
            int health = 0;
            try { health = ped.Health; } catch { }
            int previousHealth;
            bool hasPreviousHealth = LastObservedHealthById.TryGetValue(id, out previousHealth);
            if (hasPreviousHealth && health < previousHealth)
                LastDamageAtById[id] = now;

            if (IsLikelyIncapacitated(record, ped, id))
            {
                TryEnterIncapacitated(instance, record, ped);
                LastObservedHealthById[id] = Math.Max(1, health);
                LastHealthSampleAtById[id] = now;
                return true;
            }

            if (state.Injury.IsIncapacitated)
            {
                // If the game has already stood the ped up, synchronize the persisted condition.
                if (!ped.IsDead && !ped.IsIncapacitated)
                {
                    state.Injury.IsIncapacitated = false;
                    state.Injury.IncapacitatedSinceUtcTicks = 0;
                    SavePersistentState(state);
                }
                else if (ped.IsDead)
                {
                    // A persisted down state is retried briefly if the native temporarily reports
                    // IsDead during incapacitation. A real execution after that window is still death.
                    long since = state.Injury.IncapacitatedSinceUtcTicks;
                    if (since > 0 && DateTime.UtcNow.Ticks - since < TimeSpan.FromMinutes(1).Ticks)
                    {
                        TryEnterIncapacitated(instance, record, ped);
                        LastObservedHealthById[id] = Math.Max(1, health);
                        LastHealthSampleAtById[id] = now;
                        return true;
                    }
                }
            }

            if (ped.IsDead)
                return false;

            if (_enableInjurySystem && hasPreviousHealth && health < previousHealth)
            {
                int delta = previousHealth - health;
                int threshold = Math.Max(1, (int)Math.Ceiling(Math.Max(1, ped.MaxHealth) * _injuryDamageThresholdPercent / 100.0));
                if (delta >= threshold)
                {
                    bool wasInjured = state.Injury.IsInjured;
                    state.Injury.IsInjured = true;
                    state.Injury.Severity = Math.Max(state.Injury.Severity,
                        Math.Max(1, Math.Min(100, (int)Math.Ceiling(delta * 100.0 / Math.Max(1, ped.MaxHealth)))));
                    if (state.Injury.FirstInjuredUtcTicks <= 0)
                        state.Injury.FirstInjuredUtcTicks = DateTime.UtcNow.Ticks;
                    state.Injury.RecoverySecondsRemaining = Math.Max(state.Injury.RecoverySecondsRemaining, _injuryRecoverySeconds);
                    SavePersistentState(state);
                    if (!wasInjured)
                        InvokeInstance(instance, "ShowSubtitle", (GetMember(record, "Name") as string ?? "Tu compañero") +
                            " está herido. Descansar o usar un remedio ayudará a recuperarlo.");
                    Log("Tracked companion injury " + id + "; damage=" + delta + ", severity=" + state.Injury.Severity + ".");
                }
            }

            LastObservedHealthById[id] = health;
            int previousSample;
            if (!LastHealthSampleAtById.TryGetValue(id, out previousSample) || now < previousSample || now - previousSample >= 1000)
            {
                LastHealthSampleAtById[id] = now;
                RecoverInjuryOverTime(id, state, now);
            }
            return false;
        }

        private static void RecoverInjuryOverTime(string id, CompanionPersistentState state, int now)
        {
            if (!_enableInjurySystem || state == null || state.Injury == null ||
                !state.Injury.IsInjured || state.Injury.IsIncapacitated)
            {
                LastInjuryRecoveryAtById[id] = now;
                return;
            }
            int last;
            if (!LastInjuryRecoveryAtById.TryGetValue(id, out last) || now < last)
            {
                LastInjuryRecoveryAtById[id] = now;
                return;
            }
            int elapsedSeconds = (now - last) / 1000;
            if (elapsedSeconds <= 0) return;
            LastInjuryRecoveryAtById[id] = last + elapsedSeconds * 1000;
            state.Injury.RecoverySecondsRemaining = Math.Max(0, state.Injury.RecoverySecondsRemaining - elapsedSeconds);
            bool recovered = state.Injury.RecoverySecondsRemaining <= 0;
            // Avoid writing the JSON every frame; persistence is updated in coarse intervals.
            if (elapsedSeconds >= 5 || recovered)
            {
                if (recovered)
                {
                    state.Injury.IsInjured = false;
                    state.Injury.Severity = 0;
                    state.Injury.FirstInjuredUtcTicks = 0;
                }
                SavePersistentState(state);
                if (recovered)
                    Log("Companion injury recovery completed for " + id + ".");
            }
        }

        private static bool IsLikelyIncapacitated(object record, Ped ped, string id)
        {
            if (ped == null || !ped.Exists()) return false;
            try { if (ped.IsIncapacitated) return true; } catch { }
            CompanionPersistentState state = GetPersistentState(record, false);
            if (state != null && state.Injury != null && state.Injury.IsIncapacitated && ped.IsDead)
            {
                long since = state.Injury.IncapacitatedSinceUtcTicks;
                if (since == 0 || DateTime.UtcNow.Ticks - since < TimeSpan.FromMinutes(1).Ticks)
                    return true;
            }
            // enRAGE tracks a recent ragdoll/down event for up to 10 seconds; use the same limited
            // window for a fatal health drop, but never infer incapacitation from an old corpse.
            if (ped.IsDead && ped.IsRagdoll)
            {
                int last;
                if (!string.IsNullOrWhiteSpace(id) && LastDamageAtById.TryGetValue(id, out last) &&
                    Game.GameTime >= last && Game.GameTime - last <= 10000)
                    return true;
            }
            return false;
        }

        private static bool IsPedIncapacitatedRecruit(Ped ped)
        {
            if (ped == null || !ped.Exists()) return false;
            try { if (ped.IsIncapacitated) return true; } catch { }
            CachedRosterRecord cached;
            if (!RosterRecordsByPedHandle.TryGetValue(ped.Handle, out cached) || cached == null || cached.Record == null)
                return false;
            CompanionPersistentState state = GetPersistentState(cached.Record, false);
            return state != null && state.Injury != null && state.Injury.IsIncapacitated;
        }

        private static bool TryEnterIncapacitated(object instance, object record, Ped ped)
        {
            if (instance == null || record == null || ped == null || !ped.Exists() ||
                IsExiledRosterRecord(record) || (GetMember(record, "IsAlive") is bool alive && !alive))
                return false;

            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id) || !IsRecruitedRosterRecord(record)) return false;
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null || state.Injury == null) return false;

            bool wasIncapacitated = state.Injury.IsIncapacitated;
            bool wasInjured = state.Injury.IsInjured;
            int oldSeverity = state.Injury.Severity;
            double oldRecovery = state.Injury.RecoverySecondsRemaining;
            long oldFirstInjured = state.Injury.FirstInjuredUtcTicks;
            long oldIncapacitatedSince = state.Injury.IncapacitatedSinceUtcTicks;
            bool rosterWasAlive = GetMember(record, "IsAlive") is bool currentlyAlive && currentlyAlive;
            bool rosterWasPresumedDead = GetMember(record, "PresumedDead") is bool presumedDead && presumedDead;

            state.Injury.IsIncapacitated = true;
            state.Injury.IsInjured = true;
            state.Injury.Severity = Math.Max(40, state.Injury.Severity);
            if (state.Injury.FirstInjuredUtcTicks <= 0)
                state.Injury.FirstInjuredUtcTicks = DateTime.UtcNow.Ticks;
            if (state.Injury.IncapacitatedSinceUtcTicks <= 0)
                state.Injury.IncapacitatedSinceUtcTicks = DateTime.UtcNow.Ticks;
            state.Injury.RecoverySecondsRemaining = Math.Max(state.Injury.RecoverySecondsRemaining, _injuryRecoverySeconds);
            SetMember(record, "IsAlive", true);
            SetMember(record, "PresumedDead", false);
            bool stateChanged = !wasIncapacitated || !wasInjured || oldSeverity != state.Injury.Severity ||
                Math.Abs(oldRecovery - state.Injury.RecoverySecondsRemaining) > 0.01 ||
                oldFirstInjured != state.Injury.FirstInjuredUtcTicks ||
                oldIncapacitatedSince != state.Injury.IncapacitatedSinceUtcTicks ||
                !rosterWasAlive || rosterWasPresumedDead;

            if (ped.IsDead)
            {
                int lastAttempt;
                bool attemptNow = !LastResurrectionAttemptAtById.TryGetValue(id, out lastAttempt) ||
                    Game.GameTime < lastAttempt || Game.GameTime - lastAttempt >= 1000;
                if (attemptNow)
                {
                    LastResurrectionAttemptAtById[id] = Game.GameTime;
                    if (TryInvokeNativeWrapper(typeof(PED), "RESURRECT_PED", new object[] { ped.Handle }))
                    {
                        try { ped.Health = Math.Max(1, (int)Math.Ceiling(Math.Max(1, ped.MaxHealth) * _incapacitatedHealthPercent / 100.0)); } catch { }
                        TryInvokeTaskNative("TASK_KNOCKED_OUT", new object[] { ped.Handle, -1.0f, false });
                    }
                    else
                    {
                        Log("RESURRECT_PED wrapper unavailable; kept roster membership but could not reanimate downed ped " + id + ".");
                    }
                }
            }

            if (stateChanged) SavePersistentState(state);
            if (!wasIncapacitated)
                InvokeInstance(instance, "ShowSubtitle", (GetMember(record, "Name") as string ?? "Tu compañero") +
                    " ha quedado incapacitado. Apúntale y mantén E para ayudarlo a levantarse.");
            return true;
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
                string recordId = GetMember(record, "Id") as string;
                if (IsLikelyIncapacitated(record, ped, recordId))
                {
                    TryEnterIncapacitated(instance, record, ped);
                    return;
                }

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
                    if (!existingKeys.Contains("EnableAffinity")) missingSettings.Add("EnableAffinity=true");
                    if (!existingKeys.Contains("AffinityTravelIntervalSeconds")) missingSettings.Add("AffinityTravelIntervalSeconds=300");
                    if (!existingKeys.Contains("AffinityTravelGain")) missingSettings.Add("AffinityTravelGain=1");
                    if (!existingKeys.Contains("AffinityCombatGain")) missingSettings.Add("AffinityCombatGain=2");
                    if (!existingKeys.Contains("AffinityTreatmentGain")) missingSettings.Add("AffinityTreatmentGain=3");
                    if (!existingKeys.Contains("EnableAppearancePersistence")) missingSettings.Add("EnableAppearancePersistence=true");
                    if (!existingKeys.Contains("PreserveSavedOutfit")) missingSettings.Add("PreserveSavedOutfit=true");
                    if (!existingKeys.Contains("EnableIndividualOrders")) missingSettings.Add("EnableIndividualOrders=true");
                    if (!existingKeys.Contains("EnableWaitMode")) missingSettings.Add("EnableWaitMode=true");
                    if (!existingKeys.Contains("WaitRadius")) missingSettings.Add("WaitRadius=8.0");
                    if (!existingKeys.Contains("RestDurationSeconds")) missingSettings.Add("RestDurationSeconds=60");
                    if (!existingKeys.Contains("EnableInjurySystem")) missingSettings.Add("EnableInjurySystem=true");
                    if (!existingKeys.Contains("InjuryDamageThresholdPercent")) missingSettings.Add("InjuryDamageThresholdPercent=10");
                    if (!existingKeys.Contains("InjuryRecoverySeconds")) missingSettings.Add("InjuryRecoverySeconds=180");
                    if (!existingKeys.Contains("IncapacitatedHealthPercent")) missingSettings.Add("IncapacitatedHealthPercent=35");
                    if (!existingKeys.Contains("MedicineHealingPercent")) missingSettings.Add("MedicineHealingPercent=100");
                    if (!existingKeys.Contains("MedicineRecoveryReductionSeconds")) missingSettings.Add("MedicineRecoveryReductionSeconds=60");
                    if (!existingKeys.Contains("RecruitMedicineCostCents")) missingSettings.Add("RecruitMedicineCostCents=100");
                    if (!existingKeys.Contains("RestHealsInjuries")) missingSettings.Add("RestHealsInjuries=true");
                    if (missingSettings.Count > 0)
                    {
                        File.AppendAllText(path, Environment.NewLine + "; Added settings for affinity, appearance, and individual companion orders" + Environment.NewLine + string.Join(Environment.NewLine, missingSettings) + Environment.NewLine);
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
                        else if (key.Equals("EnableAffinity", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool enableAffinity))
                            _enableAffinity = enableAffinity;
                        else if (key.Equals("AffinityTravelIntervalSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int travelInterval))
                            _affinityTravelIntervalSeconds = Math.Max(30, Math.Min(7200, travelInterval));
                        else if (key.Equals("AffinityTravelGain", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int travelGain))
                            _affinityTravelGain = Math.Max(0, Math.Min(100, travelGain));
                        else if (key.Equals("AffinityCombatGain", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int combatGain))
                            _affinityCombatGain = Math.Max(0, Math.Min(100, combatGain));
                        else if (key.Equals("AffinityTreatmentGain", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int treatmentGain))
                            _affinityTreatmentGain = Math.Max(0, Math.Min(100, treatmentGain));
                        else if (key.Equals("EnableAppearancePersistence", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool appearancePersistence))
                            _enableAppearancePersistence = appearancePersistence;
                        else if (key.Equals("PreserveSavedOutfit", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool preserveOutfit))
                            _preserveSavedOutfit = preserveOutfit;
                        else if (key.Equals("EnableIndividualOrders", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool enableOrders))
                            _enableIndividualOrders = enableOrders;
                        else if (key.Equals("EnableWaitMode", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool enableWait))
                            _enableWaitMode = enableWait;
                        else if (key.Equals("WaitRadius", StringComparison.OrdinalIgnoreCase) &&
                                 float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float waitRadius))
                            _waitRadius = Math.Max(1.0f, Math.Min(50.0f, waitRadius));
                        else if (key.Equals("RestDurationSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int restDuration))
                            _restDurationSeconds = Math.Max(10, Math.Min(3600, restDuration));
                        else if (key.Equals("EnableInjurySystem", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool enableInjurySystem))
                            _enableInjurySystem = enableInjurySystem;
                        else if (key.Equals("InjuryDamageThresholdPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int injuryThreshold))
                            _injuryDamageThresholdPercent = Math.Max(1, Math.Min(50, injuryThreshold));
                        else if (key.Equals("InjuryRecoverySeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int injuryRecovery))
                            _injuryRecoverySeconds = Math.Max(15, Math.Min(86400, injuryRecovery));
                        else if (key.Equals("IncapacitatedHealthPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int incapacitatedHealth))
                            _incapacitatedHealthPercent = Math.Max(10, Math.Min(75, incapacitatedHealth));
                        else if (key.Equals("MedicineHealingPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int medicineHealing))
                            _medicineHealingPercent = Math.Max(1, Math.Min(100, medicineHealing));
                        else if (key.Equals("MedicineRecoveryReductionSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int medicineReduction))
                            _medicineRecoveryReductionSeconds = Math.Max(0, Math.Min(86400, medicineReduction));
                        else if (key.Equals("RecruitMedicineCostCents", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int medicineCost))
                            _recruitMedicineCostCents = Math.Max(0, Math.Min(100000, medicineCost));
                        else if (key.Equals("RestHealsInjuries", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool restHealsInjuries))
                            _restHealsInjuries = restHealsInjuries;
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
                        "PreventTargeting=false\r\n" +
                        "EnableAffinity=true\r\n" +
                        "AffinityTravelIntervalSeconds=300\r\n" +
                        "AffinityTravelGain=1\r\n" +
                        "AffinityCombatGain=2\r\n" +
                        "AffinityTreatmentGain=3\r\n" +
                        "EnableAppearancePersistence=true\r\n" +
                        "PreserveSavedOutfit=true\r\n" +
                        "EnableIndividualOrders=true\r\n" +
                        "EnableWaitMode=true\r\n" +
                        "WaitRadius=8.0\r\n" +
                        "RestDurationSeconds=60\r\n" +
                        "EnableInjurySystem=true\r\n" +
                        "InjuryDamageThresholdPercent=10\r\n" +
                        "InjuryRecoverySeconds=180\r\n" +
                        "IncapacitatedHealthPercent=35\r\n" +
                        "MedicineHealingPercent=100\r\n" +
                        "MedicineRecoveryReductionSeconds=60\r\n" +
                        "RecruitMedicineCostCents=100\r\n" +
                        "RestHealsInjuries=true\r\n");
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
