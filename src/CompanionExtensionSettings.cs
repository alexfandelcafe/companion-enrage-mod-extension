using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EnRAGECompanionsExtension
{
    internal sealed class CompanionExtensionSettings
    {
        internal bool EnableIndividualOrders = true;
        internal bool EnableWaitMode = true;
        internal float WaitRadius = 8.0f;
        internal bool EnableInjurySystem = true;
        internal int InjuryDamageThreshold = 25;
        internal float InjuryMoveRateMultiplier = 0.65f;
        internal int InjuryRecoverySeconds = 300;
        internal int MedicineCostCents = 100;
        internal int MedicineInjuryReduction = 1;
        internal bool MedicineRestoresFullHealth = true;
        internal float MedicineHealthRestoreFraction = 0.50f;
        internal bool EnableAffinity = true;
        internal int AffinityTravelIntervalSeconds = 300;
        internal int AffinityTravelGain = 1;
        internal int AffinityCombatGain = 2;
        internal int AffinityTreatmentGain = 3;
        internal bool EnableCombatStances = true;
        internal float ProtectAreaRadius = 25.0f;
        internal bool FireOnSignalOnly = true;
        internal int FireSignalWindowMs = 12000;
        internal bool EnableAppearancePersistence = true;
        internal bool PreserveSavedOutfit = true;
        internal bool EnableClothingComponentPersistence = false;
        internal bool EnableHatPersistence = false;
        internal bool EnableWeaponPersistence = false;

        internal static CompanionExtensionSettings Load(string path, Action<string> log)
        {
            CompanionExtensionSettings settings = new CompanionExtensionSettings();
            Action<string> writeLog = log ?? delegate { };
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string defaults =
                "\r\n; --- Órdenes individuales y espera ---\r\n" +
                "EnableIndividualOrders=true\r\n" +
                "EnableWaitMode=true\r\n" +
                "WaitRadius=8.0\r\n" +
                "; --- Heridas y tratamiento ---\r\n" +
                "EnableInjurySystem=true\r\n" +
                "InjuryDamageThreshold=25\r\n" +
                "InjuryMoveRateMultiplier=0.65\r\n" +
                "InjuryRecoverySeconds=300\r\n" +
                "MedicineCostCents=100\r\n" +
                "MedicineInjuryReduction=1\r\n" +
                "MedicineRestoresFullHealth=true\r\n" +
                "MedicineHealthRestoreFraction=0.50\r\n" +
                "; --- Afinidad ---\r\n" +
                "EnableAffinity=true\r\n" +
                "AffinityTravelIntervalSeconds=300\r\n" +
                "AffinityTravelGain=1\r\n" +
                "AffinityCombatGain=2\r\n" +
                "AffinityTreatmentGain=3\r\n" +
                "; --- Combate ---\r\n" +
                "EnableCombatStances=true\r\n" +
                "ProtectAreaRadius=25.0\r\n" +
                "FireOnSignalOnly=true\r\n" +
                "FireSignalWindowMs=12000\r\n" +
                "; --- Apariencia: los módulos experimentales permanecen desactivados por defecto ---\r\n" +
                "EnableAppearancePersistence=true\r\n" +
                "PreserveSavedOutfit=true\r\n" +
                "EnableClothingComponentPersistence=false\r\n" +
                "EnableHatPersistence=false\r\n" +
                "EnableWeaponPersistence=false\r\n";

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                if (!File.Exists(path))
                    File.WriteAllText(path, "; EnRAGE Companions Extension\r\n");

                string[] lines = File.ReadAllLines(path);
                foreach (string raw in lines)
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) ||
                        line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal))
                        continue;

                    int split = line.IndexOf('=');
                    if (split <= 0)
                        continue;
                    values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                }

                string[] defaultLines = defaults.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                bool missing = false;
                foreach (string defaultLine in defaultLines)
                {
                    string candidate = defaultLine.Trim();
                    int split = candidate.IndexOf('=');
                    if (split > 0 && !values.ContainsKey(candidate.Substring(0, split).Trim()))
                    {
                        missing = true;
                        break;
                    }
                }
                if (missing)
                    File.AppendAllText(path, defaults);

                bool b;
                int i;
                float f;
                if (values.TryGetValue("EnableIndividualOrders", out string value) && bool.TryParse(value, out b)) settings.EnableIndividualOrders = b;
                if (values.TryGetValue("EnableWaitMode", out value) && bool.TryParse(value, out b)) settings.EnableWaitMode = b;
                if (values.TryGetValue("WaitRadius", out value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) settings.WaitRadius = Clamp(f, 3f, 35f);
                if (values.TryGetValue("EnableInjurySystem", out value) && bool.TryParse(value, out b)) settings.EnableInjurySystem = b;
                if (values.TryGetValue("InjuryDamageThreshold", out value) && int.TryParse(value, out i)) settings.InjuryDamageThreshold = Clamp(i, 5, 90);
                if (values.TryGetValue("InjuryMoveRateMultiplier", out value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) settings.InjuryMoveRateMultiplier = Clamp(f, 0.25f, 1f);
                if (values.TryGetValue("InjuryRecoverySeconds", out value) && int.TryParse(value, out i)) settings.InjuryRecoverySeconds = Clamp(i, 30, 86400);
                if (values.TryGetValue("MedicineCostCents", out value) && int.TryParse(value, out i)) settings.MedicineCostCents = Clamp(i, 0, 100000);
                if (values.TryGetValue("MedicineInjuryReduction", out value) && int.TryParse(value, out i)) settings.MedicineInjuryReduction = Clamp(i, 0, 2);
                if (values.TryGetValue("MedicineRestoresFullHealth", out value) && bool.TryParse(value, out b)) settings.MedicineRestoresFullHealth = b;
                if (values.TryGetValue("MedicineHealthRestoreFraction", out value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) settings.MedicineHealthRestoreFraction = Clamp(f, 0f, 1f);
                if (values.TryGetValue("EnableAffinity", out value) && bool.TryParse(value, out b)) settings.EnableAffinity = b;
                if (values.TryGetValue("AffinityTravelIntervalSeconds", out value) && int.TryParse(value, out i)) settings.AffinityTravelIntervalSeconds = Clamp(i, 60, 3600);
                if (values.TryGetValue("AffinityTravelGain", out value) && int.TryParse(value, out i)) settings.AffinityTravelGain = Clamp(i, 0, 5);
                if (values.TryGetValue("AffinityCombatGain", out value) && int.TryParse(value, out i)) settings.AffinityCombatGain = Clamp(i, 0, 10);
                if (values.TryGetValue("AffinityTreatmentGain", out value) && int.TryParse(value, out i)) settings.AffinityTreatmentGain = Clamp(i, 0, 10);
                if (values.TryGetValue("EnableCombatStances", out value) && bool.TryParse(value, out b)) settings.EnableCombatStances = b;
                if (values.TryGetValue("ProtectAreaRadius", out value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) settings.ProtectAreaRadius = Clamp(f, 5f, 100f);
                if (values.TryGetValue("FireOnSignalOnly", out value) && bool.TryParse(value, out b)) settings.FireOnSignalOnly = b;
                if (values.TryGetValue("FireSignalWindowMs", out value) && int.TryParse(value, out i)) settings.FireSignalWindowMs = Clamp(i, 1000, 30000);
                if (values.TryGetValue("EnableAppearancePersistence", out value) && bool.TryParse(value, out b)) settings.EnableAppearancePersistence = b;
                if (values.TryGetValue("PreserveSavedOutfit", out value) && bool.TryParse(value, out b)) settings.PreserveSavedOutfit = b;
                if (values.TryGetValue("EnableClothingComponentPersistence", out value) && bool.TryParse(value, out b)) settings.EnableClothingComponentPersistence = b;
                if (values.TryGetValue("EnableHatPersistence", out value) && bool.TryParse(value, out b)) settings.EnableHatPersistence = b;
                if (values.TryGetValue("EnableWeaponPersistence", out value) && bool.TryParse(value, out b)) settings.EnableWeaponPersistence = b;
            }
            catch (Exception ex)
            {
                writeLog("Companion INI validation warning; conservative defaults were retained where values were invalid: " +
                    ex.GetType().Name + ": " + ex.Message);
            }

            return settings;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return min;
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
