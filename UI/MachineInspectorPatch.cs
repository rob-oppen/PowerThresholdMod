using Label = Mafi.Unity.UiToolkit.Library.Label;
using Slider = Mafi.Unity.UiToolkit.Library.Slider;
using Toggle = Mafi.Unity.UiToolkit.Library.Toggle;
using System;
using System.Reflection;
using HarmonyLib;
using Mafi.Core.Entities;
using Mafi.Core.Factory.Machines;
using Mafi.Localization;
using Mafi.Unity.UiToolkit.Library;
using PowerThresholdMod.Engine;
using PowerThresholdMod.Model;
using UnityEngine.UIElements;

namespace PowerThresholdMod.UI
{
    public static class MachineInspectorPatch
    {
        private static PanelWithHeader s_governorPanel;
        private static Toggle s_chkOverride;
        private static Slider s_slider;
        private static Label s_lblValue;
        private static Label s_lblStatus;
        private static Machine s_currentMachine;

        public static void ApplyPatches(Harmony harmony)
        {
            var inspectorType = AccessTools.TypeByName("Mafi.Unity.Ui.Inspectors.MachineInspector");
            if (inspectorType == null)
            {
                Mafi.Log.Warning("PowerThresholdMod: Could not find MachineInspector type to patch.");
                return;
            }

            var activateMethod = AccessTools.Method(inspectorType, "Activate", new[] { typeof(IEntity) });
            var postfixActivate = new HarmonyMethod(typeof(MachineInspectorPatch).GetMethod(nameof(Postfix_Activate), BindingFlags.Public | BindingFlags.Static));
            harmony.Patch(activateMethod, postfix: postfixActivate);

            var inputUpdateMethod = AccessTools.Method(inspectorType, "InputUpdate");
            var postfixUpdate = new HarmonyMethod(typeof(MachineInspectorPatch).GetMethod(nameof(Postfix_InputUpdate), BindingFlags.Public | BindingFlags.Static));
            harmony.Patch(inputUpdateMethod, postfix: postfixUpdate);

            Mafi.Log.Info("PowerThresholdMod: MachineInspector patches successfully hooked!");
        }

        public static void Postfix_Activate(object __instance, IEntity entity)
        {
            var machine = entity as Machine;
            s_currentMachine = machine;

            if (machine == null || (!machine.Prototype.ElectricityConsumed.IsPositive && !machine.ElectricityConsumer.HasValue))
            {
                if (s_governorPanel != null)
                {
                    s_governorPanel.SetVisible(false);
                }
                return;
            }

            if (__instance is Window window)
            {
                ensurePanelCreated(window);
                s_governorPanel.SetVisible(true);
                refreshControls(machine);
            }
        }

        private static void ensurePanelCreated(Window window)
        {
            if (s_governorPanel != null) return;

            s_governorPanel = new PanelWithHeader(new LocStrFormatted("⚡ Power Governor"));

            var overrideRow = new Row();
            s_chkOverride = new Toggle(false);
            var lblOverride = new Label(new LocStrFormatted("Override Global Threshold"));
            overrideRow.Add(s_chkOverride);
            overrideRow.Add(lblOverride);
            s_governorPanel.Add(overrideRow);

            var sliderRow = new Row();
            s_slider = new Slider();
            s_slider.Range(0f, 100f);
            s_lblValue = new Label(new LocStrFormatted("< 20%"));
            sliderRow.Add(s_slider);
            sliderRow.Add(s_lblValue);
            s_governorPanel.Add(sliderRow);

            s_lblStatus = new Label(new LocStrFormatted("Status: Inheriting Global Rule"));
            s_governorPanel.Add(s_lblStatus);

            s_chkOverride.OnValueChanged(val =>
            {
                if (s_currentMachine == null || PowerGovernorManager.Instance == null) return;
                var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(s_currentMachine);
                custom.HasOverride = val;
                PowerGovernorManager.Instance.SetInstanceOverride(s_currentMachine, custom);
                refreshControls(s_currentMachine);
            });

            s_slider.OnValueChanged((val, prev) =>
            {
                if (s_currentMachine == null || PowerGovernorManager.Instance == null) return;
                var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(s_currentMachine);
                custom.ThresholdValue = (float)Math.Round(val, 0);
                PowerGovernorManager.Instance.SetInstanceOverride(s_currentMachine, custom);
                if (s_lblValue.RootElement is TextElement te)
                {
                    te.text = $"< {custom.ThresholdValue:F0}%";
                }
            });

            window.AddPanel(s_governorPanel);
        }

        private static void refreshControls(Machine machine)
        {
            if (PowerGovernorManager.Instance == null || machine == null) return;

            var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
            var effective = PowerGovernorManager.Instance.GetEffectiveRule(machine);
            bool isThrottled = PowerGovernorManager.Instance.IsMachineThrottled(machine);

            s_chkOverride.Value(custom.HasOverride);
            s_slider.Value(effective.ThresholdValue, notify: false);
            s_slider.SetVisible(custom.HasOverride);
            s_lblValue.SetVisible(custom.HasOverride);

            string statusText;
            if (isThrottled)
            {
                statusText = "Status: ⏸ Auto-Throttled (Grid reserve below threshold)";
            }
            else if (custom.HasOverride)
            {
                statusText = $"Status: Custom Override (< {effective.ThresholdValue:F0}%)";
            }
            else
            {
                statusText = $"Status: Inheriting Global (< {effective.ThresholdValue:F0}%)";
            }

            if (s_lblStatus != null && s_lblStatus.RootElement is TextElement te)
            {
                te.text = statusText;
            }
        }

        public static void Postfix_InputUpdate(object __instance)
        {
            if (s_currentMachine != null && s_governorPanel != null)
            {
                refreshControls(s_currentMachine);
            }
        }
    }
}
