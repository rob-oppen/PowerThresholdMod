using System;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Factory.ElectricPower;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;
using Mafi.Unity.UiToolkit;
using PowerThresholdMod.Engine;
using PowerThresholdMod.UI;
using UnityEngine;

namespace PowerThresholdMod
{
    public sealed class PowerThresholdModEntry : IMod
    {
        public ModManifest Manifest { get; }
        public bool IsUiOnly => false;
        public Option<IConfig> ModConfig => Option<IConfig>.None;
        public ModJsonConfig JsonConfig { get; }

        private PowerGovernorManager m_governorManager;
        private ProtosDb m_protosDb;
        private UiRoot m_uiRoot;
        private Harmony m_harmony;

        public PowerThresholdModEntry(ModManifest manifest)
        {
            Manifest = manifest;
            JsonConfig = new ModJsonConfig(this);
            Log.Info("PowerThresholdMod: Instantiated!");
        }

        public void RegisterPrototypes(ProtoRegistrator registrator)
        {
        }

        public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded)
        {
            m_protosDb = protosDb;
        }

        public void EarlyInit(DependencyResolver resolver)
        {
        }

        public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
        {
            Log.Info("PowerThresholdMod: Initializing Governor...");

            try
            {
                var electricityManager = resolver.GetResolvedInstance<IElectricityManager>().Value;
                var entitiesManager = resolver.GetResolvedInstance<EntitiesManager>().Value;
                var simLoopEvents = resolver.GetResolvedInstance<ISimLoopEvents>().Value;
                m_uiRoot = resolver.GetResolvedInstance<UiRoot>().Value;

                // Instantiate engine
                m_governorManager = new PowerGovernorManager(electricityManager, entitiesManager);

                // Hook game simulation update loop
                simLoopEvents.Update.Add(this, m_governorManager.OnSimUpdate);

                // Apply Harmony patches for MachineInspector UI integration
                m_harmony = new Harmony("com.captainofindustry.powerthreshold");
                MachineInspectorPatch.ApplyPatches(m_harmony);

                // Hook keyboard shortcut (Shift + P) to open the Manager Window
                simLoopEvents.Update.Add(this, checkKeyboardShortcut);

                Log.Info("PowerThresholdMod: Successfully initialized!");
            }
            catch (Exception ex)
            {
                Log.Error($"PowerThresholdMod: Initialization error: {ex}");
            }
        }

        private void checkKeyboardShortcut()
        {
            if ((Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) && Input.GetKeyDown(KeyCode.P))
            {
                if (m_protosDb != null && m_governorManager != null && m_uiRoot != null)
                {
                    PowerGovernorWindow.ToggleWindow(m_protosDb, m_governorManager, m_uiRoot);
                }
            }
        }

        public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues)
        {
        }

        public void Dispose()
        {
            try
            {
                m_harmony?.UnpatchAll("com.captainofindustry.powerthreshold");
                m_governorManager?.SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Warning($"PowerThresholdMod: Error during dispose: {ex.Message}");
            }
        }
    }
}
