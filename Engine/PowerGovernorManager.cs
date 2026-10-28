using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.ElectricPower;
using Mafi.Core.Factory.Machines;
using PowerThresholdMod.Model;

namespace PowerThresholdMod.Engine
{
    public class PowerGovernorManager
    {
        public static PowerGovernorManager Instance { get; private set; }

        private readonly IElectricityManager m_electricityManager;
        private readonly EntitiesManager m_entitiesManager;

        public IElectricityManager ElectricityManager => m_electricityManager;
        public EntitiesManager EntitiesManager => m_entitiesManager;

        public Dictionary<string, MachinePowerRule> GlobalRules { get; } = new Dictionary<string, MachinePowerRule>();
        public Dictionary<int, MachineInstanceOverride> InstanceOverrides { get; } = new Dictionary<int, MachineInstanceOverride>();

        // Set of entity IDs currently throttled (paused) by our governor
        private readonly HashSet<int> m_throttledByMod = new HashSet<int>();

        // Cached count of throttled entities by prototype ID for fast UI display
        private readonly Dictionary<string, int> m_throttledCountsByProto = new Dictionary<string, int>();

        private const int CHECK_INTERVAL_STEPS = 20; // Check every ~20 simulation steps (~1 sec)
        private int m_stepCounter;

        private int m_currentSimStep;
        private int m_stableStepCounter;
        private int m_cooldownUntilStep;
        private int m_lastTestedMachineId = -1;
        private int m_testExpiresAtStep;

        public int ThrottledCount => m_throttledByMod.Count;
        public int LastTestedMachineId => m_lastTestedMachineId;
        public string LastTestedMachineName { get; private set; } = "";
        public string LastTestedProtoId { get; private set; } = "";

        public int RecoveryStableSeconds => Math.Min(10, m_stableStepCounter / 20);
        public int RecoveryCooldownSeconds => Math.Max(0, (m_cooldownUntilStep - m_currentSimStep + 19) / 20);

        public float LastCapacityMw { get; private set; }
        public float LastProductionMw { get; private set; }
        public float LastDemandedMw { get; private set; }
        public float LastConsumedMw { get; private set; }
        public float LastProductionPercent { get; private set; }
        public bool LastIsDeficit { get; private set; }

        public Electricity LastCapacity { get; private set; }
        public Electricity LastProduction { get; private set; }
        public Electricity LastDemanded { get; private set; }
        public Electricity LastConsumed { get; private set; }

        public PowerGovernorManager(IElectricityManager electricityManager, EntitiesManager entitiesManager)
        {
            Instance = this;
            m_electricityManager = electricityManager;
            m_entitiesManager = entitiesManager;
            LoadSettings();
        }

        public MachinePowerRule GetGlobalRule(string protoId)
        {
            if (!GlobalRules.TryGetValue(protoId, out var rule))
            {
                rule = new MachinePowerRule
                {
                    IsEnabled = false, // off by default until player enables
                    Mode = ThresholdMode.Percentage,
                    PercentThreshold = 20f,
                    MwThreshold = 50f,
                    Hysteresis = 2f
                };
                GlobalRules[protoId] = rule;
            }
            return rule;
        }

        public MachinePowerRule GetGlobalRule(LayoutEntityProto proto) => GetGlobalRule(proto.Id.Value);
        public MachinePowerRule GetGlobalRule(MachineProto proto) => GetGlobalRule(proto.Id.Value);

        public MachinePowerRule GetEffectiveRule(LayoutEntity entity)
        {
            int entityId = entity.Id.Value;
            if (InstanceOverrides.TryGetValue(entityId, out var custom) && custom.HasOverride)
            {
                return new MachinePowerRule
                {
                    IsEnabled = custom.IsEnabled,
                    Mode = custom.Mode,
                    PercentThreshold = custom.PercentThreshold,
                    MwThreshold = custom.MwThreshold,
                    Hysteresis = custom.Hysteresis
                };
            }
            return GetGlobalRule(entity.Prototype.Id.Value);
        }

        public MachinePowerRule GetEffectiveRule(Machine machine) => GetEffectiveRule((LayoutEntity)machine);

        public bool IsEntityThrottled(LayoutEntity entity)
        {
            return m_throttledByMod.Contains(entity.Id.Value);
        }

        public bool IsMachineThrottled(Machine machine) => IsEntityThrottled(machine);

        public bool IsProtoBeingTested(string protoId)
        {
            return m_lastTestedMachineId > 0 && LastTestedProtoId == protoId;
        }

        public int GetThrottledCountForProto(string protoId)
        {
            return m_throttledCountsByProto.TryGetValue(protoId, out int cnt) ? cnt : 0;
        }

        public string GetRecoveryStatusText()
        {
            if (ThrottledCount == 0 && m_lastTestedMachineId <= 0)
            {
                return "All configured buildings running";
            }
            if (m_lastTestedMachineId > 0)
            {
                int sec = Math.Max(1, (m_testExpiresAtStep - m_currentSimStep + 19) / 20);
                return $"🔄 Testing unpause of {LastTestedMachineName}... ({sec}s)";
            }
            if (m_currentSimStep < m_cooldownUntilStep)
            {
                return $"⚠️ Grid deficit detected. Backing off ({RecoveryCooldownSeconds}s left)...";
            }
            return $"⏳ Evaluating grid stability ({RecoveryStableSeconds}s / 10s)...";
        }

        public void ResumeAllThrottled()
        {
            if (m_entitiesManager == null) return;

            var list = new List<int>(m_throttledByMod);
            foreach (int id in list)
            {
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                if (entityOpt.HasValue)
                {
                    entityOpt.Value.SetPaused(false);
                }
            }
            m_throttledByMod.Clear();
            m_throttledCountsByProto.Clear();
            m_lastTestedMachineId = -1;
            LastTestedMachineName = "";
            LastTestedProtoId = "";
            m_testExpiresAtStep = 0;
            m_stableStepCounter = 0;
            Log.Info("PowerGovernor: Manually resumed all throttled entities.");
        }

        public void SetInstanceOverride(LayoutEntity entity, MachineInstanceOverride overrideRule)
        {
            int entityId = entity.Id.Value;
            if (overrideRule == null || !overrideRule.HasOverride)
            {
                InstanceOverrides.Remove(entityId);
            }
            else
            {
                InstanceOverrides[entityId] = overrideRule;
            }
            SaveSettings();
        }

        public void SetInstanceOverride(Machine machine, MachineInstanceOverride overrideRule) =>
            SetInstanceOverride((LayoutEntity)machine, overrideRule);

        public MachineInstanceOverride GetOrCreateInstanceOverride(LayoutEntity entity)
        {
            int entityId = entity.Id.Value;
            if (!InstanceOverrides.TryGetValue(entityId, out var custom))
            {
                var global = GetGlobalRule(entity.Prototype.Id.Value);
                custom = new MachineInstanceOverride
                {
                    HasOverride = false,
                    IsEnabled = global.IsEnabled,
                    Mode = global.Mode,
                    PercentThreshold = global.PercentThreshold,
                    MwThreshold = global.MwThreshold,
                    Hysteresis = global.Hysteresis
                };
            }
            return custom;
        }

        public MachineInstanceOverride GetOrCreateInstanceOverride(Machine machine) =>
            GetOrCreateInstanceOverride((LayoutEntity)machine);

        public void OnSimUpdate()
        {
            if (m_electricityManager == null || m_entitiesManager == null)
            {
                return;
            }

            m_currentSimStep++;

            var concrete = m_electricityManager as ElectricityManager;

            // Capacity: Prefer MaxGenerationCapacity (total grid nameplate) if positive, else GenerationCapacityThisTick
            Electricity capacity = (concrete != null && concrete.MaxGenerationCapacity.IsPositive)
                ? concrete.MaxGenerationCapacity
                : m_electricityManager.GenerationCapacityThisTick;

            // Production: GeneratedThisTick from concrete manager, otherwise ConsumedThisTick
            Electricity production = (concrete != null)
                ? concrete.GeneratedThisTick
                : m_electricityManager.ConsumedThisTick;

            Electricity demanded = m_electricityManager.DemandedThisTick;
            Electricity consumed = m_electricityManager.ConsumedThisTick;

            LastCapacity = capacity;
            LastProduction = production;
            LastDemanded = demanded;
            LastConsumed = consumed;

            LastCapacityMw = (float)capacity.Value / 1000f;
            LastProductionMw = (float)production.Value / 1000f;
            LastDemandedMw = (float)demanded.Value / 1000f;
            LastConsumedMw = (float)consumed.Value / 1000f;

            LastProductionPercent = LastCapacityMw > 0.0001f ? (LastProductionMw / LastCapacityMw) * 100f : 0f;
            LastIsDeficit = demanded.Value > consumed.Value;

            // Track stability and handle test-unpause validation
            if (LastIsDeficit)
            {
                // Grid is failing to satisfy demand
                m_stableStepCounter = 0;

                // If we were testing a building unpause, it failed!
                if (m_lastTestedMachineId > 0)
                {
                    var testedOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(m_lastTestedMachineId));
                    if (testedOpt.HasValue && testedOpt.Value.IsNotPaused)
                    {
                        testedOpt.Value.SetPaused(true);
                        m_throttledByMod.Add(m_lastTestedMachineId);
                        Log.Warning($"PowerGovernor: Test unpause of {LastTestedMachineName} caused deficit. Re-pausing and setting cooldown.");
                    }
                    m_lastTestedMachineId = -1;
                    LastTestedMachineName = "";
                    LastTestedProtoId = "";
                    m_cooldownUntilStep = m_currentSimStep + 400; // 20s cooldown
                }
            }
            else
            {
                // Grid is satisfying 100% of demand
                m_stableStepCounter++;

                // If a test unpause completed its observation window successfully
                if (m_lastTestedMachineId > 0 && m_currentSimStep >= m_testExpiresAtStep)
                {
                    Log.Info($"PowerGovernor: Test unpause of {LastTestedMachineName} succeeded. Building remains active.");
                    m_lastTestedMachineId = -1;
                    LastTestedMachineName = "";
                    LastTestedProtoId = "";
                }
            }

            // Periodic evaluation of configured rules
            m_stepCounter++;
            if (m_stepCounter % CHECK_INTERVAL_STEPS == 0)
            {
                m_throttledCountsByProto.Clear();

                var allEntities = m_entitiesManager.GetAllEntitiesOfType<LayoutEntity>();
                foreach (var entity in allEntities)
                {
                    if (!entity.CanBePaused) continue;

                    int entityId = entity.Id.Value;
                    string protoId = entity.Prototype.Id.Value;

                    // Fast check: skip entities that have neither custom override nor global rule configured
                    if (!InstanceOverrides.ContainsKey(entityId) && !GlobalRules.ContainsKey(protoId))
                    {
                        continue;
                    }

                    var rule = GetEffectiveRule(entity);
                    if (!rule.IsEnabled)
                    {
                        // If previously throttled, restore immediately
                        if (m_throttledByMod.Contains(entityId))
                        {
                            m_throttledByMod.Remove(entityId);
                            entity.SetPaused(false);
                        }
                        continue;
                    }

                    float currentMetric = (rule.Mode == ThresholdMode.Percentage) ? LastProductionPercent : LastProductionMw;
                    float threshold = rule.ThresholdValue;

                    bool isThrottled = m_throttledByMod.Contains(entityId);

                    if (isThrottled)
                    {
                        m_throttledCountsByProto[protoId] = m_throttledCountsByProto.TryGetValue(protoId, out int cnt) ? cnt + 1 : 1;
                    }
                    else
                    {
                        // Check if production fell below Threshold
                        if (currentMetric < threshold)
                        {
                            // Only throttle if building is currently active
                            if (entity.IsNotPaused)
                            {
                                m_throttledByMod.Add(entityId);
                                entity.SetPaused(true);
                                m_throttledCountsByProto[protoId] = m_throttledCountsByProto.TryGetValue(protoId, out int cnt) ? cnt + 1 : 1;
                                Log.Info($"PowerGovernor: Throttled {entity.Prototype.Strings.Name} (#{entityId}) - Production: {currentMetric:F1} < {threshold:F1}");
                            }
                        }
                    }
                }
            }

            // AUTO-RECOVERY: If grid has been 100% stable for ~10 seconds (200 steps) and no test is running
            if (m_throttledByMod.Count > 0 &&
                m_stableStepCounter >= 200 &&
                m_currentSimStep >= m_cooldownUntilStep &&
                m_lastTestedMachineId <= 0)
            {
                attemptTestUnpause();
            }
        }

        private void attemptTestUnpause()
        {
            // Pick one building to test unpause: prefer lowest threshold rule
            int bestCandidateId = -1;
            float lowestThreshold = float.MaxValue;

            foreach (int id in m_throttledByMod)
            {
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                if (!entityOpt.HasValue) continue;

                var rule = GetEffectiveRule(entityOpt.Value);
                if (rule.ThresholdValue < lowestThreshold)
                {
                    lowestThreshold = rule.ThresholdValue;
                    bestCandidateId = id;
                }
            }

            if (bestCandidateId > 0)
            {
                var candidateOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(bestCandidateId));
                if (candidateOpt.HasValue)
                {
                    m_lastTestedMachineId = bestCandidateId;
                    LastTestedMachineName = candidateOpt.Value.Prototype.Strings.Name.ToString();
                    LastTestedProtoId = candidateOpt.Value.Prototype.Id.Value;
                    m_testExpiresAtStep = m_currentSimStep + 60; // 3 seconds observation window
                    m_stableStepCounter = 0;

                    m_throttledByMod.Remove(bestCandidateId);
                    candidateOpt.Value.SetPaused(false);
                    Log.Info($"PowerGovernor: Grid stable for 10s. Testing unpause of {LastTestedMachineName} (#{bestCandidateId}).");
                }
            }
        }

        public void SaveSettings()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Captain of Industry", "Mods", "PowerThresholdMod");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "settings.json");

                using (var writer = new StreamWriter(path))
                {
                    writer.WriteLine("{");
                    writer.WriteLine("  \"rules\": {");
                    int i = 0;
                    foreach (var kvp in GlobalRules)
                    {
                        string comma = (++i < GlobalRules.Count) ? "," : "";
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "    \"{0}\": {{ \"enabled\": {1}, \"mode\": {2}, \"percent\": {3:F1}, \"mw\": {4:F1}, \"hysteresis\": {5:F1} }}{6}",
                            kvp.Key, kvp.Value.IsEnabled.ToString().ToLower(), (int)kvp.Value.Mode, kvp.Value.PercentThreshold, kvp.Value.MwThreshold, kvp.Value.Hysteresis, comma));
                    }
                    writer.WriteLine("  }");
                    writer.WriteLine("}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PowerGovernor: Failed to save settings: {ex.Message}");
            }
        }

        public void LoadSettings()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Captain of Industry", "Mods", "PowerThresholdMod", "settings.json");
                if (!File.Exists(path)) return;

                string[] lines = File.ReadAllLines(path);
                var regexNew = new Regex("\"([^\"]+)\":\\s*\\{\\s*\"enabled\":\\s*(true|false),\\s*\"mode\":\\s*(\\d+),\\s*\"percent\":\\s*([\\d\\.]+),\\s*\"mw\":\\s*([\\d\\.]+)");
                var regexOld = new Regex("\"([^\"]+)\":\\s*\\{\\s*\"enabled\":\\s*(true|false),\\s*\"mode\":\\s*(\\d+),\\s*\"threshold\":\\s*([\\d\\.]+)");

                foreach (string line in lines)
                {
                    var matchNew = regexNew.Match(line);
                    if (matchNew.Success)
                    {
                        string protoId = matchNew.Groups[1].Value;
                        bool enabled = bool.Parse(matchNew.Groups[2].Value);
                        int mode = int.Parse(matchNew.Groups[3].Value);
                        float percent = float.Parse(matchNew.Groups[4].Value, CultureInfo.InvariantCulture);
                        float mw = float.Parse(matchNew.Groups[5].Value, CultureInfo.InvariantCulture);

                        GlobalRules[protoId] = new MachinePowerRule
                        {
                            IsEnabled = enabled,
                            Mode = (ThresholdMode)mode,
                            PercentThreshold = Math.Max(0f, Math.Min(100f, percent)),
                            MwThreshold = Math.Max(0f, mw)
                        };
                        continue;
                    }

                    var matchOld = regexOld.Match(line);
                    if (matchOld.Success)
                    {
                        string protoId = matchOld.Groups[1].Value;
                        bool enabled = bool.Parse(matchOld.Groups[2].Value);
                        int mode = int.Parse(matchOld.Groups[3].Value);
                        float threshold = float.Parse(matchOld.Groups[4].Value, CultureInfo.InvariantCulture);

                        var rule = new MachinePowerRule
                        {
                            IsEnabled = enabled,
                            Mode = (ThresholdMode)mode
                        };
                        if (rule.Mode == ThresholdMode.Percentage)
                        {
                            rule.PercentThreshold = Math.Max(0f, Math.Min(100f, threshold));
                        }
                        else
                        {
                            rule.MwThreshold = Math.Max(0f, threshold);
                        }
                        GlobalRules[protoId] = rule;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PowerGovernor: Failed to load settings: {ex.Message}");
            }
        }
    }
}
