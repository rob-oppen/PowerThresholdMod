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
using Mafi.Core.Factory.Recipes;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
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
        public Dictionary<string, RecipeOutputRule> RecipeOutputRules { get; } = new Dictionary<string, RecipeOutputRule>();
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
        public IReadOnlyCollection<int> ThrottledEntityIds => m_throttledByMod;
        public int LastTestedMachineId => m_lastTestedMachineId;
        public int LastTestedMachinePriority { get; private set; } = -1;
        public string LastTestedMachineName { get; private set; } = "";
        public string LastTestedProtoId { get; private set; } = "";

        public int RecoveryStableSeconds => Math.Min(10, m_stableStepCounter / 20);
        public int RecoveryCooldownSeconds => Math.Max(0, (m_cooldownUntilStep - m_currentSimStep + 19) / 20);

        public float LastCapacityMw { get; private set; }
        public float LastProductionMw { get; private set; }
        public float LastDemandedMw { get; private set; }
        public float LastConsumedMw { get; private set; }
        public float LastDeficitMw { get; private set; }
        public float LastSurplusMw { get; private set; }
        public bool LastDemandOutpacesSupply { get; private set; }

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

        public static int GetEntityPriority(LayoutEntity entity)
        {
            if (entity == null) return 1;
            // In Captain of Industry, GeneralPriority is 0-indexed:
            // 0 is UI Priority 1 (highest priority), 9 is UI Priority 10, etc.
            // UI Priority number is entity.GeneralPriority + 1.
            return entity.GeneralPriority + 1;
        }

        public MachinePowerRule GetGlobalRule(string protoId)
        {
            if (!GlobalRules.TryGetValue(protoId, out var rule))
            {
                rule = new MachinePowerRule
                {
                    IsEnabled = false // off by default until player selects this building
                };
                GlobalRules[protoId] = rule;
            }
            return rule;
        }

        public MachinePowerRule GetGlobalRule(LayoutEntityProto proto) => GetGlobalRule(proto.Id.Value);
        public MachinePowerRule GetGlobalRule(MachineProto proto) => GetGlobalRule(proto.Id.Value);

        public RecipeOutputRule GetRecipeOutputRule(string productId)
        {
            if (!RecipeOutputRules.TryGetValue(productId, out var rule))
            {
                rule = new RecipeOutputRule
                {
                    IsEnabled = false // off by default until player selects this recipe output
                };
                RecipeOutputRules[productId] = rule;
            }
            return rule;
        }

        public RecipeOutputRule GetRecipeOutputRule(ProductProto product) => GetRecipeOutputRule(product.Id.Value);

        public static List<ProductProto> GetActiveOutputProducts(Machine machine)
        {
            var list = new List<ProductProto>();
            if (machine == null) return list;

            var seen = new HashSet<ProductProto>();

            if (machine.RecipesAssigned.IsNotEmpty())
            {
                foreach (var recipe in machine.RecipesAssigned)
                {
                    foreach (var output in recipe.AllUserVisibleOutputs)
                    {
                        if (output.Product != null && !output.Product.Id.Value.StartsWith("__PHANTOM__") && seen.Add(output.Product))
                        {
                            list.Add(output.Product);
                        }
                    }
                }
            }
            else if (machine.LastRecipeInProgress.HasValue)
            {
                foreach (var output in machine.LastRecipeInProgress.Value.AllUserVisibleOutputs)
                {
                    if (output.Product != null && !output.Product.Id.Value.StartsWith("__PHANTOM__") && seen.Add(output.Product))
                    {
                        list.Add(output.Product);
                    }
                }
            }
            else if (machine.Prototype.Recipes.IsNotEmpty())
            {
                foreach (var recipe in machine.Prototype.Recipes)
                {
                    foreach (var output in recipe.AllUserVisibleOutputs)
                    {
                        if (output.Product != null && !output.Product.Id.Value.StartsWith("__PHANTOM__") && seen.Add(output.Product))
                        {
                            list.Add(output.Product);
                        }
                    }
                }
            }

            return list;
        }

        public static string GetActiveRecipeOutputsSummary(Machine machine)
        {
            var products = GetActiveOutputProducts(machine);
            if (products.Count == 0) return "";
            return string.Join(", ", products.Select(p => p.Strings.Name.ToString()));
        }

        public MachinePowerRule GetEffectiveRule(LayoutEntity entity)
        {
            int entityId = entity.Id.Value;
            if (InstanceOverrides.TryGetValue(entityId, out var custom) && custom.HasOverride)
            {
                return new MachinePowerRule
                {
                    IsEnabled = custom.IsEnabled
                };
            }

            // Check Building Global Rule
            string protoId = entity.Prototype.Id.Value;
            bool buildingEnabled = GlobalRules.TryGetValue(protoId, out var bRule) && bRule.IsEnabled;

            // Check Recipe Output Rules
            bool recipeEnabled = false;
            if (entity is Machine machine)
            {
                var outputs = GetActiveOutputProducts(machine);
                foreach (var product in outputs)
                {
                    if (RecipeOutputRules.TryGetValue(product.Id.Value, out var rRule) && rRule.IsEnabled)
                    {
                        recipeEnabled = true;
                        break;
                    }
                }
            }

            return new MachinePowerRule
            {
                IsEnabled = buildingEnabled || recipeEnabled
            };
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
                return "All selected buildings running";
            }
            if (m_lastTestedMachineId > 0)
            {
                int sec = Math.Max(1, (m_testExpiresAtStep - m_currentSimStep + 19) / 20);
                string prioStr = LastTestedMachinePriority > 0 ? $" [P{LastTestedMachinePriority}]" : "";
                return $"🔄 Testing unpause of {LastTestedMachineName}{prioStr}... ({sec}s)";
            }
            if (m_currentSimStep < m_cooldownUntilStep)
            {
                return $"⚠️ Shortage detected. Backing off ({RecoveryCooldownSeconds}s left)...";
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
            LastTestedMachinePriority = -1;
            LastTestedMachineName = "";
            LastTestedProtoId = "";
            m_testExpiresAtStep = 0;
            m_stableStepCounter = 0;
            Log.Info("PowerGovernor: Manually resumed all throttled entities.");
        }

        public void ResumeSingleBuilding(int entityId)
        {
            if (m_entitiesManager == null) return;

            if (entityId == m_lastTestedMachineId)
            {
                m_lastTestedMachineId = -1;
                LastTestedMachinePriority = -1;
                LastTestedMachineName = "";
                LastTestedProtoId = "";
            }

            if (m_throttledByMod.Remove(entityId))
            {
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(entityId));
                if (entityOpt.HasValue)
                {
                    entityOpt.Value.SetPaused(false);
                    string protoId = entityOpt.Value.Prototype.Id.Value;
                    if (m_throttledCountsByProto.TryGetValue(protoId, out int cnt) && cnt > 0)
                    {
                        m_throttledCountsByProto[protoId] = cnt - 1;
                    }
                    Log.Info($"PowerGovernor: Manually resumed single building #{entityId}.");
                }
            }
        }

        public void ResumeAllOfProto(string protoId)
        {
            if (m_entitiesManager == null) return;

            if (LastTestedProtoId == protoId)
            {
                m_lastTestedMachineId = -1;
                LastTestedMachinePriority = -1;
                LastTestedMachineName = "";
                LastTestedProtoId = "";
            }

            var toRemove = new List<int>();
            foreach (int id in m_throttledByMod)
            {
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                if (entityOpt.HasValue && entityOpt.Value.Prototype.Id.Value == protoId)
                {
                    var rule = GetEffectiveRule(entityOpt.Value);
                    if (!rule.IsEnabled)
                    {
                        toRemove.Add(id);
                    }
                }
            }

            foreach (int id in toRemove)
            {
                m_throttledByMod.Remove(id);
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                if (entityOpt.HasValue)
                {
                    entityOpt.Value.SetPaused(false);
                }
            }

            m_throttledCountsByProto.Remove(protoId);
            Log.Info($"PowerGovernor: Manually resumed all ({toRemove.Count}) paused buildings of prototype '{protoId}'.");
        }

        public void ResumeAllOfRecipeOutput(string productId)
        {
            if (m_entitiesManager == null) return;

            var toRemove = new List<int>();
            foreach (int id in m_throttledByMod)
            {
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                if (entityOpt.HasValue && entityOpt.Value is Machine machine)
                {
                    bool hasProduct = GetActiveOutputProducts(machine).Any(p => p.Id.Value == productId);
                    if (hasProduct)
                    {
                        var rule = GetEffectiveRule(machine);
                        if (!rule.IsEnabled)
                        {
                            toRemove.Add(id);
                        }
                    }
                }
            }

            foreach (int id in toRemove)
            {
                m_throttledByMod.Remove(id);
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                if (entityOpt.HasValue)
                {
                    entityOpt.Value.SetPaused(false);
                }
            }

            Log.Info($"PowerGovernor: Manually resumed all ({toRemove.Count}) paused buildings for recipe output '{productId}'.");
        }

        public List<ThrottledBuildingInfo> GetThrottledBuildings()
        {
            var list = new List<ThrottledBuildingInfo>();
            if (m_entitiesManager == null) return list;

            foreach (int id in m_throttledByMod)
            {
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                if (!entityOpt.HasValue) continue;

                var entity = entityOpt.Value;
                bool isLocal = InstanceOverrides.TryGetValue(id, out var custom) && custom.HasOverride;
                var rule = GetEffectiveRule(entity);
                string recipeSummary = (entity is Machine m) ? GetActiveRecipeOutputsSummary(m) : "";

                list.Add(new ThrottledBuildingInfo
                {
                    Entity = entity,
                    Name = entity.Prototype.Strings.Name.ToString(),
                    ProtoId = entity.Prototype.Id.Value,
                    Proto = entity.Prototype,
                    IsLocalOverride = isLocal,
                    EffectiveRule = rule,
                    GeneralPriority = GetEntityPriority(entity),
                    ActiveRecipeOutputsText = recipeSummary
                });
            }

            return list.OrderBy(b => b.Name).ThenBy(b => b.GeneralPriority).ThenBy(b => b.Entity.Id.Value).ToList();
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
                var rule = GetEffectiveRule(entity);
                custom = new MachineInstanceOverride
                {
                    HasOverride = false,
                    IsEnabled = rule.IsEnabled
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

            // Supply capacity: MaxGenerationCapacity if available, otherwise GenerationCapacityThisTick
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

            // Demand outpaces supply if demanded exceeds generation capacity OR if demanded exceeds delivered power
            bool demandOutpacesSupply = (demanded.Value > capacity.Value) || (demanded.Value > consumed.Value);
            LastDemandOutpacesSupply = demandOutpacesSupply;

            Electricity effectiveSupply = (capacity.Value < consumed.Value && capacity.IsPositive) ? capacity : consumed;
            if (effectiveSupply.Value <= 0 && capacity.IsPositive) effectiveSupply = capacity;

            Electricity deficit = (demanded.Value > effectiveSupply.Value)
                ? (demanded - effectiveSupply)
                : Electricity.Zero;

            LastDeficitMw = (float)deficit.Value / 1000f;
            LastSurplusMw = Math.Max(0f, (float)(capacity.Value - demanded.Value) / 1000f);

            // Handle test-unpause validation and grid stability tracking
            if (demandOutpacesSupply)
            {
                // Demand outpaces supply - reset stability counter
                m_stableStepCounter = 0;

                // If a test unpause was in progress, it failed!
                if (m_lastTestedMachineId > 0)
                {
                    var testedOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(m_lastTestedMachineId));
                    if (testedOpt.HasValue && testedOpt.Value.IsNotPaused)
                    {
                        testedOpt.Value.SetPaused(true);
                        m_throttledByMod.Add(m_lastTestedMachineId);
                        Log.Warning($"PowerGovernor: Test unpause of [P{LastTestedMachinePriority}] {LastTestedMachineName} caused demand to outpace supply. Re-pausing and setting cooldown.");
                    }
                    m_lastTestedMachineId = -1;
                    LastTestedMachinePriority = -1;
                    LastTestedMachineName = "";
                    LastTestedProtoId = "";
                    m_cooldownUntilStep = m_currentSimStep + 400; // 20s cooldown
                }
            }
            else
            {
                // Supply meets demand comfortably
                m_stableStepCounter++;

                // If test unpause observation period completed successfully
                if (m_lastTestedMachineId > 0 && m_currentSimStep >= m_testExpiresAtStep)
                {
                    Log.Info($"PowerGovernor: Test unpause of [P{LastTestedMachinePriority}] {LastTestedMachineName} succeeded. Building remains active.");
                    m_lastTestedMachineId = -1;
                    LastTestedMachinePriority = -1;
                    LastTestedMachineName = "";
                    LastTestedProtoId = "";
                }
            }

            // Periodic evaluation of configured rules (every ~1s, or every 5 steps if actively in shortage)
            m_stepCounter++;
            bool shouldCheck = (m_stepCounter % CHECK_INTERVAL_STEPS == 0) || (demandOutpacesSupply && m_stepCounter % 5 == 0);
            if (shouldCheck)
            {
                m_throttledCountsByProto.Clear();

                // 1. Maintain throttled list: clean up demolished, player-resumed, or disabled-rule entities
                var toRemove = new List<int>();
                foreach (int id in m_throttledByMod)
                {
                    if (id == m_lastTestedMachineId) continue;

                    var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                    if (!entityOpt.HasValue)
                    {
                        toRemove.Add(id);
                        continue;
                    }

                    var entity = entityOpt.Value;
                    var rule = GetEffectiveRule(entity);
                    if (!rule.IsEnabled)
                    {
                        entity.SetPaused(false);
                        toRemove.Add(id);
                        continue;
                    }

                    if (entity.IsNotPaused)
                    {
                        toRemove.Add(id); // Player manually resumed in-game
                        continue;
                    }

                    string pid = entity.Prototype.Id.Value;
                    m_throttledCountsByProto[pid] = m_throttledCountsByProto.TryGetValue(pid, out int cnt) ? cnt + 1 : 1;
                }

                foreach (int id in toRemove)
                {
                    m_throttledByMod.Remove(id);
                }

                // 2. ONLY engage power check when demand outpaces supply!
                if (demandOutpacesSupply)
                {
                    var allEntities = m_entitiesManager.GetAllEntitiesOfType<LayoutEntity>();
                    var candidates = new List<LayoutEntity>();

                    foreach (var entity in allEntities)
                    {
                        if (!entity.CanBePaused || !entity.IsNotPaused) continue;

                        var rule = GetEffectiveRule(entity);
                        if (!rule.IsEnabled) continue;

                        candidates.Add(entity);
                    }

                    // 3. PRIORITY SHEDDING: Pause selected buildings based on priority number (P10 down to P1) until demand and supply match!
                    if (candidates.Count > 0)
                    {
                        var orderedCandidates = candidates
                            .OrderByDescending(e => GetEntityPriority(e))
                            .ThenBy(e => e.Id.Value)
                            .ToList();

                        Electricity remainingDeficit = deficit.IsPositive ? deficit : Electricity.FromKw(1000);

                        foreach (var candidate in orderedCandidates)
                        {
                            int cid = candidate.Id.Value;
                            int prio = GetEntityPriority(candidate);
                            string pid = candidate.Prototype.Id.Value;

                            m_throttledByMod.Add(cid);
                            candidate.SetPaused(true);
                            m_throttledCountsByProto[pid] = m_throttledCountsByProto.TryGetValue(pid, out int cnt) ? cnt + 1 : 1;

                            Electricity pwr = (candidate is Machine m && m.PowerRequired.IsPositive)
                                ? m.PowerRequired
                                : (candidate.Prototype is IProtoWithPowerConsumption pwc ? pwc.ElectricityConsumed : Electricity.Zero);

                            Log.Info($"PowerGovernor: Demand outpaced supply. Throttled [P{prio}] {candidate.Prototype.Strings.Name} (#{cid}) - Shed: {pwr.Format()}");

                            remainingDeficit -= pwr;
                            if (!remainingDeficit.IsPositive)
                            {
                                break; // Demand and supply are matched!
                            }
                        }
                    }
                }
            }

            // AUTO-RECOVERY: If supply satisfies 100% of demand stably for ~10s and no test is running
            if (m_throttledByMod.Count > 0 &&
                !demandOutpacesSupply &&
                m_stableStepCounter >= 200 &&
                m_currentSimStep >= m_cooldownUntilStep &&
                m_lastTestedMachineId <= 0)
            {
                attemptTestUnpause();
            }
        }

        private void attemptTestUnpause()
        {
            // PRIORITY RESTORATION: Slowly enable machines in reverse based on priority 1 to 10
            int bestCandidateId = -1;
            int bestPriority = int.MaxValue; // Lower number = higher priority in COI (1 is highest)

            foreach (int id in m_throttledByMod)
            {
                var entityOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(id));
                if (!entityOpt.HasValue) continue;

                var entity = entityOpt.Value;
                int prio = GetEntityPriority(entity);

                // Priority 1 first, then 2, 3... up to 10
                if (prio < bestPriority)
                {
                    bestPriority = prio;
                    bestCandidateId = id;
                }
            }

            if (bestCandidateId > 0)
            {
                var candidateOpt = m_entitiesManager.GetEntity<LayoutEntity>(new EntityId(bestCandidateId));
                if (candidateOpt.HasValue)
                {
                    m_lastTestedMachineId = bestCandidateId;
                    LastTestedMachinePriority = bestPriority;
                    LastTestedMachineName = candidateOpt.Value.Prototype.Strings.Name.ToString();
                    LastTestedProtoId = candidateOpt.Value.Prototype.Id.Value;
                    m_testExpiresAtStep = m_currentSimStep + 60; // 3 seconds observation window
                    m_stableStepCounter = 0;

                    m_throttledByMod.Remove(bestCandidateId);
                    candidateOpt.Value.SetPaused(false);
                    Log.Info($"PowerGovernor: Grid stable for 10s. Testing unpause of [P{bestPriority}] {LastTestedMachineName} (#{bestCandidateId}).");
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
                            "    \"{0}\": {{ \"enabled\": {1} }}{2}",
                            kvp.Key, kvp.Value.IsEnabled.ToString().ToLower(), comma));
                    }
                    writer.WriteLine("  },");
                    writer.WriteLine("  \"recipeRules\": {");
                    int j = 0;
                    foreach (var kvp in RecipeOutputRules)
                    {
                        string comma = (++j < RecipeOutputRules.Count) ? "," : "";
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "    \"{0}\": {{ \"enabled\": {1} }}{2}",
                            kvp.Key, kvp.Value.IsEnabled.ToString().ToLower(), comma));
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
                var regex = new Regex("\"([^\"]+)\":\\s*\\{[^}]*\"enabled\":\\s*(true|false)");

                string currentSection = "rules";

                foreach (string line in lines)
                {
                    if (line.Contains("\"rules\""))
                    {
                        currentSection = "rules";
                    }
                    else if (line.Contains("\"recipeRules\""))
                    {
                        currentSection = "recipeRules";
                    }

                    var match = regex.Match(line);
                    if (match.Success)
                    {
                        string id = match.Groups[1].Value;
                        bool enabled = bool.Parse(match.Groups[2].Value);

                        if (currentSection == "recipeRules")
                        {
                            RecipeOutputRules[id] = new RecipeOutputRule
                            {
                                IsEnabled = enabled
                            };
                        }
                        else
                        {
                            GlobalRules[id] = new MachinePowerRule
                            {
                                IsEnabled = enabled
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PowerGovernor: Failed to load settings: {ex.Message}");
            }
        }
    }

    public struct ThrottledBuildingInfo
    {
        public LayoutEntity Entity;
        public string Name;
        public string ProtoId;
        public LayoutEntityProto Proto;
        public bool IsLocalOverride;
        public MachinePowerRule EffectiveRule;
        public int GeneralPriority;
        public string ActiveRecipeOutputsText;
    }
}
