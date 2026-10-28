using Label = Mafi.Unity.UiToolkit.Library.Label;
using Slider = Mafi.Unity.UiToolkit.Library.Slider;
using Toggle = Mafi.Unity.UiToolkit.Library.Toggle;
using System;
using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Factory.Machines;
using Mafi.Core.Prototypes;
using Mafi.Localization;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using PowerThresholdMod.Engine;
using PowerThresholdMod.Model;

namespace PowerThresholdMod.UI
{
    public class PowerGovernorWindow : Window
    {
        private static PowerGovernorWindow s_instance;
        private readonly ProtosDb m_protosDb;
        private readonly PowerGovernorManager m_manager;

        private Label m_lblGridStatus;
        private Label m_lblCapacity;
        private Label m_lblDemand;
        private Label m_lblReserve;

        public static bool IsWindowOpen => s_instance != null && s_instance.IsOpen;

        public static void ToggleWindow(ProtosDb protosDb, PowerGovernorManager manager, UiRoot uiRoot)
        {
            if (IsWindowOpen)
            {
                CloseWindow();
            }
            else
            {
                OpenWindow(protosDb, manager, uiRoot);
            }
        }

        public static void OpenWindow(ProtosDb protosDb, PowerGovernorManager manager, UiRoot uiRoot)
        {
            if (s_instance == null)
            {
                s_instance = new PowerGovernorWindow(protosDb, manager);
            }

            if (!s_instance.IsOpen)
            {
                s_instance.Open(uiRoot);
            }
        }

        public static void CloseWindow()
        {
            if (s_instance != null && s_instance.IsOpen)
            {
                s_instance.Close();
            }
        }

        public PowerGovernorWindow(ProtosDb protosDb, PowerGovernorManager manager)
            : base(new LocStrFormatted("⚡ Electric Power Governor"), addFullscreenButton: false)
        {
            m_protosDb = protosDb;
            m_manager = manager;

            WindowWidth(new Px(640));
            MakeMovableAndEnablePositionSaving();
            EnablePinning();

            buildUi();
        }

        private void buildUi()
        {
            // 1. Grid Status Panel
            var headerPanel = new PanelWithHeader(new LocStrFormatted("Power Grid Status"));
            headerPanel.Body.PaddingTopBottom(3.pt()).Gap(4.px());

            m_lblGridStatus = new Label(new LocStrFormatted("Grid Status: Calculating..."));
            m_lblCapacity = new Label(new LocStrFormatted("Capacity: 0.0 MW"));
            m_lblDemand = new Label(new LocStrFormatted("Demand: 0.0 MW"));
            m_lblReserve = new Label(new LocStrFormatted("Net Headroom: 0.0 MW (0.0%)"));

            var statusRow1 = new Row().AlignItemsCenter().Gap(10.px());
            statusRow1.Add(m_lblGridStatus);

            var statusRow2 = new Row().AlignItemsCenter().Gap(16.px());
            statusRow2.Add(m_lblCapacity, m_lblDemand);

            var statusRow3 = new Row().AlignItemsCenter().Gap(10.px());
            statusRow3.Add(m_lblReserve);

            headerPanel.BodyAdd(statusRow1, statusRow2, statusRow3);
            AddPanel(headerPanel);

            // 2. Machine Thresholds Panel
            var rulesPanel = new PanelWithHeader(new LocStrFormatted("Global Machine Thresholds"));
            rulesPanel.Body.PaddingTopBottom(3.pt()).Gap(4.px());

            var scroll = new ScrollColumn().MaxHeight(new Px(400)).AlignItemsStretch();

            var electricMachineProtos = m_protosDb.Filter<MachineProto>(p => p.ElectricityConsumed.IsPositive)
                .OrderBy(p => p.Strings.Name.ToString())
                .ToList();

            foreach (var proto in electricMachineProtos)
            {
                var rule = m_manager.GetGlobalRule(proto);
                string protoName = proto.Strings.Name.ToString();

                var row = new Row().AlignItemsCenter().Gap(8.px()).PaddingTopBottom(2.px());

                var chkEnabled = new Toggle(rule.IsEnabled);
                var lblName = new Label(new LocStrFormatted(protoName)).MinWidth(new Px(170));

                var btnMode = new ButtonText(new LocStrFormatted(rule.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"), null)
                    .MinWidth(new Px(50));

                var slider = new Slider();
                slider.Range(0f, rule.Mode == ThresholdMode.Percentage ? 100f : 50f);
                slider.Value(rule.ThresholdValue, notify: false);
                slider.MinWidth(new Px(160));

                var lblValue = new Label(new LocStrFormatted(FormatRuleValue(rule))).MinWidth(new Px(75));

                chkEnabled.OnValueChanged(val =>
                {
                    rule.IsEnabled = val;
                    m_manager.SaveSettings();
                });

                slider.OnValueChanged((val, prev) =>
                {
                    rule.ThresholdValue = (float)Math.Round(val, 0);
                    lblValue.Value(new LocStrFormatted(FormatRuleValue(rule)));
                    m_manager.SaveSettings();
                });

                btnMode.OnClick(() =>
                {
                    rule.Mode = (rule.Mode == ThresholdMode.Percentage) ? ThresholdMode.MegaWatts : ThresholdMode.Percentage;
                    slider.Range(0f, rule.Mode == ThresholdMode.Percentage ? 100f : 50f);
                    btnMode.Value(new LocStrFormatted(rule.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"));
                    lblValue.Value(new LocStrFormatted(FormatRuleValue(rule)));
                    m_manager.SaveSettings();
                });

                row.Add(chkEnabled, lblName, btnMode, slider, lblValue);
                scroll.Add(row);
            }

            rulesPanel.BodyAdd(scroll);
            AddPanel(rulesPanel);
        }

        private static string FormatRuleValue(MachinePowerRule rule)
        {
            return (rule.Mode == ThresholdMode.Percentage)
                ? $"< {rule.ThresholdValue:F0}%"
                : $"< {rule.ThresholdValue:F0} MW";
        }

        public override bool InputUpdate()
        {
            bool res = base.InputUpdate();
            updateStatusText();
            return res;
        }

        private void updateStatusText()
        {
            if (m_manager == null || m_lblCapacity == null) return;

            float cap = m_manager.LastCapacityMw;
            float dem = m_manager.LastDemandedMw;
            float reserve = m_manager.LastReserveMw;
            float pct = m_manager.LastReservePercent;

            m_lblCapacity.Value(new LocStrFormatted($"Capacity: {cap:F1} MW"));
            m_lblDemand.Value(new LocStrFormatted($"Demand: {dem:F1} MW"));
            m_lblReserve.Value(new LocStrFormatted($"Net Reserve: {reserve:F1} MW ({pct:F1}%)"));

            if (reserve < 0)
            {
                m_lblGridStatus.Value(new LocStrFormatted("Grid Status: ⚠️ CRITICAL BROWNOUT"));
            }
            else if (pct < 15f)
            {
                m_lblGridStatus.Value(new LocStrFormatted("Grid Status: ⚡ HIGH LOAD (STRAINED)"));
            }
            else
            {
                m_lblGridStatus.Value(new LocStrFormatted("Grid Status: ✔ HEALTHY"));
            }
        }
    }
}
