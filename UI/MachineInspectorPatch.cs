using Label = Mafi.Unity.UiToolkit.Library.Label;
using Slider = Mafi.Unity.UiToolkit.Library.Slider;
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
                var lblOverride = new Label(new LocStrFormatted("Override Global Threshold"));
                rowOverride.Add(chkOverride, lblOverride);

                var rowSlider = new Row().AlignItemsCenter().Gap(6.px());
                var btnMode = new ButtonText(new LocStrFormatted("[ % ]"), null).Width(55.px()).NoShrink();
                var slider = new Slider();
                slider.ValueFormatter(Option.Some<Func<Percent, LocStrFormatted>>(p => LocStrFormatted.Empty));
                slider[2].Visible(false);
                slider[2].Width(0.px());
                slider.Range(0f, 100f);
                slider.Width(130.px()).NoShrink();

                var lblVal = new Label(new LocStrFormatted("Pause if < 20%")).Width(115.px()).NoShrink();
                rowSlider.Add(btnMode, slider, lblVal);

                var lblStatus = new Label(new LocStrFormatted("Status: Inheriting Global"));
                var lblPriority = new Label(new LocStrFormatted("Priority: P10")).Color(ColorRgba.LightBlue);

                panel.BodyAdd(rowOverride, rowSlider, lblStatus, lblPriority);
                inspector.MainBody.Add(panel);
                panel.Visible(false);

                // Wire up UI events
                chkOverride.OnValueChanged(val =>
                {
                    var machine = inspector.Entity;
                    if (machine == null || PowerGovernorManager.Instance == null) return;
                    var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
                    custom.HasOverride = val;
                    custom.IsEnabled = val;
                    PowerGovernorManager.Instance.SetInstanceOverride(machine, custom);
                    refreshMachineControls(machine, chkOverride, slider, lblVal, btnMode, lblStatus, lblPriority);
                });

                // Note: Mafi Slider passes (prevValue, newValue) to OnValueChanged
                slider.OnValueChanged((prev, current) =>
                {
                    var machine = inspector.Entity;
                    if (machine == null || PowerGovernorManager.Instance == null) return;
                    var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
                    custom.ThresholdValue = (float)Math.Round(current, 0);
                    custom.IsEnabled = true;
                    PowerGovernorManager.Instance.SetInstanceOverride(machine, custom);
                    lblVal.Value(new LocStrFormatted(FormatRuleValue(custom)));
                    updateStatusText(machine, lblStatus);
                });

                btnMode.OnClick(() =>
                {
                    var machine = inspector.Entity;
                    if (machine == null || PowerGovernorManager.Instance == null) return;
                    var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
                    custom.Mode = (custom.Mode == ThresholdMode.Percentage) ? ThresholdMode.MegaWatts : ThresholdMode.Percentage;
                    custom.IsEnabled = true;
                    float maxRange = custom.Mode == ThresholdMode.Percentage
                        ? 100f
                        : Math.Max(100f, PowerGovernorManager.Instance.LastCapacityMw);
                    slider.Range(0f, maxRange);
                    slider.Value(custom.ThresholdValue, notify: false);
                    btnMode.Value(new LocStrFormatted(custom.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"));
                    lblVal.Value(new LocStrFormatted(FormatRuleValue(custom)));
                    PowerGovernorManager.Instance.SetInstanceOverride(machine, custom);
                    updateStatusText(machine, lblStatus);
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
                    refreshMachineControls(machine, chkOverride, slider, lblVal, btnMode, lblStatus, lblPriority);
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
                        lblPriority.Value(new LocStrFormatted($"Priority: P{prio}  •  (Paused: P10→P1  |  Restored: P1→P10)"));
                        lblPriority.Color(GetPriorityColor(prio));
                    });
            }
            catch (Exception ex)
            {
                Log.Error($"PowerThresholdMod: Error in MachineInspector constructor postfix: {ex}");
            }
        }

        private static void refreshMachineControls(Machine machine, Toggle chkOverride, Slider slider, Label lblVal, ButtonText btnMode, Label lblStatus, Label lblPriority)
        {
            if (machine == null || PowerGovernorManager.Instance == null) return;

            var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
            var effective = PowerGovernorManager.Instance.GetEffectiveRule(machine);

            chkOverride.Value(custom.HasOverride);

            float maxRange = effective.Mode == ThresholdMode.Percentage
                ? 100f
                : Math.Max(100f, PowerGovernorManager.Instance.LastCapacityMw);

            slider.Range(0f, maxRange);
            slider.Value(effective.ThresholdValue, notify: false);
            slider.Visible(custom.HasOverride);

            btnMode.Value(new LocStrFormatted(effective.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"));
            btnMode.Visible(custom.HasOverride);

            lblVal.Value(new LocStrFormatted(FormatRuleValue(effective)));
            lblVal.Visible(custom.HasOverride);

            int prio = machine.GeneralPriority > 0 ? machine.GeneralPriority : 10;
            lblPriority.Value(new LocStrFormatted($"Priority: P{prio}  •  (Paused: P10→P1  |  Restored: P1→P10)"));
            lblPriority.Color(GetPriorityColor(prio));

            updateStatusText(machine, lblStatus);
        }

        private static ColorRgba GetPriorityColor(int priority)
        {
            if (priority <= 3) return new ColorRgba(100, 230, 120, 255); // Green (High priority)
            if (priority <= 7) return new ColorRgba(245, 210, 80, 255);  // Yellow/Amber (Medium priority)
            return new ColorRgba(255, 120, 100, 255);                    // Red/Coral (Low priority)
        }

        private static void updateStatusText(Machine machine, Label lblStatus)
        {
            if (machine == null || PowerGovernorManager.Instance == null) return;

            var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
            var effective = PowerGovernorManager.Instance.GetEffectiveRule(machine);
            bool isThrottled = PowerGovernorManager.Instance.IsMachineThrottled(machine);

            float prodMw = PowerGovernorManager.Instance.LastProductionMw;
            float prodPct = PowerGovernorManager.Instance.LastProductionPercent;

            if (isThrottled)
            {
                string metricStr = (effective.Mode == ThresholdMode.Percentage) ? $"{prodPct:F1}%" : $"{prodMw:F1} MW";
                string threshStr = (effective.Mode == ThresholdMode.Percentage) ? $"{effective.ThresholdValue:F0}%" : $"{effective.ThresholdValue:F0} MW";
                lblStatus.Value(new LocStrFormatted($"Status: ⏸ Auto-Paused (Production {metricStr} < {threshStr})"));
            }
            else if (custom.HasOverride)
            {
                lblStatus.Value(new LocStrFormatted($"Status: Custom Override ({FormatRuleValue(custom)})"));
            }
            else
            {
                lblStatus.Value(new LocStrFormatted($"Status: Inheriting Global ({FormatRuleValue(effective)})"));
            }
        }

        private static string FormatRuleValue(MachinePowerRule rule)
        {
            return (rule.Mode == ThresholdMode.Percentage)
                ? $"Pause if < {rule.ThresholdValue:F0}%"
                : $"Pause if < {rule.ThresholdValue:F0} MW";
        }

        private static string FormatRuleValue(MachineInstanceOverride custom)
        {
            return (custom.Mode == ThresholdMode.Percentage)
                ? $"Pause if < {custom.ThresholdValue:F0}%"
                : $"Pause if < {custom.ThresholdValue:F0} MW";
        }
    }
}

