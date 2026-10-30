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
        private static MethodInfo _regardOfRecord;
        private static MethodInfo _roleOf;
        private static MethodInfo _whoIs;
        private static MethodInfo _makePrompt;
        private static MethodInfo _promptValid;
        private static MethodInfo _interrogable;
        private static MethodInfo _updateInterrogation;
        private static MethodInfo _woundedDown;
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
        // Manual E-key wound recovery remains available; the unstable custom wait/follow-order prompt is removed.
        // Keep wounded-down recovery available on E; only the separate wait/follow order prompt stays removed.
        private const int IncapacitatedPromptControl = unchecked((int)0xCEFD9220); // context-yes / E
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
        // Legacy INI keys are stripped at startup; persisted wait states migrate back to Following.
        private static bool _enableInjurySystem = true;
        private static int _injuryDamageThresholdPercent = 10;
        private static int _injuryRecoverySeconds = 180;
        private static int _incapacitatedHealthPercent = 35;
        private static int _medicineHealingPercent = 100;
        private static int _medicineRecoveryReductionSeconds = 60;
        private static int _recruitMedicineCostCents = 100;
        private static readonly Dictionary<string, string> DefaultExtensionTexts = CreateDefaultExtensionTexts();
        private static readonly Dictionary<string, string> ExtensionTexts =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastObservedHealthById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastDamageBaselineById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastDamageAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastHealthSampleAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastInjuryRecoveryAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastInjuryPersistAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastAutoAssistAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> AutoAssistTargetByHelperId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastAutoAssistMoveAtByHelperId =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> RecoveryAnimationUntilById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int _lastPlayerWeaponFireAt = -100000;
        private static readonly Dictionary<string, int> LastMedicineTransactionAt =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastPlayerFireAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private sealed class AffinityTravelTracker
        {
            internal int StartedAt;
            internal Vector3 StartPosition;
        }
        private static readonly Dictionary<string, AffinityTravelTracker> AffinityTravelTrackers =
            new Dictionary<string, AffinityTravelTracker>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastAffinityCombatAt =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastNaturalAffinityActionAt =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastPlayerMeleeActionAtById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastAffinityObservedHealthById =
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
                _regardOfRecord = FindMethod(_mainType, "RegardOfRecord", 1,
                    p => p[0].ParameterType.Name == "NemesisRecord");
                MethodInfo regardEarned = FindMethod(_mainType, "RegardEarned", 1,
                    p => p[0].ParameterType == typeof(string));
                _roleOf = FindMethod(_mainType, "RoleOf", 1, p => p[0].ParameterType == typeof(int));
                _whoIs = FindMethod(_mainType, "WhoIs", 1, p => p[0].ParameterType == typeof(int));
                _makePrompt = FindMethod(_mainType, "MakePrompt", 4, p =>
                    (p[0].ParameterType.IsEnum || p[0].ParameterType == typeof(int)) &&
                    p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(bool) &&
                    p[3].ParameterType == typeof(int));
                _promptValid = FindMethod(_mainType, "PromptValid", 1, p => p[0].ParameterType == typeof(int));
                _interrogable = FindMethod(_mainType, "Interrogable", 3, p => IsPedType(p[0].ParameterType) && IsPedType(p[1].ParameterType) && p[2].ParameterType == typeof(float));
                _updateInterrogation = FindMethod(_mainType, "UpdateInterrogation", 1, p => IsPedType(p[0].ParameterType));
                _woundedDown = FindMethod(_mainType, "WoundedDown", 1, p => IsPedType(p[0].ParameterType));
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
                MethodInfo waitToJoin = FindMethod(_mainType, "WaitToJoin", 2, p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType));
                MethodInfo ride = FindMethod(_mainType, "Ride", 2, p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType));
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
                LoadExtensionTexts();
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
                    MethodInfo drawDetail = FindMethod(_rosterMenuType, "DrawDetail", 0);
                    MethodInfo activateDetail = FindMethod(_rosterMenuType, "ActivateDetail", 0);
                    MethodInfo handleDetailKey = FindMethod(_rosterMenuType, "HandleDetailKey", 1);
                    if (detailActions != null) _harmony.Patch(detailActions, postfix: Hook(nameof(DetailActionsPostfix)));
                    if (drawDetail != null) _harmony.Patch(drawDetail, transpiler: Hook(nameof(DrawDetailAffinityTranspiler)));
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
                // Waiting/incapacitated extension recruits must not have enRAGE's stock movement
                // methods overwrite the order task after UpdateCompanion runs.
                if (waitToJoin != null) _harmony.Patch(waitToJoin, prefix: Hook(nameof(CompanionMovementOrderPrefix)));
                if (ride != null) _harmony.Patch(ride, prefix: Hook(nameof(CompanionMovementOrderPrefix)));
                // enRAGE's stock CompanionFight attacks every hated target around the player once
                // any nearby combat starts. Extension recruits use a strict retaliatory policy.
                _harmony.Patch(_companionFight, prefix: Hook(nameof(CompanionFightPrefix)));

                if (_recordAt != null)
                    _harmony.Patch(_recordAt, postfix: Hook(nameof(RecordAtPostfix)));
                if (_regardOfRecord != null)
                    _harmony.Patch(_regardOfRecord, postfix: Hook(nameof(RegardOfRecordPostfix)));
                if (regardEarned != null)
                    _harmony.Patch(regardEarned, postfix: Hook(nameof(RegardEarnedPostfix)));

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

                PropertyInfo idProperty = recordType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo getId = idProperty == null ? null : idProperty.GetGetMethod();
                MethodInfo startsWith = typeof(string).GetMethod("StartsWith", new[] { typeof(string), typeof(StringComparison) });
                if (getId == null || startsWith == null)
                {
                    Log("Intent override unavailable; NemesisRecord.Id getter or string comparison method was not found.");
                    return;
                }

                // Generate a prefix with exact runtime argument/enum types, without taking a compile-time
                // reference to NemesisMod.dll. Extension recruits use stable extcomp_/extvdl_ IDs.
                AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                    new AssemblyName("EnRAGECompanionIntentRuntime_" + Guid.NewGuid().ToString("N")),
                    AssemblyBuilderAccess.Run);
                ModuleBuilder module = assembly.DefineDynamicModule("IntentPrefixModule");
                TypeBuilder type = module.DefineType("IntentPrefix", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                MethodBuilder prefix = type.DefineMethod("Prefix", MethodAttributes.Public | MethodAttributes.Static,
                    typeof(bool), new[] { recordType, intentOf.ReturnType.MakeByRefType() });
                prefix.DefineParameter(1, ParameterAttributes.None, "__0");
                prefix.DefineParameter(2, ParameterAttributes.None, "__result");

                ILGenerator il = prefix.GetILGenerator();
                LocalBuilder id = il.DeclareLocal(typeof(string));
                Label original = il.DefineLabel();
                Label extensionRecord = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, getId);
                il.Emit(OpCodes.Stloc, id);
                il.Emit(OpCodes.Ldloc, id);
                il.Emit(OpCodes.Brfalse_S, original);

                il.Emit(OpCodes.Ldloc, id);
                il.Emit(OpCodes.Ldstr, "extcomp_");
                il.Emit(OpCodes.Ldc_I4, (int)StringComparison.OrdinalIgnoreCase);
                il.Emit(OpCodes.Callvirt, startsWith);
                il.Emit(OpCodes.Brtrue_S, extensionRecord);
                il.Emit(OpCodes.Ldloc, id);
                il.Emit(OpCodes.Ldstr, "extvdl_");
                il.Emit(OpCodes.Ldc_I4, (int)StringComparison.OrdinalIgnoreCase);
                il.Emit(OpCodes.Callvirt, startsWith);
                il.Emit(OpCodes.Brfalse_S, original);

                il.MarkLabel(extensionRecord);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(Enum.Parse(intentOf.ReturnType, "None"), CultureInfo.InvariantCulture));
                il.Emit(OpCodes.Stind_I4);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(original);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);

                Type prefixType = type.CreateType();
                _harmony.Patch(intentOf, prefix: new HarmonyMethod(prefixType.GetMethod("Prefix")));
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
                    __args[1] = T("action.dismiss");
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

        private static bool CompanionMovementOrderPrefix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return true;
                object companion = __args[0];
                string key = GetField(companion, "Key") as string;
                if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    return true;

                object record = GetField(companion, "Rec");
                CompanionPersistentState state = GetPersistentState(record, false);
                if (state == null) return true;
                NormalizeRemovedWaitState(state);
                if (state.Injury != null && state.Injury.IsIncapacitated)
                    return false;
            }
            catch (Exception ex) { Log("Companion movement order guard warning: " + Unwrap(ex)); }
            return true;
        }

        private static void CompanionCyclePostfix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0 || !(__args[0] is Ped player) || player == null || !player.Exists())
                    return;
                try { if (PED.IS_PED_SHOOTING(player.Handle)) _lastPlayerWeaponFireAt = Game.GameTime; } catch { }
                SyncVanDerLindeStoryRoster(__instance, false);
                UpdateCachedRecruitedPedDeaths(__instance);
                MaintainDismissedRecruitRelationships(__instance, player);
                MaintainRecruitedFriendship(__instance);
                MaintainRecruitedIdentity(__instance);
                MaintainAffinityTravel(__instance, player);
                UpdateNaturalAffinityInteractions(__instance, player);

                Ped stockAskTarget = GetField(__instance, "_askPed") as Ped;
                if (stockAskTarget != null && stockAskTarget.Exists() && IsPedIncapacitatedRecruit(stockAskTarget))
                    InvokeInstance(__instance, "HideAskPrompt");
                else
                    InvokeInstance(__instance, "UpdateAsk", player);

                UpdateGenericRecruitPrompt(__instance, player);
                // Manual wounded-down recovery is retained; only the custom wait/follow order was removed.
                UpdateIncapacitatedPrompt(__instance, player);
                HideIndividualOrderPrompts();
                UpdateCombatStancePrompt(__instance, player);
                UpdateRecruitMedicinePrompt(__instance, player);

                IList list = GetField(__instance, "_comps") as IList;
                if (list == null || list.Count == 0) return;
                object[] snapshot = list.Cast<object>().ToArray();
                TryAutoAssistIncapacitatedCompanions(__instance, player, snapshot);
                foreach (object companion in snapshot)
                {
                    if (companion == null) continue;
                    NormalizeRemovedWaitState(GetPersistentState(GetField(companion, "Rec"), false));
                    if (MaintainCompanionCondition(__instance, companion, player))
                    {
                        MaintainExtensionCompanionDamageability(companion, player);
                        continue;
                    }
                    MarkRecruitedCompanionDeath(__instance, companion);
                    int state = GetFieldInt(companion, "State", -1);
                    switch (state)
                    {
                        case 0: InvokeInstance(__instance, "WaitToJoin", companion, player); break;
                        case 1: InvokeInstance(__instance, "Ride", companion, player); break;
                        case 2: InvokeInstance(__instance, "GoingHome", companion, player); break;
                    }
                    MaintainExtensionCompanionDamageability(companion, player);
                }
            }
            catch (Exception ex) { Log("Companion cycle error: " + Unwrap(ex)); }
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
                    return;
                }

                // Recovery path for records created by older builds: associate the live ped
                // with its existing roster record without creating a duplicate entry.
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
            }
            catch (Exception ex) { Log("RecordAt roster migration warning: " + Unwrap(ex)); }
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
        private static void NormalizeRemovedWaitState(CompanionPersistentState state)
        {
            if (state == null || (state.FollowState != CompanionFollowState.Waiting &&
                state.FollowState != CompanionFollowState.ReturningToPosition))
                return;

            state.FollowState = CompanionFollowState.Following;
            if (state.SavedPosition != null)
                state.SavedPosition.HasValue = false;
            SavePersistentState(state);
        }

        private static void SetPersistentFollowState(object record, CompanionFollowState followState, Ped anchorPed)
        {
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null) return;
            if (followState == CompanionFollowState.Resting)
                followState = CompanionFollowState.Following;

            state.FollowState = followState;
            if (followState == CompanionFollowState.Waiting)
            {
                Ped anchor = anchorPed;
                if (anchor == null || !anchor.Exists()) anchor = Game.Player.Character;
                if (anchor != null && anchor.Exists())
                {
                    var pos = anchor.Position;
                    state.SavedPosition.HasValue = true;
                    state.SavedPosition.X = pos.X;
                    state.SavedPosition.Y = pos.Y;
                    state.SavedPosition.Z = pos.Z;
                    try { state.SavedPosition.Heading = anchor.Heading; } catch { state.SavedPosition.Heading = 0f; }
                }
            }
            else if (state.SavedPosition != null)
                state.SavedPosition.HasValue = false;
            SavePersistentState(state);
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

        private static void RegardOfRecordPostfix(object __0, ref int __result)
        {
            try
            {
                // enRAGE's native story relationship display calls RegardOfRecord(NemesisRecord).
                // Its stock implementation only recognizes vdl_ IDs, so extension recruits need
                // their persistent 0-100 affinity mapped to the native -100..100 regard scale.
                if (!IsRecruitedRosterRecord(__0)) return;
                CompanionPersistentState state = GetPersistentState(__0, true);
                if (state == null) return;
                __result = Math.Max(-100, Math.Min(100, state.Affinity * 2 - 100));
            }
            catch (Exception ex) { Log("Native story regard meter sync warning: " + Unwrap(ex)); }
        }

        // The original RosterMenu only renders its built-in "HACIA TI" meter inside the
        // NemesisRecord.Story branch. Recruits deliberately stay Story=false to avoid changing
        // enRAGE's story lifecycle, even when the VDL view contains them. Widen only the display
        // condition for those VDL recruit sheets so the existing native meter and drawing code run.
        private static IEnumerable<CodeInstruction> DrawDetailAffinityTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();
            try
            {
                Type recordType = _mainType == null ? null : _mainType.Assembly.GetType("NemesisSystem.NemesisRecord", false);
                PropertyInfo storyProperty = recordType == null ? null : recordType.GetProperty("Story", InstanceFlags);
                MethodInfo storyGetter = storyProperty == null ? null : storyProperty.GetGetMethod(true);
                MethodInfo replacement = typeof(EnRAGEHooks).GetMethod(
                    nameof(ShouldDrawNativeStoryAffinitySection), BindingFlags.Static | BindingFlags.NonPublic);
                bool patched = false;

                if (storyGetter != null && replacement != null)
                {
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if ((codes[i].opcode == OpCodes.Callvirt || codes[i].opcode == OpCodes.Call) &&
                            codes[i].operand is MethodInfo called && called == storyGetter)
                        {
                            CodeInstruction instruction = new CodeInstruction(OpCodes.Call, replacement);
                            instruction.labels.AddRange(codes[i].labels);
                            instruction.blocks.AddRange(codes[i].blocks);
                            codes[i] = instruction;
                            patched = true;
                            break;
                        }
                    }
                }

                Log(patched
                    ? "RosterMenu.DrawDetail now uses enRAGE's native regard meter for VDL recruits."
                    : "WARNING: could not locate the Story getter in RosterMenu.DrawDetail; native regard meter display was not patched.");
            }
            catch (Exception ex)
            {
                Log("Native regard meter transpiler failed: " + Unwrap(ex));
            }
            return codes;
        }

        private static bool ShouldDrawNativeStoryAffinitySection(object record)
        {
            try
            {
                if (record == null) return false;
                if (GetMember(record, "Story") is bool story && story) return true;
                if (!IsRecruitedRosterRecord(record) || !UsesVanDerLindeStoryRoster()) return false;
                return string.Equals(GetMember(record, "Gang") as string, "vdl", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log("Native regard section eligibility check failed: " + Unwrap(ex));
                return false;
            }
        }

        // In the native detail view the explanatory line calls RegardEarned with Id.Substring(4).
        // For extension IDs, map that synthetic key to the persistent affinity so the line stays
        // consistent with the native meter instead of displaying the unrelated 0 + 0 story values.
        private static void RegardEarnedPostfix(string __0, ref int __result)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(__0))
                    return;

                // DrawDetail passes Id.Substring(4): extcomp_foo becomes "omp_foo",
                // and the legacy extvdl_foo format becomes "dl_foo".
                string recordId;
                if (__0.StartsWith("omp_", StringComparison.OrdinalIgnoreCase))
                    recordId = "extc" + __0;
                else if (__0.StartsWith("dl_", StringComparison.OrdinalIgnoreCase))
                    recordId = "extv" + __0;
                else
                    return;

                CompanionStateStore store = CompanionStateRuntime.Store;
                CompanionPersistentState state;
                if (store != null && store.TryGet(recordId, out state) && state != null)
                    __result = Math.Max(-100, Math.Min(100, state.Affinity * 2 - 100));
            }
            catch (Exception ex) { Log("Native regard breakdown sync warning: " + Unwrap(ex)); }
        }

        private static void AwardAffinity(object record, int gain, string reason)
        {
            if (!_enableAffinity || gain <= 0 || record == null) return;
            int affinity = ChangeAffinity(record, gain);
            if (affinity >= 0)
                Log("Affinity +" + gain + " (" + reason + ") for " +
                    ((GetMember(record, "Name") as string) ?? (GetMember(record, "Id") as string)) + "; now " + affinity + "/100.");
        }

        // Observe the game's existing lock-on interaction controls; no extension-owned UI is added.
        // INPUT_INTERACT_LOCKON_POS = Greet, INPUT_INTERACT_LOCKON_NEG = Antagonize.
        // Observe RDR2's existing lock-on controls; this does not create its own interaction UI.
        // If melee is pressed without a retained lock-on target, associate it only with a very
        // nearby cached recruit and wait for the native shove signal before changing affinity.
        private static void UpdateNaturalAffinityInteractions(object instance, Ped player)
        {
            if (!_enableAffinity || instance == null || player == null || !player.Exists())
                return;

            try
            {
                int now = Game.GameTime;
                bool greeted = PAD.IS_CONTROL_JUST_PRESSED(0, unchecked((int)0xF63A17F9));
                bool antagonized = PAD.IS_CONTROL_JUST_PRESSED(0, unchecked((int)0x26A18F47));
                bool meleePressed = PAD.IS_CONTROL_JUST_PRESSED(0, unchecked((int)0xB2F377E8));

                Ped aimedTarget = FindAimedRecruitedPed(instance, player);
                Ped target = aimedTarget;
                if ((target == null || !target.Exists()) && meleePressed)
                    target = FindNearestTrackedRecruit(player, 3.0f);

                if (target != null && target.Exists())
                {
                    CachedRosterRecord cached;
                    if (RosterRecordsByPedHandle.TryGetValue(target.Handle, out cached) &&
                        cached != null && cached.Record != null &&
                        IsRecruitedRosterRecord(cached.Record) && !IsExiledRosterRecord(cached.Record))
                    {
                        object record = cached.Record;
                        string id = GetMember(record, "Id") as string;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            // Greet/antagonize only count when RDR2's lock-on system identifies this
                            // recruit. A nearby NPC is a fallback only for physical interactions.
                            if (aimedTarget != null && greeted)
                                ApplyNaturalAffinityChange(record, id, "greet", "saludar", 2, 1500);
                            if (aimedTarget != null && antagonized)
                                ApplyNaturalAffinityChange(record, id, "antagonize", "provocar", -3, 1500);

                            if (meleePressed)
                            {
                                LastPlayerMeleeActionAtById[id] = now;
                                LastAffinityObservedHealthById[id] = target.Health;
                            }

                            int previousHealth;
                            bool hasPreviousHealth = LastAffinityObservedHealthById.TryGetValue(id, out previousHealth);
                            int currentHealth = target.Health;
                            if (hasPreviousHealth && currentHealth < previousHealth)
                            {
                                int lastMelee;
                                bool meleeWasRecent = LastPlayerMeleeActionAtById.TryGetValue(id, out lastMelee) &&
                                    now >= lastMelee && now - lastMelee <= 1800;
                                bool playerShotRecently = now >= _lastPlayerWeaponFireAt &&
                                    now - _lastPlayerWeaponFireAt <= 1800;

                                if (meleeWasRecent)
                                {
                                    ApplyNaturalAffinityChange(record, id, "physical", "agresión física", -5, 2500);
                                    LastPlayerMeleeActionAtById.Remove(id);
                                }
                                else if (aimedTarget != null && playerShotRecently)
                                {
                                    // This is a short-window heuristic because the game API exposed
                                    // here does not attribute every health delta to its exact source.
                                    ApplyNaturalAffinityChange(record, id, "physical", "daño tras disparo del jugador", -8, 2500);
                                }
                            }
                            LastAffinityObservedHealthById[id] = currentHealth;
                        }
                    }
                }

                ApplyPendingShoveAffinity(player, now);
            }
            catch (Exception ex)
            {
                Log("Natural affinity interaction check warning: " + Unwrap(ex));
            }
        }

        private static Ped FindNearestTrackedRecruit(Ped player, float radius)
        {
            if (player == null || !player.Exists()) return null;
            Ped closest = null;
            float bestDistance = radius;

            foreach (KeyValuePair<int, CachedRosterRecord> pair in RosterRecordsByPedHandle.ToArray())
            {
                CachedRosterRecord cached = pair.Value;
                if (cached == null || cached.Record == null || cached.Ped == null ||
                    IsExiledRosterRecord(cached.Record) || !IsCachedRecruitedPed(pair.Key))
                    continue;

                try
                {
                    float distance = cached.Ped.Position.DistanceTo(player.Position);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        closest = cached.Ped;
                    }
                }
                catch { }
            }
            return closest;
        }

        private static void ApplyPendingShoveAffinity(Ped player, int now)
        {
            if (player == null || !player.Exists() || LastPlayerMeleeActionAtById.Count == 0)
                return;

            foreach (KeyValuePair<string, int> pending in LastPlayerMeleeActionAtById.ToArray())
            {
                string id = pending.Key;
                int actionAt = pending.Value;
                if (now < actionAt || now - actionAt > 1800)
                {
                    LastPlayerMeleeActionAtById.Remove(id);
                    continue;
                }

                CachedRosterRecord tracked = RosterRecordsByPedHandle.Values.FirstOrDefault(cached =>
                    cached != null && cached.Record != null && cached.Ped != null &&
                    string.Equals(GetMember(cached.Record, "Id") as string, id, StringComparison.OrdinalIgnoreCase));
                if (tracked == null || tracked.Ped == null || !tracked.Ped.Exists() || tracked.Ped.IsDead ||
                    IsExiledRosterRecord(tracked.Record))
                    continue;

                try
                {
                    if (tracked.Ped.Position.DistanceTo(player.Position) > 4.0f)
                        continue;

                    // PED::_HAS_PED_BEEN_SHOVED_RECENTLY(Ped, ms).
                    bool shoved = Function.Call<bool>(0x29FCE825613FEFCAUL,
                        new InputArgument[] { tracked.Ped.Handle, 1400 });
                    if (!shoved)
                        continue;

                    ApplyNaturalAffinityChange(tracked.Record, id, "physical", "empujón", -5, 2500);
                    LastPlayerMeleeActionAtById.Remove(id);
                }
                catch (Exception ex)
                {
                    Log("Natural shove affinity check warning: " + Unwrap(ex));
                }
            }
        }

        private static void ApplyNaturalAffinityChange(object record, string id, string actionKey,
            string reason, int delta, int cooldownMs)
        {
            if (!_enableAffinity || record == null || string.IsNullOrWhiteSpace(id) || delta == 0)
                return;

            string key = id + "|" + actionKey;
            int now = Game.GameTime;
            int previous;
            if (LastNaturalAffinityActionAt.TryGetValue(key, out previous) &&
                now >= previous && now - previous < cooldownMs)
                return;

            LastNaturalAffinityActionAt[key] = now;
            int affinity = ChangeAffinity(record, delta);
            if (affinity >= 0)
                Log("Affinity " + (delta > 0 ? "+" : "") + delta + " (" + reason + ") for " +
                    ((GetMember(record, "Name") as string) ?? id) + "; now " + affinity + "/100.");
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
            string name = GetMember(record, "Name") as string ?? T("companion.default");
            InvokeInstance(instance, "ShowSubtitle", TF("affinity.status", name, affinity));
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

                string call = T("action.call"), kick = T("action.kick");
                string confirm = T("action.kick.confirm"), cancel = T("action.kick.cancel");
                if (IsRosterRecordIncapacitated(record))
                {
                    __result.RemoveAll(action => action != null && action != "Atrás" && action != "Back" && action != "Volver" && action != "Cerrar" && action != "Close");
                    if (!string.IsNullOrWhiteSpace(_pendingKickRosterRecordId) &&
                        string.Equals(_pendingKickRosterRecordId, id, StringComparison.OrdinalIgnoreCase))
                        _pendingKickRosterRecordId = null;
                    return;
                }
                if (ExiledRosterIds.Contains(id))
                {
                    __result.Remove("Llamarlo"); __result.Remove(call);
                    __result.Remove("Echar de la banda"); __result.Remove(kick);
                    __result.Remove("Confirmar expulsión"); __result.Remove(confirm);
                    __result.Remove("Cancelar expulsión"); __result.Remove(cancel);
                    return;
                }

                bool recruitedRecord = IsRecruitedRosterRecord(record);
                bool alreadyFollowing = recruitedRecord && IsRecruitedRecordAlreadyFollowing(_latestMainInstance, record);
                bool anyCompanionFollowing = IsRosterRecordCurrentlyFollowing(_latestMainInstance, record);
                if (recruitedRecord)
                {
                    // Affinity is shown through enRAGE's native RegardOfRecord relationship meter;
                    // do not add a second, extension-owned affinity UI to the detail actions.
                    __result.RemoveAll(action => action != null &&
                        (action == "Mejorar afinidad (+10)" || action == "Reducir afinidad (-10)" ||
                         action.IndexOf("afinidad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         action.IndexOf("affinity", StringComparison.OrdinalIgnoreCase) >= 0));
                    __result.Remove("Llamarlo"); __result.Remove(call);
                    if (!alreadyFollowing && (!(GetMember(record, "IsAlive") is bool alive) || alive))
                        InsertBeforeBack(__result, call);
                }

                if (anyCompanionFollowing)
                {
                    __result.Remove("Echar de la banda"); __result.Remove(kick);
                    __result.Remove("Confirmar expulsión"); __result.Remove(confirm);
                    __result.Remove("Cancelar expulsión"); __result.Remove(cancel);
                    return;
                }
                if (!IsKickableRosterRecord(_latestMainInstance, record)) return;
                if (string.Equals(_pendingKickRosterRecordId, id, StringComparison.OrdinalIgnoreCase))
                {
                    int index = __result.IndexOf("Echar de la banda");
                    if (index < 0) index = __result.IndexOf(kick);
                    if (index >= 0) __result[index] = confirm;
                    else InsertBeforeBack(__result, confirm);
                    InsertBeforeBack(__result, cancel);
                }
                else
                {
                    __result.Remove("Confirmar expulsión"); __result.Remove(confirm);
                    __result.Remove("Cancelar expulsión"); __result.Remove(cancel);
                    __result.Remove(kick);
                    InsertBeforeBack(__result, kick);
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
                bool navigationAction = action == "Atrás" || action == "Back" || action == "Volver" ||
                    action == "Cerrar" || action == "Close";
                if (IsRosterRecordIncapacitated(record) && !navigationAction)
                {
                    _pendingKickRosterRecordId = null;
                    InvokeInstance(_latestMainInstance, "ShowSubtitle", T("incapacitated.only_help"));
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(_pendingKickRosterRecordId) &&
                    string.Equals(_pendingKickRosterRecordId, recordIdForConfirmation, StringComparison.OrdinalIgnoreCase) &&
                    action != T("action.kick.confirm") && action != T("action.kick.cancel") && action != T("action.kick"))
                {
                    // Leaving the confirmation row or choosing another action abandons the pending
                    // decision; reopening the same sheet must not silently preserve old confirmation.
                    _pendingKickRosterRecordId = null;
                }

                bool recruitedRecord = IsRecruitedRosterRecord(record);
                bool alreadyFollowing = recruitedRecord && IsRecruitedRecordAlreadyFollowing(_latestMainInstance, record);
                bool anyCompanionFollowing = IsRosterRecordCurrentlyFollowing(_latestMainInstance, record);
                if (action == T("action.call") && recruitedRecord)
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
                if (anyCompanionFollowing && (action == T("action.kick") || action == T("action.kick.confirm")))
                {
                    InvokeInstance(_latestMainInstance, "ShowSubtitle", "No puedes expulsar a un compañero que te está siguiendo. Despídelo primero.");
                    return false;
                }

                if (!IsKickableRosterRecord(_latestMainInstance, record)) return true;
                string id = GetMember(record, "Id") as string;
                if (action == T("action.kick"))
                {
                    _pendingKickRosterRecordId = id;
                    SetField(__instance, "_action", selected);
                    InvokeInstance(_latestMainInstance, "ShowSubtitle",
                        TF("subtitle.kick.warning", T("action.kick.confirm")));
                    Log("Requested confirmation before expelling story roster member " + id + ".");
                    return false;
                }
                if (action == T("action.kick.confirm"))
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
                if (action == T("action.kick.cancel"))
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
            string name = (GetMember(record, "FullName") as string) ?? (GetMember(record, "Name") as string) ?? T("companion.default");
            if (string.IsNullOrWhiteSpace(id) || !IsRecruitedRosterRecord(record)) return;
            if (IsExiledRosterRecord(record))
            {
                HideRecruitMedicinePrompt();
                InvokeInstance(instance, "ShowSubtitle", TF("medicine.exiled", name));
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
            if (targetedPed == null || !targetedPed.Exists() ||
                (targetedPed.IsDead && !IsPedIncapacitatedRecruit(targetedPed)) ||
                !RosterRecordsByPedHandle.TryGetValue(targetedPed.Handle, out targetedCache) ||
                targetedCache == null || targetedCache.Record == null ||
                !string.Equals(GetMember(targetedCache.Record, "Id") as string, id, StringComparison.OrdinalIgnoreCase))
            {
                InvokeInstance(instance, "ShowSubtitle", "No se pudo identificar al recluta apuntado; no se cobró el remedio.");
                return;
            }

            Ped ped = targetedPed;
            CompanionPersistentState preTreatmentState = GetPersistentState(record, false);
            bool injuryNeedsTreatment = preTreatmentState != null && preTreatmentState.Injury != null &&
                preTreatmentState.Injury.IsInjured;
            if (ped != null && ped.Health >= ped.MaxHealth && !injuryNeedsTreatment)
            {
                if (PendingFullHealthIds.Remove(id))
                    SavePersistentIdSet("EnRAGECompanionsExtension.pending-heals.txt", PendingFullHealthIds);
                InvokeInstance(instance, "ShowSubtitle", TF("medicine.full_health", name));
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
                InvokeInstance(instance, "ShowSubtitle", TF("medicine.insufficient_cash",
                    (_recruitMedicineCostCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture)));
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

            bool treatmentApplied = false;
            try { treatmentApplied = ApplyConfiguredMedicineHealing(ped); }
            catch (Exception ex) { Log("Medicine treatment failed for " + id + ": " + Unwrap(ex)); }
            if (!treatmentApplied)
            {
                bool refunded = TryRefundMedicineCharge(_recruitMedicineCostCents);
                InvokeInstance(instance, "ShowSubtitle", refunded
                    ? "El remedio no pudo aplicarse; se devolvió el cobro."
                    : "El remedio no pudo aplicarse y no se pudo confirmar la devolución. Revisa el registro del mod.");
                if (!refunded)
                    Log("CRITICAL: medicine treatment failed and refund could not be confirmed for " + id + ".");
                return;
            }

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
            InvokeInstance(instance, "ShowSubtitle", TF("medicine.success", name, _medicineHealingPercent, (_recruitMedicineCostCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture)));
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

        private static bool ApplyConfiguredMedicineHealing(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead) return false;

            try
            {
                int maxHealth = ped.MaxHealth;
                if (maxHealth <= 0) return false;

                int previousHealth = ped.Health;
                int amount = Math.Max(1, (int)Math.Ceiling(maxHealth * _medicineHealingPercent / 100.0));
                int targetHealth = Math.Min(maxHealth, Math.Max(1, previousHealth) + amount);
                if (targetHealth > previousHealth)
                    ped.Health = targetHealth;

                // Do not report a successful treatment just because the setter did not throw.
                // The native ped must still exist, be alive, and expose the requested health.
                return ped.Exists() && !ped.IsDead && ped.Health >= targetHealth && ped.Health > 0;
            }
            catch (Exception ex)
            {
                Log("Could not apply or verify companion medicine: " + Unwrap(ex));
                return false;
            }
        }

        private static bool TryRefundMedicineCharge(int amountCents)
        {
            if (amountCents <= 0) return true;
            try
            {
                // Signature confirmed in the bundled enRAGE decompilation: cents plus an add-reason hash.
                MONEY._MONEY_INCREMENT_CASH_BALANCE(amountCents, MISC.GET_HASH_KEY("ADD_REASON_DEFAULT"));
                return true;
            }
            catch (Exception ex)
            {
                Log("Medicine refund failed: " + Unwrap(ex));
                return false;
            }
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
            HideIndividualOrderPrompts();
            HideIncapacitatedPrompt();
            HideRecruitMedicinePrompt();
            HideCombatStancePrompt();
            SetPersistentFollowState(record, CompanionFollowState.Dismissed, null);
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
                InvokeInstance(instance, "ShowSubtitle", TF("kick.success", name));
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
            string name = (GetMember(record, "FullName") as string) ?? (GetMember(record, "Name") as string) ?? T("companion.default");
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
                InvokeInstance(instance, "ShowSubtitle", TF("roster.dead", name));
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
                    InvokeInstance(instance, "ShowSubtitle", TF("roster.already_following", name));
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
                    // Reuse the same pedestrian; do not create a clone or a second roster record.
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
                InvokeInstance(instance, "ShowSubtitle", existingPed != null ? TF("recruit.success.adopted", name) : TF("recruit.success.approaching", name));
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
            // Extension recruits with no recorded player kills must not inherit enRAGE's
            // GangMember hunting intent. Keep genuine kill history intact (checked above), but
            // normalize the extension-owned record's origin as well as disabling its hunt flag.
            changed |= SetEnumMember(record, "Origin", "Greeted");
            changed |= SetMember(record, "CanHunt", false);
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
                if (candidate == null || !candidate.Exists() || IsPedIncapacitatedRecruit(candidate))
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
                    string promptLabel = IsCachedRecruitedPed(candidate.Handle) ? T("action.call") : T("recruit.prompt.start");
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

        private static void HideIndividualOrderPrompt()
        {
            int prompt = _individualOrderPrompt;
            if (prompt != 0 && IsPromptValid(prompt))
                try { HUD._UI_PROMPT_DELETE(prompt); } catch { }
            _individualOrderPrompt = 0;
            _individualOrderPromptFor = 0;
            _individualOrderPromptGroup = 0;
            _individualOrderPromptArmed = false;
            _individualOrderPromptPed = null;
            _individualOrderPromptLabel = null;
        }
        private static void HideIndividualOrderPrompts()
        {
            HideIndividualOrderPrompt();
        }

        private static void UpdateIncapacitatedPrompt(object instance, Ped player)
        {
            try
            {
                if (player == null || !player.Exists())
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
                string targetId = GetMember(cached.Record, "Id") as string;
                int recoveryUntil;
                if (!string.IsNullOrWhiteSpace(targetId) &&
                    RecoveryAnimationUntilById.TryGetValue(targetId, out recoveryUntil) &&
                    Game.GameTime < recoveryUntil)
                {
                    HideIncapacitatedPrompt();
                    return;
                }
                bool down = IsNativeWoundedDown(instance, target) ||
                    (state != null && state.Injury != null && state.Injury.IsIncapacitated && !target.IsDead);
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
                        new object[] { control, T("incapacitated.prompt"), true, group }));
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
                    HelpIncapacitatedCompanion(instance, cached.Record, selected, player);
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

        private static bool HelpIncapacitatedCompanion(object instance, object record, Ped ped, Ped helperPed)
        {
            if (instance == null || record == null || ped == null || !ped.Exists() ||
                helperPed == null || !helperPed.Exists() || helperPed.Handle == ped.Handle ||
                IsExiledRosterRecord(record) ||
                (GetMember(record, "IsAlive") is bool alive && !alive))
                return false;

            CompanionPersistentState state = GetPersistentState(record, false);
            bool persistentlyDown = state != null && state.Injury != null && state.Injury.IsIncapacitated;
            bool nativeWoundedDown = IsNativeWoundedDown(instance, ped);
            if (!nativeWoundedDown && !(persistentlyDown && !ped.IsDead))
                return false;

            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return false;

            // Invoke the paired native only for enRAGE's actual wounded-down state. For
            // extension-simulated incapacitation, use a stand-up task instead. Never call
            // RESURRECT_PED: an ordinary death must not be brought back.
            bool nativeStarted = nativeWoundedDown &&
                TryInvokeNativeWrapper(typeof(PED), "_INCAPACITATED_REVIVE",
                    new object[] { ped.Handle, helperPed.Handle });
            if (!nativeStarted)
            {
                if (ped.IsDead)
                {
                    InvokeInstance(instance, "ShowSubtitle", T("incapacitated.revive_unavailable"));
                    Log("Native incapacitated revive unavailable for " + id + "; did not resurrect dead ped.");
                    return false;
                }

                bool stood = TryInvokeTaskNative("TASK_STAND_UP", new object[] { ped.Handle, true });
                if (!stood) stood = TryInvokeTaskNative("TASK_STAND_UP", new object[] { ped.Handle });
                if (!stood)
                {
                    InvokeInstance(instance, "ShowSubtitle", T("incapacitated.revive_unavailable"));
                    return false;
                }
            }

            int maxHealth = 0;
            try { maxHealth = ped.MaxHealth; } catch { }
            if (maxHealth <= 0) maxHealth = 100;
            int recoveryHealth = Math.Max(1, (int)Math.Ceiling(maxHealth * _incapacitatedHealthPercent / 100.0));
            if (!ped.IsDead)
            {
                try
                {
                    if (ped.Health < recoveryHealth) ped.Health = recoveryHealth;
                    if (!ped.Exists() || ped.IsDead || ped.Health <= 0)
                    {
                        InvokeInstance(instance, "ShowSubtitle", T("incapacitated.revive_unavailable"));
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log("Could not restore health while helping incapacitated companion: " + Unwrap(ex));
                    return false;
                }
            }
            else
            {
                // The paired native can complete asynchronously while the wrapper still reports
                // IsDead. Protect this short transition from death cleanup and movement-order tasks.
                RecoveryAnimationUntilById[id] = Game.GameTime + 3000;
                LastAutoAssistAtById[id] = Game.GameTime + 8000;
                object helperRecordPending = GetTrackedRecord(instance, helperPed.Handle);
                string helperPendingId = GetMember(helperRecordPending, "Id") as string;
                if (!string.IsNullOrWhiteSpace(helperPendingId))
                    RecoveryAnimationUntilById[helperPendingId] = Game.GameTime + 2200;
                return true;
            }

            try
            {
                if (!nativeStarted)
                {
                    if (!TryInvokeTaskNative("TASK_STAND_UP", new object[] { ped.Handle, true }))
                        TryInvokeTaskNative("TASK_STAND_STILL", new object[] { ped.Handle, 900 });
                }
            }
            catch { }

            if (state == null) state = GetPersistentState(record, true);
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
            LastObservedHealthById[id] = ped.Health;
            LastDamageBaselineById[id] = ped.Health;
            LastHealthSampleAtById[id] = Game.GameTime;
            LastDamageAtById.Remove(id);
            LastInjuryRecoveryAtById[id] = Game.GameTime;
            RecoveryAnimationUntilById[id] = Game.GameTime + 2200;
            LastAutoAssistAtById[id] = Game.GameTime + 8000;

            object helperRecord = GetTrackedRecord(instance, helperPed.Handle);
            string helperId = GetMember(helperRecord, "Id") as string;
            if (!string.IsNullOrWhiteSpace(helperId))
                RecoveryAnimationUntilById[helperId] = Game.GameTime + 2200;

            try { InvokeInstance(instance, "SaveRoster"); } catch { }
            InvokeInstance(instance, "ShowSubtitle", TF("incapacitated.helped", GetMember(record, "Name") as string ?? T("companion.default")));
            return true;
        }

        private static void TryAutoAssistIncapacitatedCompanions(object instance, Ped player, object[] companions)
        {
            if (instance == null || player == null || !player.Exists() || player.IsInCombat || companions == null ||
                companions.Length == 0)
                return;

            // The entire active group must be out of combat before companions attempt medical aid.
            foreach (object companion in companions)
            {
                string key = GetField(companion, "Key") as string;
                if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal) ||
                    GetFieldInt(companion, "State", 2) == 2)
                    continue;
                Ped memberPed = GetField(companion, "Ped") as Ped;
                if (memberPed == null || !memberPed.Exists()) memberPed = GetField(companion, "Real") as Ped;
                if (memberPed != null && memberPed.Exists() && !memberPed.IsDead && memberPed.IsInCombat)
                    return;
            }

            int now = Game.GameTime;
            foreach (object downedCompanion in companions)
            {
                string key = GetField(downedCompanion, "Key") as string;
                if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    continue;
                object record = GetField(downedCompanion, "Rec");
                if (record == null || IsExiledRosterRecord(record) ||
                    !(GetMember(record, "IsAlive") is bool alive) || !alive)
                    continue;

                Ped target = GetField(downedCompanion, "Ped") as Ped;
                if (target == null || !target.Exists()) target = GetField(downedCompanion, "Real") as Ped;
                if (target == null || !target.Exists()) continue;

                CompanionPersistentState targetState = GetPersistentState(record, false);
                bool downed = IsNativeWoundedDown(instance, target) ||
                    (targetState != null && targetState.Injury != null &&
                     targetState.Injury.IsIncapacitated && !target.IsDead);
                if (!downed) continue;

                string id = GetMember(record, "Id") as string;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (AutoAssistTargetByHelperId.Values.Any(targetId =>
                    string.Equals(targetId, id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                int nextAllowed;
                if (LastAutoAssistAtById.TryGetValue(id, out nextAllowed) && now < nextAllowed) continue;

                object bestHelper = null;
                Ped bestHelperPed = null;
                float bestDistance = 12.0f;
                foreach (object candidate in companions)
                {
                    if (object.ReferenceEquals(candidate, downedCompanion)) continue;
                    string helperKey = GetField(candidate, "Key") as string;
                    if (string.IsNullOrWhiteSpace(helperKey) || !helperKey.StartsWith(KeyPrefix, StringComparison.Ordinal) ||
                        GetFieldInt(candidate, "State", 2) == 2)
                        continue;
                    object helperRecord = GetField(candidate, "Rec");
                    string helperId = GetMember(helperRecord, "Id") as string;
                    if (string.IsNullOrWhiteSpace(helperId) || AutoAssistTargetByHelperId.ContainsKey(helperId) ||
                        helperRecord == null || IsExiledRosterRecord(helperRecord) ||
                        !(GetMember(helperRecord, "IsAlive") is bool helperAlive) || !helperAlive)
                        continue;
                    Ped helper = GetField(candidate, "Ped") as Ped;
                    if (helper == null || !helper.Exists()) helper = GetField(candidate, "Real") as Ped;
                    if (helper == null || !helper.Exists() || helper.IsDead || helper.IsInCombat || helper.IsOnMount ||
                        IsNativeWoundedDown(instance, helper) || IsRosterRecordIncapacitated(helperRecord))
                        continue;
                    float distance;
                    try { distance = target.Position.DistanceTo(helper.Position); }
                    catch { continue; }
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        bestHelper = candidate;
                        bestHelperPed = helper;
                    }
                }

                if (bestHelperPed == null) continue;
                LastAutoAssistAtById[id] = now + 10000;
                string selectedHelperId = GetMember(GetField(bestHelper, "Rec"), "Id") as string;
                if (bestDistance <= 2.35f)
                {
                    if (HelpIncapacitatedCompanion(instance, record, target, bestHelperPed))
                        Log("Nearby recruit started wound assistance for " + id + " using " +
                            (selectedHelperId ?? "another recruit") + ".");
                }
                else if (!string.IsNullOrWhiteSpace(selectedHelperId))
                {
                    AutoAssistTargetByHelperId[selectedHelperId] = id;
                    LastAutoAssistMoveAtByHelperId.Remove(selectedHelperId);
                    // If the helper is interrupted/cannot path, retry with another active recruit soon.
                    LastAutoAssistAtById[id] = now + 2500;
                    Log("Recruit " + selectedHelperId + " is approaching injured companion " + id + ".");
                }
            }
        }

        private static void UpdateCombatStancePrompt(object instance, Ped player)
        {
            try
            {
                if (player == null || !player.Exists())
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
                string label = TF("stance.prefix", CombatStanceLabel(state.CombatStance));
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
                case CompanionCombatStance.HoldPosition: return T("stance.hold");
                case CompanionCombatStance.CoverPlayer: return T("stance.cover");
                case CompanionCombatStance.DefendArea: return T("stance.defend");
                case CompanionCombatStance.FireOnSignal: return T("stance.fire");
                default: return T("stance.follow");
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
            InvokeInstance(instance, "ShowSubtitle", TF("stance.changed", GetMember(record, "Name") as string ?? T("companion.default"), CombatStanceLabel(state.CombatStance)));
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
                    string promptLabel = TF("prompt.medicine",
                        (_recruitMedicineCostCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture));
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

                object recruitedRecord = GetField(companion, "Rec");
                if (IsRosterRecordIncapacitated(recruitedRecord))
                {
                    InvokeInstance(__instance, "ShowSubtitle", T("incapacitated.only_help"));
                    return false;
                }

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
                    if (IsRosterRecordIncapacitated(GetField(existingCompanion, "Rec")))
                    {
                        InvokeInstance(__instance, "ShowSubtitle", T("incapacitated.only_help"));
                        return false;
                    }
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
                InvokeInstance(__instance, "ShowSubtitle", TF("recruit.faction_missing", _recruitGangId ?? "vdl"));
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
                // Both first-time recruitment and prompt-based recall must leave the persistent
                // individual order in Following; a dismissed state must not survive re-adoption.
                SetPersistentFollowState(rosterRecord ?? trackedRecord, CompanionFollowState.Following, null);
                ApplyRecruitedPedIdentity(__instance, rosterRecord ?? trackedRecord, ped);
                SetField(__instance, "_askPed", null);
                SetField(__instance, "_askKey", null);
                SetField(__instance, "_askComp", null);
                InvokeInstance(__instance, "HideAskPrompt");
                InvokeInstance(__instance, "ShowSubtitle", TF("companion.recruited", name));
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

            object record = GetField(companion, "Rec");
            CompanionPersistentState savedState = GetPersistentState(record, false);
            if (savedState != null && savedState.Injury != null && savedState.Injury.IsIncapacitated)
            {
                // An incapacitated recruit keeps the same roster identity. Do not let enRAGE's
                // missing/dead-ped recovery path spawn a fresh body and detach it from the band.
                SetField(companion, "State", 0);
                __result = false;
                return false;
            }

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
            if (playerFiring)
            {
                _lastPlayerWeaponFireAt = now;
                if (!string.IsNullOrWhiteSpace(stanceId))
                    LastPlayerFireAtById[stanceId] = now;
            }

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
                float radius = 16.0f;
                if ((float)Math.Sqrt(dx * dx + dy * dy + dz * dz) > radius)
                    threat = null;
            }

            if (stance == CompanionCombatStance.FireOnSignal)
            {
                int lastPlayerFire;
                bool recentPlayerFire = playerFiring ||
                    (now >= _lastPlayerWeaponFireAt && now - _lastPlayerWeaponFireAt <= 2500) ||
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
            // GangMember explicitly maps to Intent.Hunts in enRAGE, so it is not a neutral origin.
            // Greeted plus CanHunt=false prevents both a fabricated kill-history marker and "te da caza".
            SetEnumMember(created, "Origin", "Greeted");
            SetMember(created, "CanHunt", false);
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
                    if (!LastDamageBaselineById.ContainsKey(id))
                        LastDamageBaselineById[id] = ped.Health;
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
                    if (!ped.Exists() || IsExiledRosterRecord(cached.Record)) continue;
                    string id = GetMember(cached.Record, "Id") as string;
                    if (!ped.IsDead && !ped.IsIncapacitated) continue;

                    int health = 0;
                    try { health = ped.Health; } catch { }
                    int previousHealth;
                    if (!string.IsNullOrWhiteSpace(id) &&
                        LastObservedHealthById.TryGetValue(id, out previousHealth) && health < previousHealth)
                        LastDamageAtById[id] = Game.GameTime;

                    int recoveryUntil;
                    if (!string.IsNullOrWhiteSpace(id) &&
                        RecoveryAnimationUntilById.TryGetValue(id, out recoveryUntil) &&
                        Game.GameTime < recoveryUntil)
                        continue;

                    // Only enRAGE's native WoundedDown state (or the extension's recent-hit
                    // threshold for living peds) is recoverable. A generic dead ped stays dead.
                    if (IsLikelyIncapacitated(instance, cached.Record, ped, id))
                    {
                        SetMember(cached.Record, "IsAlive", true);
                        SetMember(cached.Record, "PresumedDead", false);
                        TryEnterIncapacitated(instance, cached.Record, ped);
                        continue;
                    }

                    if (!ped.IsDead) continue;
                    CompanionPersistentState staleInjury = GetPersistentState(cached.Record, false);
                    if (staleInjury != null && staleInjury.Injury != null && staleInjury.Injury.IsIncapacitated)
                    {
                        staleInjury.Injury.IsIncapacitated = false;
                        staleInjury.Injury.IncapacitatedSinceUtcTicks = 0;
                        SavePersistentState(staleInjury);
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
                        LastObservedHealthById.Remove(id);
                        LastDamageBaselineById.Remove(id);
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
            if (IsRosterRecordIncapacitated(GetField(companion, "Rec")))
            {
                InvokeInstance(instance, "ShowSubtitle", T("incapacitated.only_help"));
                return;
            }
            string name = GetField(companion, "Name") as string ?? "Tu aliado";
            string key = GetField(companion, "Key") as string;
            Ped ped = GetField(companion, "Ped") as Ped;
            if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
            bool preserveHorse = false;
            Ped horse = GetField(companion, "Horse") as Ped;
            Ped player = Game.Player.Character;
            try { preserveHorse = ped != null && ped.Exists() && ped.IsOnMount && horse != null && horse.Exists(); } catch { }
            SetPersistentFollowState(GetField(companion, "Rec"), CompanionFollowState.Dismissed, null);
            string dismissedId = GetMember(GetField(companion, "Rec"), "Id") as string ?? string.Empty;
            AutoAssistTargetByHelperId.Remove(dismissedId);
            LastAutoAssistMoveAtByHelperId.Remove(dismissedId);
            LastObservedHealthById.Remove(dismissedId);
            LastDamageBaselineById.Remove(dismissedId);
            LastDamageAtById.Remove(dismissedId);
            LastHealthSampleAtById.Remove(dismissedId);
            LastInjuryRecoveryAtById.Remove(dismissedId);

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
            InvokeInstance(instance, "ShowSubtitle", TF("companion.dismissed", name));
            Log("Dismissed extension companion " + name + "; pedestrian preserved and ambient AI resumed.");
        }
        private static void MaintainExtensionCompanionDamageability(object companion, Ped player)
        {
            if (companion == null) return;
            string key = GetField(companion, "Key") as string;
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return;
            Ped ped = GetField(companion, "Ped") as Ped;
            if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
            if (ped == null || !ped.Exists() || ped.IsDead) return;

            int state = GetFieldInt(companion, "State", -1), health = 0, maxHealth = 1;
            try { health = ped.Health; } catch { }
            try { maxHealth = Math.Max(1, ped.MaxHealth); } catch { }
            CompanionPersistentState persistent = GetPersistentState(GetField(companion, "Rec"), false);
            bool isPersistentlyIncapacitated = persistent != null && persistent.Injury != null &&
                persistent.Injury.IsIncapacitated;
            if (isPersistentlyIncapacitated)
            {
                // Keep an incapacitated but still-living ped from taking a lethal follow-up hit.
                try { ENTITY.SET_ENTITY_INVINCIBLE(ped.Handle, true); } catch { }
                return;
            }
            try { ENTITY.SET_ENTITY_INVINCIBLE(ped.Handle, false); } catch { }
            if (state == 2 && health > 0 &&
                health < (int)Math.Ceiling(maxHealth * 0.30))
            {
                try { TASK.CLEAR_PED_TASKS(ped.Handle, true, false); } catch { }
                try
                {
                    if (player != null && player.Exists())
                    {
                        int group = PED.GET_PED_GROUP_INDEX(player.Handle);
                        if (group > 0 && !PED.IS_PED_GROUP_MEMBER(ped.Handle, group, false))
                            PED.SET_PED_AS_GROUP_MEMBER(ped.Handle, group);
                    }
                }
                catch { }
                SetField(companion, "State", 0);
                SetField(companion, "AskedAt", Game.GameTime);
                try { ped.BlockPermanentEvents = false; } catch { }
            }
        }

        private static bool MaintainCompanionCondition(object instance, object companion, Ped player)
        {
            if (companion == null) return false;
            string key = GetField(companion, "Key") as string;
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
            object record = GetField(companion, "Rec");
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (record == null || IsExiledRosterRecord(record) ||
                (GetMember(record, "IsAlive") is bool alive && !alive))
            {
                AutoAssistTargetByHelperId.Remove(id);
                LastAutoAssistMoveAtByHelperId.Remove(id);
                return false;
            }
            Ped ped = GetField(companion, "Ped") as Ped;
            if (ped == null || !ped.Exists()) ped = GetField(companion, "Real") as Ped;
            if (ped == null || !ped.Exists()) return false;

            // An assigned helper temporarily overrides its normal follow/wait task so it can
            // approach the downed recruit. The paired recovery starts only once they're close.
            string assignedTargetId;
            if (AutoAssistTargetByHelperId.TryGetValue(id, out assignedTargetId))
            {
                bool cancelAssist = player == null || !player.Exists() || player.IsInCombat ||
                    ped.IsDead || ped.IsInCombat || ped.IsOnMount || GetFieldInt(companion, "State", 2) == 2;
                object targetCompanion = cancelAssist ? null : FindActiveExtensionCompanion(instance, assignedTargetId);
                object targetRecord = targetCompanion == null ? null : GetField(targetCompanion, "Rec");
                Ped targetPed = targetCompanion == null ? null : GetField(targetCompanion, "Ped") as Ped;
                if (targetPed == null || !targetPed.Exists())
                    targetPed = targetCompanion == null ? null : GetField(targetCompanion, "Real") as Ped;

                if (targetRecord == null || targetPed == null || !targetPed.Exists() ||
                    IsExiledRosterRecord(targetRecord) ||
                    (!(GetMember(targetRecord, "IsAlive") is bool targetAlive) || !targetAlive))
                    cancelAssist = true;

                int targetRecoveryUntil;
                if (!cancelAssist &&
                    RecoveryAnimationUntilById.TryGetValue(assignedTargetId, out targetRecoveryUntil) &&
                    Game.GameTime < targetRecoveryUntil)
                    cancelAssist = true;

                bool targetDown = false;
                if (!cancelAssist)
                {
                    CompanionPersistentState targetState = GetPersistentState(targetRecord, false);
                    targetDown = IsNativeWoundedDown(instance, targetPed) ||
                        (targetState != null && targetState.Injury != null &&
                         targetState.Injury.IsIncapacitated && !targetPed.IsDead);
                    if (!targetDown) cancelAssist = true;
                }

                if (cancelAssist)
                {
                    AutoAssistTargetByHelperId.Remove(id);
                    LastAutoAssistMoveAtByHelperId.Remove(id);
                }
                else
                {
                    float distance;
                    try { distance = targetPed.Position.DistanceTo(ped.Position); }
                    catch { distance = float.MaxValue; }

                    if (distance <= 2.35f)
                    {
                        AutoAssistTargetByHelperId.Remove(id);
                        LastAutoAssistMoveAtByHelperId.Remove(id);
                        if (HelpIncapacitatedCompanion(instance, targetRecord, targetPed, ped))
                            return true;
                    }
                    else
                    {
                        int nextMoveAt;
                        if (!LastAutoAssistMoveAtByHelperId.TryGetValue(id, out nextMoveAt) ||
                            Game.GameTime >= nextMoveAt)
                        {
                            bool moving = TryInvokeTaskNative("TASK_GO_STRAIGHT_TO_COORD",
                                new object[] { ped.Handle, targetPed.Position.X, targetPed.Position.Y,
                                    targetPed.Position.Z, 1.0f, 2000, targetPed.Heading, 1.0f });
                            if (moving)
                                LastAutoAssistMoveAtByHelperId[id] = Game.GameTime + 1100;
                            else
                            {
                                AutoAssistTargetByHelperId.Remove(id);
                                LastAutoAssistMoveAtByHelperId.Remove(id);
                                Log("Could not route recruit " + id + " to assist " + assignedTargetId + ".");
                            }
                        }
                        if (AutoAssistTargetByHelperId.ContainsKey(id)) return true;
                    }
                }
            }

            int recoveryUntil;
            if (!string.IsNullOrWhiteSpace(id) && RecoveryAnimationUntilById.TryGetValue(id, out recoveryUntil))
            {
                if (Game.GameTime < recoveryUntil) return true;
                RecoveryAnimationUntilById.Remove(id);
            }

            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null || state.Injury == null) return false;
            int now = Game.GameTime, health = 0, maxHealth = 1;
            try { health = ped.Health; } catch { }
            try { maxHealth = Math.Max(1, ped.MaxHealth); } catch { maxHealth = 1; }

            if (state.Injury.IsIncapacitated)
            {
                int recoveredHealth = Math.Max(1, (int)Math.Ceiling(maxHealth * _incapacitatedHealthPercent / 100.0));
                bool nativeDown = IsNativeWoundedDown(instance, ped);
                bool stillDown = ped.IsDead || nativeDown || ped.IsIncapacitated || ped.IsRagdoll;
                if (!stillDown && health >= recoveredHealth)
                {
                    // _INCAPACITATED_REVIVE has completed the native recovery animation.
                    state.Injury.IsIncapacitated = false;
                    state.Injury.IncapacitatedSinceUtcTicks = 0;
                    state.Injury.IsInjured = state.Injury.Severity > 0 || state.Injury.RecoverySecondsRemaining > 0;
                    SavePersistentState(state);
                    SetMember(record, "IsAlive", true);
                    SetMember(record, "PresumedDead", false);
                    try { ENTITY.SET_ENTITY_INVINCIBLE(ped.Handle, false); } catch { }
                    LastObservedHealthById[id] = health;
                    LastDamageBaselineById[id] = health;
                    LastHealthSampleAtById[id] = now;
                    LastDamageAtById.Remove(id);
                    LastInjuryRecoveryAtById[id] = now;
                    try { InvokeInstance(instance, "SaveRoster"); } catch { }
                }
                else
                {
                    // Do not undo the protection installed for a downed recruit.
                    try { ENTITY.SET_ENTITY_INVINCIBLE(ped.Handle, true); } catch { }
                    return true;
                }
            }

            // enRAGE's Adopt marks real companions invincible. Re-enable damage before sampling.
            try { ENTITY.SET_ENTITY_INVINCIBLE(ped.Handle, false); }
            catch (Exception ex) { Log("Could not restore recruit damageability: " + Unwrap(ex)); }

            int previousHealth;
            bool hasPreviousHealth = LastObservedHealthById.TryGetValue(id, out previousHealth);
            if (hasPreviousHealth && health < previousHealth) LastDamageAtById[id] = now;
            int baseline;
            if (!LastDamageBaselineById.TryGetValue(id, out baseline))
            {
                baseline = health;
                LastDamageBaselineById[id] = health;
            }

            if (IsLikelyIncapacitated(instance, record, ped, id))
            {
                TryEnterIncapacitated(instance, record, ped);
                LastObservedHealthById[id] = Math.Max(1, health);
                LastHealthSampleAtById[id] = now;
                return true;
            }

            if (state.Injury.IsIncapacitated)
            {
                // Some API builds do not report IsRagdoll/IsIncapacitated while TASK_KNOCKED_OUT
                // is starting. Do not clear the persistent down state until the ped has recovered
                // at least the configured assistance health, or the player's help handler clears it.
                int recoveredHealth = Math.Max(1, (int)Math.Ceiling(maxHealth * _incapacitatedHealthPercent / 100.0));
                if (!ped.IsDead && !ped.IsIncapacitated && !ped.IsRagdoll && health >= recoveredHealth)
                {
                    state.Injury.IsIncapacitated = false;
                    state.Injury.IncapacitatedSinceUtcTicks = 0;
                    SavePersistentState(state);
                }
                else if (ped.IsDead)
                {
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

            if (ped.IsDead) return false;
            if (_enableInjurySystem)
            {
                // Accumulate small hits against a stable baseline. Comparing only to the previous
                // frame meant repeated sub-threshold hits were invisible to the injury system.
                if (health > baseline)
                    LastDamageBaselineById[id] = health;
                else if (health < baseline)
                {
                    int delta = baseline - health;
                    int threshold = Math.Max(1, (int)Math.Ceiling(maxHealth * _injuryDamageThresholdPercent / 100.0));
                    if (delta >= threshold)
                    {
                        bool wasInjured = state.Injury.IsInjured;
                        state.Injury.IsInjured = true;
                        state.Injury.Severity = Math.Max(state.Injury.Severity, Math.Max(1, Math.Min(100,
                            (int)Math.Ceiling(delta * 100.0 / maxHealth))));
                        if (state.Injury.FirstInjuredUtcTicks <= 0) state.Injury.FirstInjuredUtcTicks = DateTime.UtcNow.Ticks;
                        state.Injury.RecoverySecondsRemaining = Math.Max(state.Injury.RecoverySecondsRemaining, _injuryRecoverySeconds);
                        SavePersistentState(state);
                        LastDamageBaselineById[id] = health;
                        if (!wasInjured)
                            InvokeInstance(instance, "ShowSubtitle", TF("injury.detected", GetMember(record, "Name") as string ?? T("companion.default")));
                        Log("Tracked companion injury " + id + "; damage=" + delta + ", severity=" + state.Injury.Severity + ".");
                    }
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
            // Avoid writing the JSON every frame; persist the countdown at five-second intervals.
            int lastPersistAt;
            bool persistDue = !LastInjuryPersistAtById.TryGetValue(id, out lastPersistAt) ||
                now < lastPersistAt || now - lastPersistAt >= 5000;
            if (persistDue || recovered)
            {
                if (recovered)
                {
                    state.Injury.IsInjured = false;
                    state.Injury.Severity = 0;
                    state.Injury.FirstInjuredUtcTicks = 0;
                }
                SavePersistentState(state);
                LastInjuryPersistAtById[id] = now;
                if (recovered)
                    Log("Companion injury recovery completed for " + id + ".");
            }
        }
        private static bool IsLikelyIncapacitated(object instance, object record, Ped ped, string id)
        {
            if (ped == null || !ped.Exists()) return false;
            if (IsNativeWoundedDown(instance, ped)) return true;
            if (_woundedDown == null)
            {
                try { if (ped.IsIncapacitated) return true; } catch { }
            }
            // Some RDR2 ped types do not expose IsRagdoll reliably while going down.
            // A recently damaged recruit at or below 20% health should still enter the recoverable
            // incapacitated state; TryEnterIncapacitated applies the knocked-out task when possible.
            if (!ped.IsDead && _enableInjurySystem)
            {
                int lastDamage, maxHealth = 1, health = 0;
                try { maxHealth = Math.Max(1, ped.MaxHealth); } catch { }
                try { health = ped.Health; } catch { }
                int threshold = Math.Max(1, (int)Math.Ceiling(maxHealth * 0.20));
                if (health > 0 && health <= threshold && !string.IsNullOrWhiteSpace(id) &&
                    LastDamageAtById.TryGetValue(id, out lastDamage) && Game.GameTime >= lastDamage &&
                    Game.GameTime - lastDamage <= 10000)
                    return true;
            }
            CompanionPersistentState state = GetPersistentState(record, false);
            if (ped.IsDead)
            {
                // A dead ped is recoverable only while enRAGE still recognizes its native
                // wound-incapacitation state. Do not turn an ordinary death into a revive.
                if (_woundedDown == null)
                {
                    try { if (TryStaticBool("LooksKnockedOut", ped)) return true; } catch { }
                }
                return false;
            }
            if (state != null && state.Injury != null && state.Injury.IsIncapacitated &&
                (ped.IsIncapacitated || ped.IsRagdoll)) return true;
            return false;
        }

        private static bool IsNativeWoundedDown(object instance, Ped ped)
        {
            if (ped == null || !ped.Exists()) return false;
            if (_woundedDown != null)
            {
                object owner = instance ?? _latestMainInstance;
                if (owner == null) return false;
                try { return _woundedDown.Invoke(owner, new object[] { ped }) is bool down && down; }
                catch (Exception ex) { Log("Native WoundedDown check failed: " + Unwrap(ex)); return false; }
            }
            try
            {
                if (ped.IsIncapacitated && (!ped.IsDead || TryStaticBool("LooksKnockedOut", ped))) return true;
            }
            catch { }
            return false;
        }

        private static bool IsRosterRecordIncapacitated(object record)
        {
            if (record == null) return false;
            CompanionPersistentState state = GetPersistentState(record, false);
            return state != null && state.Injury != null && state.Injury.IsIncapacitated;
        }

        private static bool IsPedIncapacitatedRecruit(Ped ped)
        {
            if (ped == null || !ped.Exists()) return false;
            CachedRosterRecord cached;
            if (!RosterRecordsByPedHandle.TryGetValue(ped.Handle, out cached) || cached == null || cached.Record == null)
            {
                if (_woundedDown == null) try { return ped.IsIncapacitated; } catch { }
                return false;
            }
            if (IsNativeWoundedDown(_latestMainInstance, ped)) return true;
            CompanionPersistentState state = GetPersistentState(cached.Record, false);
            if (state != null && state.Injury != null && state.Injury.IsIncapacitated)
                return !ped.IsDead || IsNativeWoundedDown(_latestMainInstance, ped);
            if (_woundedDown == null) try { return ped.IsIncapacitated; } catch { }
            return false;
        }
        private static bool TryEnterIncapacitated(object instance, object record, Ped ped)
        {
            if (instance == null || record == null || ped == null || !ped.Exists() ||
                IsExiledRosterRecord(record) || (GetMember(record, "IsAlive") is bool alive && !alive)) return false;
            string id = GetMember(record, "Id") as string;
            if (string.IsNullOrWhiteSpace(id) || !IsRecruitedRosterRecord(record)) return false;
            CompanionPersistentState state = GetPersistentState(record, true);
            if (state == null || state.Injury == null) return false;

            bool wasIncapacitated = state.Injury.IsIncapacitated;
            bool wasInjured = state.Injury.IsInjured;
            int oldSeverity = state.Injury.Severity;
            double oldRecovery = state.Injury.RecoverySecondsRemaining;
            long oldFirstInjured = state.Injury.FirstInjuredUtcTicks;
            long oldSince = state.Injury.IncapacitatedSinceUtcTicks;
            bool rosterAlive = GetMember(record, "IsAlive") is bool aliveBefore && aliveBefore;
            bool presumedDead = GetMember(record, "PresumedDead") is bool wasPresumedDead && wasPresumedDead;

            state.Injury.IsIncapacitated = true;
            state.Injury.IsInjured = true;
            state.Injury.Severity = Math.Max(40, state.Injury.Severity);
            if (state.Injury.FirstInjuredUtcTicks <= 0) state.Injury.FirstInjuredUtcTicks = DateTime.UtcNow.Ticks;
            if (state.Injury.IncapacitatedSinceUtcTicks <= 0) state.Injury.IncapacitatedSinceUtcTicks = DateTime.UtcNow.Ticks;
            state.Injury.RecoverySecondsRemaining = Math.Max(state.Injury.RecoverySecondsRemaining, _injuryRecoverySeconds);
            SetMember(record, "IsAlive", true);
            SetMember(record, "PresumedDead", false);
            bool changed = !wasIncapacitated || !wasInjured || oldSeverity != state.Injury.Severity ||
                Math.Abs(oldRecovery - state.Injury.RecoverySecondsRemaining) > 0.01 ||
                oldFirstInjured != state.Injury.FirstInjuredUtcTicks || oldSince != state.Injury.IncapacitatedSinceUtcTicks ||
                !rosterAlive || presumedDead;

            if (!ped.IsDead && !wasIncapacitated && !IsNativeWoundedDown(instance, ped))
            {
                // Simulated extension incapacitation needs a knock-out task; when enRAGE has
                // already classified a real wound, preserve its native wounded-down animation.
                // Never call RESURRECT_PED here.
                TryInvokeTaskNative("TASK_KNOCKED_OUT", new object[] { ped.Handle, -1.0f, false });
            }

            if (changed)
            {
                SavePersistentState(state);
                try { InvokeInstance(instance, "SaveRoster"); } catch { }
            }
            if (!wasIncapacitated)
                InvokeInstance(instance, "ShowSubtitle", TF("incapacitated.entered", GetMember(record, "Name") as string ?? T("companion.default")));
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
                if (IsLikelyIncapacitated(instance, record, ped, recordId))
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
            if (instance == null) return null;
            if (string.Equals(name, "ShowSubtitle", StringComparison.OrdinalIgnoreCase) &&
                args != null && args.Length > 0 && args[0] is string subtitle)
                args[0] = TranslateExactUiText(subtitle);

            MethodInfo method = FindMethod(instance.GetType(), name, args == null ? 0 : args.Length,
                p => args == null || p.Length == args.Length);
            if (method == null) return null;
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


        private static Dictionary<string, string> CreateDefaultExtensionTexts()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "affinity.disabled", "La afinidad está desactivada en el archivo INI." },
                { "affinity.status", "{0}: afinidad {1}/100." },
                { "affinity.meter", "AFINIDAD {0}/100" },
                { "action.call", "Llamarlo" },
                { "action.dismiss", "Dejar de seguirme" },
                { "action.kick", "Echar de la banda" },
                { "action.kick.confirm", "Confirmar expulsión" },
                { "action.kick.cancel", "Cancelar expulsión" },
                { "action.affinity.increase", "Mejorar afinidad (+10)" },
                { "action.affinity.decrease", "Reducir afinidad (-10)" },
                { "subtitle.kick.cancelled", "Expulsión cancelada." },
                { "subtitle.already_following", "Ese miembro ya te está siguiendo." },
                { "subtitle.cannot_kick_following", "No puedes expulsar a un compañero que te está siguiendo. Despídelo primero." },
                { "subtitle.kick.warning", "Aviso: la expulsión es irreversible y podría tener consecuencias para ti en el futuro. Elige {0} para continuar." },
                { "subtitle.kick.invalid", "La confirmación ya no es válida. Vuelve a seleccionar la expulsión." },
                { "medicine.exiled", "{0} ya fue expulsado de la banda y no puede recibir remedios." },
                { "medicine.dead", "No puedes curar a un miembro fallecido del roster." },
                { "medicine.target.invalid", "No se pudo identificar al recluta apuntado; no se cobró el remedio." },
                { "medicine.full_health", "{0} ya tiene la salud al máximo y no hay ninguna herida pendiente." },
                { "medicine.cash.check_failed", "No se pudo comprobar el dinero disponible." },
                { "medicine.insufficient_cash", "Necesitas {0} dólares para comprar el remedio." },
                { "medicine.processing", "El remedio ya se está procesando; no se cobró otra vez." },
                { "medicine.charge_failed", "No se pudo confirmar el cobro; espera un momento antes de volver a intentarlo." },
                { "medicine.failed_refunded", "El remedio no pudo aplicarse; se devolvió el cobro." },
                { "medicine.failed_refund_failed", "El remedio no pudo aplicarse y no se pudo confirmar la devolución. Revisa el registro del mod." },
                { "medicine.success", "{0} recibió el remedio. Curación: {1}%. Coste: {2} dólares." },
                { "kick.success", "{0} ha sido expulsado de la banda. La decisión no se puede deshacer." },
                { "kick.failed", "No se pudo completar la expulsión. Revisa el registro de la extensión." },
                { "roster.model_missing", "No se pudo recuperar el modelo de este miembro." },
                { "roster.dead", "{0} figura como fallecido en el roster." },
                { "roster.already_following", "{0} ya te está siguiendo." },
                { "recruit.limit", "No puedes llevar más compañeros ahora." },
                { "recruit.male_only", "Solo puedes reclutar NPC masculinos." },
                { "recruit.faction_missing", "La facción configurada '{0}' no existe en enRAGE. Revisa RecruitGangId en el archivo INI." },
                { "recruit.exiled", "Este NPC fue expulsado permanentemente y no puede volver a ser reclutado." },
                { "recruit.prompt.start", "Cabalga conmigo" },
                { "recruit.success.adopted", "{0} ha vuelto y vuelve a seguirte." },
                { "recruit.success.approaching", "{0} fue llamado y se acerca para seguirte." },
                { "companion.default", "Tu compañero" },
                { "companion.ally", "Tu aliado" },
                { "companion.dismissed", "{0} deja de seguirte y vuelve a comportarse como un NPC del mundo." },
                { "companion.recruited", "{0} ahora combate de tu lado." },
                { "injury.detected", "{0} está herido. Un remedio ayudará a recuperarlo." },
                { "incapacitated.only_help", "Está incapacitado por una herida. Apúntale y mantén E para ayudarlo a levantarse." },
                { "incapacitated.prompt", "Ayudar a levantarse" },
                { "incapacitated.revive_unavailable", "No se pudo iniciar la recuperación por herida. El recluta sigue incapacitado." },
                { "incapacitated.health_failed", "No se pudo restaurar la salud; el compañero sigue incapacitado." },
                { "incapacitated.still_down", "El compañero todavía no puede levantarse; sigue incapacitado." },
                { "incapacitated.verify_failed", "No se pudo verificar la recuperación; el compañero sigue incapacitado." },
                { "incapacitated.entered", "{0} ha quedado incapacitado. Apúntale y mantén E para ayudarlo a levantarse." },
                { "incapacitated.helped", "{0} se ha levantado. La herida seguirá recuperándose con el tiempo." },
                { "stance.prefix", "Postura: {0}" },
                { "stance.hold", "Mantener posición" },
                { "stance.cover", "Cubrir al jugador" },
                { "stance.defend", "Defender la zona" },
                { "stance.fire", "Fuego a mi señal" },
                { "stance.follow", "Seguir órdenes" },
                { "stance.changed", "{0}: postura {1}." },
                { "prompt.medicine", "Dar remedio (${0})" }
            };
        }

        private static void LoadExtensionTexts()
        {
            ExtensionTexts.Clear();
            foreach (KeyValuePair<string, string> item in DefaultExtensionTexts)
                ExtensionTexts[item.Key] = item.Value;

            string path = ResolveScriptsFile("EnRAGECompanionsExtension.texts.ini");
            try
            {
                if (!File.Exists(path))
                {
                    List<string> initial = new List<string>
                    {
                        "; EnRAGE Companions Extension translations.",
                        "; Edit the value after '='; do not change the keys.",
                        "; Keep placeholders such as {0}, {1}, and {2} in formatted texts.",
                        ""
                    };
                    initial.AddRange(DefaultExtensionTexts.Select(item => item.Key + "=" + item.Value));
                    File.WriteAllLines(path, initial);
                    Log("Created editable extension text file: " + path);
                    return;
                }

                HashSet<string> foundKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;
                    string key = line.Substring(0, split).Trim();
                    if (!DefaultExtensionTexts.ContainsKey(key)) continue;
                    string value = line.Substring(split + 1).Trim();
                    if (value.Length == 0) continue;
                    ExtensionTexts[key] = value;
                    foundKeys.Add(key);
                }

                List<string> missing = DefaultExtensionTexts
                    .Where(item => !foundKeys.Contains(item.Key))
                    .Select(item => item.Key + "=" + item.Value)
                    .ToList();
                if (missing.Count > 0)
                {
                    List<string> additions = new List<string> { "", "; New extension text keys; translate the value after '='." };
                    additions.AddRange(missing);
                    File.AppendAllLines(path, additions);
                }
                Log("Loaded " + foundKeys.Count + " editable extension text override(s).");
            }
            catch (Exception ex)
            {
                Log("Translation file could not be loaded; built-in Spanish texts will be used. " + Unwrap(ex));
            }
        }

        private static string T(string key)
        {
            string value;
            if (ExtensionTexts.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
                return value;
            if (DefaultExtensionTexts.TryGetValue(key, out value))
                return value;
            return key;
        }

        private static string TranslateExactUiText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (KeyValuePair<string, string> item in DefaultExtensionTexts)
                if (string.Equals(item.Value, text, StringComparison.Ordinal))
                    return T(item.Key);
            return text;
        }

        private static string TF(string key, params object[] args)
        {
            string format = T(key);
            try { return string.Format(CultureInfo.InvariantCulture, format, args); }
            catch (FormatException)
            {
                try { return string.Format(CultureInfo.InvariantCulture, DefaultExtensionTexts[key], args); }
                catch { return format; }
            }
        }

        private static string ReadConfiguration()
        {
            string path = ResolveScriptsFile("EnRAGECompanionsExtension.ini");
            try
            {
                if (File.Exists(path))
                {
                    string[] existingLines = File.ReadAllLines(path);
                    // Remove only the obsolete, known extension-generated appearance settings.
                    // Their behavior was removed; leave all unrelated user configuration intact.
                    string[] cleanedLines = existingLines.Where(raw =>
                    {
                        string candidate = (raw ?? string.Empty).Trim();
                        if (candidate.Equals("; Capture/reapply outfit hash, standard clothing slots, hat/accessory props, and current equipped weapon hash when wrappers exist.", StringComparison.OrdinalIgnoreCase) ||
                            candidate.Equals("; Not full YMT XML/material persistence or a full weapon inventory snapshot.", StringComparison.OrdinalIgnoreCase))
                            return false;
                        int separator = candidate.IndexOf('=');
                        if (separator <= 0) return true;
                        string key = candidate.Substring(0, separator).Trim();
                        return !key.Equals("EnableAppearancePersistence", StringComparison.OrdinalIgnoreCase) &&
                               !key.Equals("PreserveSavedOutfit", StringComparison.OrdinalIgnoreCase) &&
                               !key.Equals("RestDurationSeconds", StringComparison.OrdinalIgnoreCase) &&
                               !key.Equals("RestHealsInjuries", StringComparison.OrdinalIgnoreCase) &&
                               !key.Equals("EnableIndividualOrders", StringComparison.OrdinalIgnoreCase) &&
                               !key.Equals("EnableWaitMode", StringComparison.OrdinalIgnoreCase) &&
                               !key.Equals("WaitRadius", StringComparison.OrdinalIgnoreCase);
                    }).ToArray();
                    if (cleanedLines.Length != existingLines.Length)
                    {
                        try
                        {
                            File.WriteAllLines(path, cleanedLines);
                            existingLines = cleanedLines;
                        }
                        catch (Exception ex)
                        {
                            // The old keys are ignored by the current parser even if this cleanup fails.
                            Log("Could not remove obsolete INI entries: " + Unwrap(ex));
                        }
                    }
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
                    if (!existingKeys.Contains("EnableInjurySystem")) missingSettings.Add("EnableInjurySystem=true");
                    if (!existingKeys.Contains("InjuryDamageThresholdPercent")) missingSettings.Add("InjuryDamageThresholdPercent=10");
                    if (!existingKeys.Contains("InjuryRecoverySeconds")) missingSettings.Add("InjuryRecoverySeconds=180");
                    if (!existingKeys.Contains("IncapacitatedHealthPercent")) missingSettings.Add("IncapacitatedHealthPercent=35");
                    if (!existingKeys.Contains("MedicineHealingPercent")) missingSettings.Add("MedicineHealingPercent=100");
                    if (!existingKeys.Contains("MedicineRecoveryReductionSeconds")) missingSettings.Add("MedicineRecoveryReductionSeconds=60");
                    if (!existingKeys.Contains("RecruitMedicineCostCents")) missingSettings.Add("RecruitMedicineCostCents=100");
                    if (missingSettings.Count > 0)
                    {
                        File.AppendAllText(path, Environment.NewLine + "; Added settings for affinity, injury, and medicine" + Environment.NewLine + string.Join(Environment.NewLine, missingSettings) + Environment.NewLine);
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
                        "EnableInjurySystem=true\r\n" +
                        "InjuryDamageThresholdPercent=10\r\n" +
                        "InjuryRecoverySeconds=180\r\n" +
                        "IncapacitatedHealthPercent=35\r\n" +
                        "MedicineHealingPercent=100\r\n" +
                        "MedicineRecoveryReductionSeconds=60\r\n" +
                        "RecruitMedicineCostCents=100\r\n" +
                        "\r\n");
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
