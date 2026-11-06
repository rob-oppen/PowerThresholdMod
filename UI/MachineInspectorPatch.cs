using Label = Mafi.Unity.UiToolkit.Library.Label;
using Toggle = Mafi.Unity.UiToolkit.Library.Toggle;
using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Factory.Machines;
using Mafi.Core.Syncers;
using Mafi.Localization;
using Mafi.Unity.Ui.Library.Inspectors;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using PowerThresholdMod.Engine;
using PowerThresholdMod.Model;

namespace PowerThresholdMod.UI
{
    public static class MachineInspectorPatch
    {
        public static void ApplyPatches(Harmony harmony)
        {
            var inspectorType = AccessTools.TypeByName("Mafi.Unity.Ui.Inspectors.MachineInspector");
            if (inspectorType == null)
            {
                Log.Warning("PowerThresholdMod: Could not find MachineInspector type to patch.");
                return;
            }

            var ctors = inspectorType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ctors.Length > 0)
            {
                var postfixCtor = new HarmonyMethod(typeof(MachineInspectorPatch).GetMethod(nameof(MachineInspector_Ctor_Postfix), BindingFlags.Public | BindingFlags.Static));
                harmony.Patch(ctors[0], postfix: postfixCtor);
                Log.Info("PowerThresholdMod: MachineInspector constructor successfully patched!");
            }
            else
            {
                Log.Warning("PowerThresholdMod: MachineInspector constructor not found.");
            }
        }

        public static void MachineInspector_Ctor_Postfix(UiComponent __instance)
        {
            try
            {
                var inspector = __instance as BaseInspector<Machine>;
                if (inspector == null) return;

                var panel = new PanelWithHeader(new LocStrFormatted("⚡ Electric Power Governor"));
                panel.Body.PaddingTopBottom(2.pt()).Gap(4.px());

                var rowOverride = new Row().AlignItemsCenter().Gap(6.px());
                var chkOverride = new Toggle(standalone: true);
                chkOverride[1].Visible(false);
                chkOverride.Width(22.px()).NoShrink();
                var lblOverride = new Label(new LocStrFormatted("Auto-Shed this machine during shortages"));
                rowOverride.Add(chkOverride, lblOverride);

                var lblPriority = new Label(new LocStrFormatted("Priority: P10")).Color(ColorRgba.LightBlue);
                var lblStatus = new Label(new LocStrFormatted("Status: Normal (Supply meets demand)"));

                panel.BodyAdd(rowOverride, lblPriority, lblStatus);
                inspector.MainBody.Add(panel);
                panel.Visible(false);

                // Wire up UI events
                chkOverride.OnValueChanged(val =>
                {
                    var machine = inspector.Entity;
                    if (machine == null || PowerGovernorManager.Instance == null) return;
                    var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
                    custom.HasOverride = true;
                    custom.IsEnabled = val;
                    PowerGovernorManager.Instance.SetInstanceOverride(machine, custom);
                    refreshMachineControls(machine, chkOverride, lblStatus, lblPriority);
                });

                // Observe machine changes when player clicks machines
                inspector.Observe(() => inspector.Entity).Do((Machine machine) =>
                {
                    if (machine == null || (!machine.Prototype.ElectricityConsumed.IsPositive && !machine.ElectricityConsumer.HasValue))
                    {
                        panel.Visible(false);
                        return;
                    }
                    panel.Visible(true);
                    refreshMachineControls(machine, chkOverride, lblStatus, lblPriority);
                });

                // Observe throttled status in sync with power grid
                inspector.Observe(() => (inspector.Entity != null && PowerGovernorManager.Instance != null) && PowerGovernorManager.Instance.IsMachineThrottled(inspector.Entity))
                    .Do((bool isThrottled) =>
                    {
                        var machine = inspector.Entity;
                        if (machine != null)
                        {
                            updateStatusText(machine, lblStatus);
                        }
                    });

                // Observe in-game General Priority changes on the machine
                inspector.Observe(() => (inspector.Entity != null) ? inspector.Entity.GeneralPriority : 10)
                    .Do((int p) =>
                    {
                        int prio = p > 0 ? p : 10;
                        lblPriority.Value(new LocStrFormatted($"In-Game Priority: P{prio}  •  (Shed: P10→P1  |  Restore: P1→P10)"));
                        lblPriority.Color(GetPriorityColor(prio));
                    });
            }
            catch (Exception ex)
            {
                Log.Error($"PowerThresholdMod: Error in MachineInspector constructor postfix: {ex}");
            }
        }

        private static void refreshMachineControls(Machine machine, Toggle chkOverride, Label lblStatus, Label lblPriority)
        {
            if (machine == null || PowerGovernorManager.Instance == null) return;

            var effective = PowerGovernorManager.Instance.GetEffectiveRule(machine);
            chkOverride.Value(effective.IsEnabled);

            int prio = machine.GeneralPriority > 0 ? machine.GeneralPriority : 10;
            lblPriority.Value(new LocStrFormatted($"In-Game Priority: P{prio}  •  (Shed: P10→P1  |  Restore: P1→P10)"));
            lblPriority.Color(GetPriorityColor(prio));

            updateStatusText(machine, lblStatus);
        }

        private static void updateStatusText(Machine machine, Label lblStatus)
        {
            if (machine == null || PowerGovernorManager.Instance == null) return;

            var effective = PowerGovernorManager.Instance.GetEffectiveRule(machine);
            bool isThrottled = PowerGovernorManager.Instance.IsMachineThrottled(machine);
            bool demandOutpacesSupply = PowerGovernorManager.Instance.LastDemandOutpacesSupply;

            if (isThrottled)
            {
                lblStatus.Value(new LocStrFormatted("Status: ⏸ Auto-Paused (Demand outpaced supply)"));
                lblStatus.Color(ColorRgba.Orange);
            }
            else if (!effective.IsEnabled)
            {
                lblStatus.Value(new LocStrFormatted("Status: Exempt from Governor (Always On)"));
                lblStatus.Color(new ColorRgba(130, 145, 165, 200));
            }
            else if (demandOutpacesSupply)
            {
                lblStatus.Value(new LocStrFormatted("Status: Running (Deficit active - protected by priority)"));
                lblStatus.Color(ColorRgba.Gold);
            }
            else
            {
                lblStatus.Value(new LocStrFormatted("Status: Normal (Supply satisfies 100% of demand)"));
                lblStatus.Color(new ColorRgba(100, 230, 120, 255));
            }
        }

        private static ColorRgba GetPriorityColor(int priority)
        {
            if (priority <= 3) return new ColorRgba(100, 230, 120, 255); // Green (High priority)
            if (priority <= 7) return new ColorRgba(245, 210, 80, 255);  // Yellow/Amber (Medium priority)
            return new ColorRgba(255, 120, 100, 255);                    // Red/Coral (Low priority)
        }
    }
}
