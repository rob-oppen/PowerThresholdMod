using Label = Mafi.Unity.UiToolkit.Library.Label;
using Slider = Mafi.Unity.UiToolkit.Library.Slider;
using Toggle = Mafi.Unity.UiToolkit.Library.Toggle;
using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
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

            // Hook MachineInspector constructor
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
                var chkOverride = new Toggle(false);
                var lblOverride = new Label(new LocStrFormatted("Override Global Threshold"));
                rowOverride.Add(chkOverride, lblOverride);

                var rowSlider = new Row().AlignItemsCenter().Gap(6.px());
                var slider = new Slider();
                slider.Range(0f, 100f);
                slider.MinWidth(new Px(140));
                slider.ValueFormatter(Option.Some<Func<Percent, LocStrFormatted>>(p => LocStrFormatted.Empty));

                var lblVal = new Label(new LocStrFormatted("< 20%")).MinWidth(new Px(65));
                var btnMode = new ButtonText(new LocStrFormatted("[ % ]"), null).MinWidth(new Px(45));
                rowSlider.Add(btnMode, slider, lblVal);

                var lblStatus = new Label(new LocStrFormatted("Status: Inheriting Global"));

                panel.BodyAdd(rowOverride, rowSlider, lblStatus);
                inspector.MainBody.Add(panel);
                panel.Visible(false);

                // Wire up UI events
                chkOverride.OnValueChanged(val =>
                {
                    var machine = inspector.Entity;
                    if (machine == null || PowerGovernorManager.Instance == null) return;
                    var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
                    custom.HasOverride = val;
                    PowerGovernorManager.Instance.SetInstanceOverride(machine, custom);
                    refreshMachineControls(machine, chkOverride, slider, lblVal, btnMode, lblStatus);
                });

                // Note: Mafi Slider passes (prevValue, newValue) to OnValueChanged
                slider.OnValueChanged((prev, current) =>
                {
                    var machine = inspector.Entity;
                    if (machine == null || PowerGovernorManager.Instance == null) return;
                    var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
                    custom.ThresholdValue = (float)Math.Round(current, 0);
                    PowerGovernorManager.Instance.SetInstanceOverride(machine, custom);
                    lblVal.Value(new LocStrFormatted(FormatRuleValue(custom)));
                });

                btnMode.OnClick(() =>
                {
                    var machine = inspector.Entity;
                    if (machine == null || PowerGovernorManager.Instance == null) return;
                    var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
                    custom.Mode = (custom.Mode == ThresholdMode.Percentage) ? ThresholdMode.MegaWatts : ThresholdMode.Percentage;
                    slider.Range(0f, custom.Mode == ThresholdMode.Percentage ? 100f : 50f);
                    btnMode.Value(new LocStrFormatted(custom.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"));
                    lblVal.Value(new LocStrFormatted(FormatRuleValue(custom)));
                    PowerGovernorManager.Instance.SetInstanceOverride(machine, custom);
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
                    refreshMachineControls(machine, chkOverride, slider, lblVal, btnMode, lblStatus);
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
            }
            catch (Exception ex)
            {
                Log.Error($"PowerThresholdMod: Error in MachineInspector constructor postfix: {ex}");
            }
        }

        private static void refreshMachineControls(Machine machine, Toggle chkOverride, Slider slider, Label lblVal, ButtonText btnMode, Label lblStatus)
        {
            if (machine == null || PowerGovernorManager.Instance == null) return;

            var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
            var effective = PowerGovernorManager.Instance.GetEffectiveRule(machine);

            chkOverride.Value(custom.HasOverride);

            slider.Range(0f, effective.Mode == ThresholdMode.Percentage ? 100f : 50f);
            slider.Value(effective.ThresholdValue, notify: false);
            slider.Visible(custom.HasOverride);

            btnMode.Value(new LocStrFormatted(effective.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"));
            btnMode.Visible(custom.HasOverride);

            lblVal.Value(new LocStrFormatted(FormatRuleValue(effective)));
            lblVal.Visible(custom.HasOverride);

            updateStatusText(machine, lblStatus);
        }

        private static void updateStatusText(Machine machine, Label lblStatus)
        {
            if (machine == null || PowerGovernorManager.Instance == null) return;

            var custom = PowerGovernorManager.Instance.GetOrCreateInstanceOverride(machine);
            var effective = PowerGovernorManager.Instance.GetEffectiveRule(machine);
            bool isThrottled = PowerGovernorManager.Instance.IsMachineThrottled(machine);

            if (isThrottled)
            {
                lblStatus.Value(new LocStrFormatted("Status: ⏸ Auto-Throttled (Grid reserve below threshold)"));
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
                ? $"< {rule.ThresholdValue:F0}%"
                : $"< {rule.ThresholdValue:F0} MW";
        }

        private static string FormatRuleValue(MachineInstanceOverride custom)
        {
            return (custom.Mode == ThresholdMode.Percentage)
                ? $"< {custom.ThresholdValue:F0}%"
                : $"< {custom.ThresholdValue:F0} MW";
        }
    }
}
