using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        private static MethodInfo _recordAt;
        private static MethodInfo _roleOf;
        private static bool _installed;
        private static bool _insideAskReentry;
        private static int _maxCompanions = 3;
        private static bool _keepPlayerSideAfterDismissal;
        private static string _persistentNemesisGangId = string.Empty;
        private static readonly Dictionary<string, uint> ModelHashes = new Dictionary<string, uint>();

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
                _recordAt = FindMethod(_mainType, "RecordAt", 1, p => p[0].ParameterType == typeof(int));
                _roleOf = FindMethod(_mainType, "RoleOf", 1, p => p[0].ParameterType == typeof(int));

                MethodInfo updateCompanion = FindMethod(_mainType, "UpdateCompanion", 1, p => IsPedType(p[0].ParameterType));
                MethodInfo ask = FindMethod(_mainType, "Ask", 3, p => IsPedType(p[0].ParameterType) && p[1].ParameterType == typeof(string) && IsPedType(p[2].ParameterType));
                MethodInfo spawnCompanion = FindMethod(_mainType, "SpawnCompanion", 2, p => p[0].ParameterType.Name == "Companion" && IsPedType(p[1].ParameterType));
                MethodInfo cleanup = FindMethod(_mainType, "Cleanup", 1, p => p[0].ParameterType.Name == "Companion");
                MethodInfo restoreReal = FindMethod(_mainType, "RestoreReal", 1, p => p[0].ParameterType.Name == "Companion");

                if (updateCompanion == null || _updateAsk == null || ask == null || spawnCompanion == null)
                {
                    Log("ERROR: Required enRAGE methods were not found. This build may have changed; no patches applied.");
                    _mainType = null;
                    return false;
                }

                ReadConfiguration();
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(updateCompanion,
                    prefix: Hook(nameof(CompanionCyclePrefix)),
                    postfix: Hook(nameof(CompanionCyclePostfix)));
                _harmony.Patch(_updateAsk, postfix: Hook(nameof(UpdateAskPostfix)));
                _harmony.Patch(ask, prefix: Hook(nameof(AskPrefix)));
                _harmony.Patch(spawnCompanion, prefix: Hook(nameof(SpawnCompanionPrefix)));

                if (cleanup != null)
                    _harmony.Patch(cleanup, postfix: Hook(nameof(CleanupPostfix)));
                if (restoreReal != null)
                    _harmony.Patch(restoreReal, postfix: Hook(nameof(RestoreRealPostfix)));

                _installed = true;
                Log("Installed. Target=" + _mainType.Assembly.GetName().Name + "; max companions=" + _maxCompanions +
                    "; keep side after dismissal=" + _keepPlayerSideAfterDismissal + ".");
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

        // The original enRAGE build uses this method to run a one-time shutdown of the old
        // companion system. Mark that one-time shutdown complete before its body runs, then
        // run the existing companion routines from its postfix.
        private static void CompanionCyclePrefix(object __instance)
        {
            SetField(__instance, "_companionsOffDone", true);
        }

        private static void CompanionCyclePostfix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0 || !(__args[0] is Ped player) || player == null || !player.Exists())
                    return;

                InvokeInstance(__instance, "UpdateAsk", player);

                IList list = GetField(__instance, "_comps") as IList;
                if (list == null || list.Count == 0)
                    return;

                // Iterate over a snapshot: Ride/GoingHome/Cleanup can remove entries from _comps.
                object[] snapshot = list.Cast<object>().ToArray();
                foreach (object companion in snapshot)
                {
                    if (companion == null)
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

        // Allow normal enRAGE prompts to take priority. If its scan did not select a character,
        // pick an eligible nearby ped, populate the same private fields, and re-enter UpdateAsk.
        // Re-entry is guarded so the postfix does not recursively select forever.
        private static void UpdateAskPostfix(object __instance, object[] __args)
        {
            if (_insideAskReentry || __args == null || __args.Length == 0 || !(__args[0] is Ped player))
                return;

            try
            {
                if (player == null || !player.Exists() || player.IsInCombat || player.IsOnMount || IsAskBlocked(__instance))
                    return;

                Ped current = GetField(__instance, "_askPed") as Ped;
                if (current != null && current.Exists() && current.Position.DistanceTo(player.Position) <= 3.5f)
                    return;

                // The game's original prompt handler leaves stale _askPed values after hiding the
                // prompt. Clear only the stale selection; normal valid story/companion selections
                // were returned above and are never replaced.
                SetField(__instance, "_askPed", null);
                SetField(__instance, "_askKey", null);
                SetField(__instance, "_askComp", null);

                if (CountActiveCompanions(__instance) >= _maxCompanions)
                    return;

                Ped candidate = FindCandidate(__instance, player);
                if (candidate == null)
                    return;

                string key = KeyPrefix + candidate.Handle.ToString("X8");
                SetField(__instance, "_askPed", candidate);
                SetField(__instance, "_askKey", key);
                SetField(__instance, "_askComp", null);

                _insideAskReentry = true;
                try
                {
                    _updateAsk.Invoke(__instance, new object[] { player });
                }
                finally
                {
                    _insideAskReentry = false;
                }
            }
            catch (Exception ex)
            {
                _insideAskReentry = false;
                Log("Recruitment scan error: " + Unwrap(ex));
            }
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
                Ped ped = __args[0] as Ped;
                Ped player = __args[2] as Ped;
                RecruitGeneric(__instance, ped, key, player);
            }
            catch (Exception ex)
            {
                Log("Recruitment failed: " + Unwrap(ex));
            }

            // This key intentionally does not exist in enRAGE's CampPeople dictionary.
            // Suppress the original Ask to avoid a KeyNotFoundException.
            return false;
        }

        private static void RecruitGeneric(object __instance, Ped ped, string key, Ped player)
        {
            if (ped == null || !ped.Exists() || ped.IsDead || player == null || !player.Exists())
                return;

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
            string name = GetMember(trackedRecord, "Name") as string;
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
                SetField(companion, "Rec", trackedRecord);
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

                adopt.Invoke(__instance, new object[] { companion, ped, player });
                // Detach only after successful adoption, so a failed adoption leaves camp ownership intact.
                DetachFromLiveCamp(__instance, ped);
                try { ApplyPersistentNemesisGang(__instance, ped.Handle); }
                catch (Exception ex) { Log("Persistent faction update warning: " + Unwrap(ex)); }
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

        private static void RestoreRealPostfix(object[] __args)
        {
            if (!_keepPlayerSideAfterDismissal || __args == null || __args.Length == 0)
                return;

            try
            {
                object companion = __args[0];
                string key = GetField(companion, "Key") as string;
                if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    return;

                Ped ped = GetField(companion, "Ped") as Ped;
                Ped player = Game.Player.Character;
                if (ped != null && ped.Exists() && player != null && player.Exists())
                    ped.RelationshipGroup = player.RelationshipGroup;
            }
            catch (Exception ex)
            {
                Log("Persistent side switch warning: " + Unwrap(ex));
            }
        }

        private static Ped FindCandidate(object instance, Ped player)
        {
            if (_roleOf == null)
                return null;

            MethodInfo snapshotMethod = FindMethod(_mainType, "PedSnapshot", 1, p => IsPedType(p[0].ParameterType));
            if (snapshotMethod == null || !snapshotMethod.IsStatic)
                return null;
            if (FindMethod(_mainType, "IsStoryPed", 1, p => IsPedType(p[0].ParameterType)) == null ||
                FindMethod(_mainType, "GameEventPed", 1, p => IsPedType(p[0].ParameterType)) == null ||
                FindMethod(_mainType.Assembly.GetType("NemesisSystem.Kids", false), "IsChild", 1, p => IsPedType(p[0].ParameterType)) == null)
            {
                Log("Candidate scan disabled: enRAGE's story/event/child filters could not be resolved.");
                return null;
            }

            object raw = snapshotMethod.Invoke(null, new object[] { player });
            IEnumerable snapshot = raw as IEnumerable;
            if (snapshot == null)
                return null;

            Ped best = null;
            float bestDistance = 3.2f;
            int playerGroup = PED.GET_PED_GROUP_INDEX(player.Handle);

            foreach (object tuple in snapshot)
            {
                Ped ped = TuplePed(tuple);
                if (ped == null || !ped.Exists() || ped.IsDead || !ped.IsHuman || ped.Handle == player.Handle)
                    continue;
                if (ped.IsHogtied || ped.IsInVehicle || ped.IsOnMount || PED.IS_PED_IN_COMBAT(ped.Handle, 0))
                    continue;

                float distance = ped.Position.DistanceTo(player.Position);
                if (distance >= bestDistance)
                    continue;
                if (AlreadyCompanion(GetField(instance, "_comps") as IList, ped.Handle))
                    continue;
                if (!ENTITY.HAS_ENTITY_CLEAR_LOS_TO_ENTITY(ped.Handle, player.Handle, 17))
                    continue;
                if (playerGroup != 0 && PED.IS_PED_GROUP_MEMBER(ped.Handle, playerGroup, false))
                    continue;

                string role = "";
                try { role = _roleOf.Invoke(instance, new object[] { ped.Handle })?.ToString() ?? ""; }
                catch { continue; }

                // Start with ordinary peds and un-named enRAGE camp members. Other mod-managed
                // roles have their own lifecycle and are deliberately not stolen in this first pass.
                if (role != "None" && role != "Camp")
                    continue;
                if (TryStaticBool("IsStoryPed", ped))
                    continue;
                if (TryStaticBool("GameEventPed", ped))
                    continue;
                if (TryStaticBoolOnType("NemesisSystem.Kids", "IsChild", ped))
                    continue;

                if (role == "None")
                {
                    if (ENTITY.IS_ENTITY_A_MISSION_ENTITY(ped.Handle))
                        continue;
                    if (FindMethod(_mainType, "IsLawman", 1, p => IsPedType(p[0].ParameterType)) == null ||
                        FindMethod(_mainType, "GameOwned", 1, p => IsPedType(p[0].ParameterType)) == null)
                        continue; // fail closed if enRAGE's mission/law filters are unavailable
                    if (TryInstanceBool(instance, "IsLawman", ped) || TryInstanceBool(instance, "GameOwned", ped))
                        continue;
                }

                best = ped;
                bestDistance = distance;
            }
            return best;
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

        // This only changes an NPC that enRAGE already represents as a NemesisRecord.
        // A random ambient pedestrian has no persistent roster record to mutate safely.
        private static void ApplyPersistentNemesisGang(object instance, int handle)
        {
            string targetGang = (_persistentNemesisGangId ?? string.Empty).Trim();
            if (targetGang.Length == 0)
                return;

            object record = GetTrackedRecord(instance, handle);
            if (record == null)
            {
                Log("Persistent faction not written: ped " + handle + " has no NemesisRecord.");
                return;
            }

            object isStory = GetMember(record, "Story");
            if (isStory is bool story && story)
            {
                Log("Persistent faction not written: refusing to rewrite a story record for ped " + handle + ".");
                return;
            }

            Type gangsType = _mainType.Assembly.GetType("NemesisSystem.Gangs", false);
            MethodInfo byId = gangsType == null ? null : FindMethod(gangsType, "ById", 1, p => p[0].ParameterType == typeof(string));
            object gang = byId != null && byId.IsStatic ? byId.Invoke(null, new object[] { targetGang }) : null;
            if (gang == null)
            {
                Log("Persistent faction not written: unknown enRAGE gang id '" + targetGang + "'. Check Gangs.ById(id) in the installed version.");
                return;
            }

            MethodInfo saveRoster = FindMethod(_mainType, "SaveRoster", 0);
            if (saveRoster == null)
            {
                Log("Persistent faction not written: SaveRoster was not found.");
                return;
            }

            string oldGang = GetMember(record, "Gang") as string;
            if (string.Equals(oldGang, targetGang, StringComparison.OrdinalIgnoreCase))
                return;

            // SetMember writes public properties on NemesisRecord; SetField alone cannot write
            // C# auto-properties by their source-level names. SaveRoster marks the roster dirty.
            if (!SetMember(record, "FormerGang", string.IsNullOrWhiteSpace(oldGang) ? GetMember(record, "FormerGang") : oldGang) ||
                !SetMember(record, "Gang", targetGang) ||
                !SetMember(record, "BossId", null) ||
                !SetMember(record, "CampId", null) ||
                !SetMember(record, "CampKnown", false))
            {
                Log("Persistent faction write failed for ped " + handle + "; one or more record properties were unavailable.");
                return;
            }

            if (string.Equals(GetMember(record, "Affiliation") as string, targetGang, StringComparison.OrdinalIgnoreCase))
                SetMember(record, "Affiliation", null);

            saveRoster.Invoke(instance, null);
            Log("Updated enRAGE NemesisRecord faction for ped " + handle + ": '" + (oldGang ?? "none") + "' -> '" + targetGang + "'.");
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

        private static string ReadConfiguration()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnRAGECompanionsExtension.ini");
            try
            {
                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path))
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
                        else if (key.Equals("KeepPlayerSideAfterDismissal", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool keep))
                            _keepPlayerSideAfterDismissal = keep;
                        else if (key.Equals("PersistentNemesisGangId", StringComparison.OrdinalIgnoreCase))
                            _persistentNemesisGangId = value;
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path,
                        "; EnRAGE Companions Extension settings\r\n" +
                        "MaxCompanions=3\r\n" +
                        "KeepPlayerSideAfterDismissal=false\r\n" +
                        "PersistentNemesisGangId=\r\n");
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
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory);
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "EnRAGECompanionsExtension.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
