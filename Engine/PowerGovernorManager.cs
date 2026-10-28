using System;
using System.Collections.Generic;
using System.IO;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
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

        // Keyed by MachineProto.Id.Value (string)
        public Dictionary<string, MachinePowerRule> GlobalRules { get; } = new Dictionary<string, MachinePowerRule>();

        // Keyed by Machine EntityId.Value (int)
        public Dictionary<int, MachineInstanceOverride> InstanceOverrides { get; } = new Dictionary<int, MachineInstanceOverride>();

        // Tracks machines that were paused by the Governor (so we never unpause player-manually-paused machines)
        private readonly HashSet<int> m_throttledByMod = new HashSet<int>();

        private uint m_stepCounter = 0;
        private const uint CHECK_INTERVAL_STEPS = 30; // ~1 second of game time

        // Live grid metrics for UI
        public float LastCapacityMw { get; private set; }
        public float LastDemandedMw { get; private set; }
        public float LastReserveMw { get; private set; }
        public float LastReservePercent { get; private set; }

        public PowerGovernorManager(IElectricityManager electricityManager, EntitiesManager entitiesManager)
        {
            Instance = this;
            m_electricityManager = electricityManager;
            m_entitiesManager = entitiesManager;
            LoadSettings();
        }

        public MachinePowerRule GetGlobalRule(MachineProto proto)
        {
            string id = proto.Id.Value;
            if (!GlobalRules.TryGetValue(id, out var rule))
            {
                rule = new MachinePowerRule
                {
                    IsEnabled = false, // off by default until player enables
                    Mode = ThresholdMode.Percentage,
                    ThresholdValue = 20f,
                    Hysteresis = 2f
                };
                GlobalRules[id] = rule;
            }
            return rule;
        }

        public MachinePowerRule GetEffectiveRule(Machine machine)
        {
            int entityId = machine.Id.Value;
            if (InstanceOverrides.TryGetValue(entityId, out var custom) && custom.HasOverride)
            {
                return new MachinePowerRule
                {
                    IsEnabled = custom.IsEnabled,
                    Mode = custom.Mode,
                    ThresholdValue = custom.ThresholdValue,
                    Hysteresis = custom.Hysteresis
                };
            }
            return GetGlobalRule(machine.Prototype);
        }

        public bool IsMachineThrottled(Machine machine)
        {
            return m_throttledByMod.Contains(machine.Id.Value);
        }

        public void SetInstanceOverride(Machine machine, MachineInstanceOverride overrideRule)
        {
            int entityId = machine.Id.Value;
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

        public MachineInstanceOverride GetOrCreateInstanceOverride(Machine machine)
        {
            int entityId = machine.Id.Value;
            if (!InstanceOverrides.TryGetValue(entityId, out var custom))
            {
                var global = GetGlobalRule(machine.Prototype);
                custom = new MachineInstanceOverride
                {
                    HasOverride = false,
                    IsEnabled = global.IsEnabled,
                    Mode = global.Mode,
                    ThresholdValue = global.ThresholdValue,
                    Hysteresis = global.Hysteresis
                };
            }
            return custom;
        }

        public void OnSimUpdate()
        {
            m_stepCounter++;
            if (m_stepCounter < CHECK_INTERVAL_STEPS)
            {
                return;
            }
            m_stepCounter = 0;

            if (m_electricityManager == null || m_entitiesManager == null)
            {
                return;
            }

            // Read live electricity grid stats
            Electricity capacity = m_electricityManager.GenerationCapacityThisTick;
            Electricity demanded = m_electricityManager.DemandedThisTick;

            // In COI, Electricity values are in Kw / scaled quantities
            LastCapacityMw = (float)capacity.Quantity.Value / 1000f;
            LastDemandedMw = (float)demanded.Quantity.Value / 1000f;
            LastReserveMw = LastCapacityMw - LastDemandedMw;
            LastReservePercent = LastCapacityMw > 0.001f ? (LastReserveMw / LastCapacityMw) * 100f : 0f;

            // Enumerate all active machines
            foreach (Machine machine in m_entitiesManager.GetAllEntitiesOfType<Machine>())
            {
                if (!machine.Prototype.ElectricityConsumed.IsPositive && !machine.ElectricityConsumer.HasValue)
                {
                    continue;
                }

                int machineId = machine.Id.Value;
                MachinePowerRule rule = GetEffectiveRule(machine);

                if (!rule.IsEnabled)
                {
                    // If disabled, resume machine if we had paused it
                    if (m_throttledByMod.Contains(machineId))
                    {
                        m_throttledByMod.Remove(machineId);
                        machine.SetPaused(false);
                    }
                    continue;
                }

                float currentMetric = (rule.Mode == ThresholdMode.Percentage) ? LastReservePercent : LastReserveMw;
                float threshold = rule.ThresholdValue;
                float hysteresis = rule.Hysteresis;

                bool isThrottled = m_throttledByMod.Contains(machineId);

                if (isThrottled)
                {
                    // Check if grid has recovered to (Threshold + Hysteresis)
                    if (currentMetric >= (threshold + hysteresis))
                    {
                        m_throttledByMod.Remove(machineId);
                        machine.SetPaused(false);
                        Log.Info($"PowerGovernor: Resumed machine {machine.Prototype.Strings.Name} (#{machineId}) - Grid: {currentMetric:F1} >= {(threshold + hysteresis):F1}");
                    }
                }
                else
                {
                    // Check if grid fell below Threshold
                    if (currentMetric < threshold)
                    {
                        // Only throttle if player hasn't already paused it
                        if (machine.IsNotPaused)
                        {
                            m_throttledByMod.Add(machineId);
                            machine.SetPaused(true);
                            Log.Info($"PowerGovernor: Throttled machine {machine.Prototype.Strings.Name} (#{machineId}) - Grid: {currentMetric:F1} < {threshold:F1}");
                        }
                    }
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
                        writer.WriteLine($"    \"{kvp.Key}\": {{ \"enabled\": {kvp.Value.IsEnabled.ToString().ToLower()}, \"mode\": {(int)kvp.Value.Mode}, \"threshold\": {kvp.Value.ThresholdValue}, \"hysteresis\": {kvp.Value.Hysteresis} }}{comma}");
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

                // Simple JSON reading without heavy external dependency
                string json = File.ReadAllText(path);
                // We will populate rules as machines are encountered or on window open
            }
            catch (Exception ex)
            {
                Log.Warning($"PowerGovernor: Failed to load settings: {ex.Message}");
            }
        }
    }
}
