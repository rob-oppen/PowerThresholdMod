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
using Mafi.Unity;
using Mafi.Unity.Ui.Hud;
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
        private GameObject m_hostObj;
        private ISimLoopEvents m_simLoopEvents;

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
                var electricityManager = resolver.TryResolve<IElectricityManager>().ValueOrNull
                    ?? (IElectricityManager)resolver.TryResolve<ElectricityManager>().ValueOrNull;

                var entitiesManager = resolver.TryResolve<EntitiesManager>().ValueOrNull
                    ?? resolver.TryResolve<IEntitiesManager>().ValueOrNull as EntitiesManager;

                m_simLoopEvents = resolver.TryResolve<ISimLoopEvents>().ValueOrNull;
                m_uiRoot = resolver.TryResolve<UiRoot>().ValueOrNull;

                if (electricityManager == null)
                {
                    Log.Error("PowerThresholdMod: IElectricityManager could not be resolved!");
                    return;
                }
                if (entitiesManager == null)
                {
                    Log.Error("PowerThresholdMod: EntitiesManager could not be resolved!");
                    return;
                }

                HudController hudController = resolver.TryResolve<HudController>().ValueOrNull;
                IRootEscapeManager escapeManager = resolver.TryResolve<IRootEscapeManager>().ValueOrNull;

                // Instantiate engine
                m_governorManager = new PowerGovernorManager(electricityManager, entitiesManager);

                // Hook simulation loop (UpdateEnd is called after entities demand power, ensuring accurate tick metrics)
                if (m_simLoopEvents != null)
                {
                    m_simLoopEvents.UpdateEnd.AddNonSaveable(this, onSimUpdate);
                    Log.Info("PowerThresholdMod: Successfully hooked simulation UpdateEnd loop via AddNonSaveable!");
                }

                // Apply Harmony patches for MachineInspector UI integration
                m_harmony = new Harmony("com.captainofindustry.powerthreshold");
                MachineInspectorPatch.ApplyPatches(m_harmony);

                // Attach persistent Unity host for per-frame keyboard updates, HUD button, and Escape key handling
                m_hostObj = GameObject.Find("PowerGovernorHost");
                if (m_hostObj == null)
                {
                    m_hostObj = new GameObject("PowerGovernorHost");
                    UnityEngine.Object.DontDestroyOnLoad(m_hostObj);
                }

                var host = m_hostObj.GetComponent<PowerGovernorHostMb>();
                if (host == null)
                {
                    host = m_hostObj.AddComponent<PowerGovernorHostMb>();
                }
                host.Initialize(hudController, m_uiRoot, escapeManager, m_protosDb, m_governorManager);

                Log.Info("PowerThresholdMod: Successfully initialized!");
            }
            catch (Exception ex)
            {
                Log.Error($"PowerThresholdMod: Initialization error: {ex}");
            }
        }

        private void onSimUpdate()
        {
            m_governorManager?.OnSimUpdate();
        }

        public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues)
        {
        }

        public void Dispose()
        {
            try
            {
                if (m_simLoopEvents != null)
                {
                    m_simLoopEvents.UpdateEnd.RemoveNonSaveable(this, onSimUpdate);
                }
                m_harmony?.UnpatchAll("com.captainofindustry.powerthreshold");
                m_governorManager?.SaveSettings();
                if (m_hostObj != null)
                {
                    UnityEngine.Object.Destroy(m_hostObj);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PowerThresholdMod: Error during dispose: {ex.Message}");
            }
        }
    }
}
