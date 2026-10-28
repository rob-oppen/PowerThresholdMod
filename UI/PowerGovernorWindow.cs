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
using Mafi.Unity.UiToolkit.Library;
using PowerThresholdMod.Engine;
using PowerThresholdMod.Model;
using UnityEngine.UIElements;

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

        public static void ToggleWindow(ProtosDb protosDb, PowerGovernorManager manager, UiRoot uiRoot)
        {
            if (s_instance == null)
            {
                s_instance = new PowerGovernorWindow(protosDb, manager);
            }

            if (s_instance.IsOpen)
            {
                s_instance.Close();
            }
            else
            {
                s_instance.Open(uiRoot);
            }
        }

        public PowerGovernorWindow(ProtosDb protosDb, PowerGovernorManager manager)
            : base(new LocStrFormatted("⚡ Electric Power Governor"), addFullscreenButton: false)
        {
            m_protosDb = protosDb;
            m_manager = manager;

            WindowWidth(new Px(580));
            // max height auto
            MakeMovableAndEnablePositionSaving();
            EnablePinning();

            buildUi();
        }

        private void buildUi()
        {
            // 1. Grid Status Panel
            var headerPanel = new PanelWithHeader(new LocStrFormatted("Grid Metrics"));
            m_lblGridStatus = new Label(new LocStrFormatted("Grid Status: Calculating..."));
            m_lblCapacity = new Label(new LocStrFormatted("Capacity: 0.0 MW"));
            m_lblDemand = new Label(new LocStrFormatted("Demand: 0.0 MW"));
            m_lblReserve = new Label(new LocStrFormatted("Net Headroom: 0.0 MW (0.0%)"));

            var statusRow1 = new Row();
            statusRow1.Add(m_lblGridStatus);
            headerPanel.Add(statusRow1);

            var statusRow2 = new Row();
            statusRow2.Add(m_lblCapacity);
            statusRow2.Add(m_lblDemand);
            headerPanel.Add(statusRow2);

            var statusRow3 = new Row();
            statusRow3.Add(m_lblReserve);
            headerPanel.Add(statusRow3);

            AddPanel(headerPanel);

            // 2. Machine Thresholds Panel
            var rulesPanel = new PanelWithHeader(new LocStrFormatted("Global Machine Thresholds"));

            // Enumerate all electricity consuming machine prototypes
            var electricMachineProtos = m_protosDb.Filter<MachineProto>(p => p.ElectricityConsumed.IsPositive)
                .OrderBy(p => p.Strings.Name.ToString())
                .ToList();

            foreach (var proto in electricMachineProtos)
            {
                var rule = m_manager.GetGlobalRule(proto);
                string protoName = proto.Strings.Name.ToString();

                var row = new Row();
                var chkEnabled = new Toggle(rule.IsEnabled);
                var lblName = new Label(new LocStrFormatted(protoName));

                var slider = new Slider();
                slider.Range(0f, 100f);
                slider.Value(rule.ThresholdValue, notify: false);

                var lblValue = new Label(new LocStrFormatted(FormatRuleValue(rule)));

                var btnMode = new ButtonText(new LocStrFormatted(rule.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"), () =>
                {
                    rule.Mode = (rule.Mode == ThresholdMode.Percentage) ? ThresholdMode.MegaWatts : ThresholdMode.Percentage;
                    m_manager.SaveSettings();
                });

                chkEnabled.OnValueChanged(val =>
                {
                    rule.IsEnabled = val;
                    m_manager.SaveSettings();
                });

                slider.OnValueChanged((val, prev) =>
                {
                    rule.ThresholdValue = (float)Math.Round(val, 0);
                    if (lblValue.RootElement is TextElement te)
                    {
                        te.text = FormatRuleValue(rule);
                    }
                    m_manager.SaveSettings();
                });

                row.Add(chkEnabled);
                row.Add(lblName);
                row.Add(slider);
                row.Add(lblValue);
                row.Add(btnMode);

                rulesPanel.Add(row);
            }

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

            if (m_lblCapacity.RootElement is TextElement teCap) teCap.text = $"Capacity: {cap:F1} MW";
            if (m_lblDemand.RootElement is TextElement teDem) teDem.text = $"Demand: {dem:F1} MW";
            if (m_lblReserve.RootElement is TextElement teRes) teRes.text = $"Net Reserve: {reserve:F1} MW ({pct:F1}%)";

            if (m_lblGridStatus.RootElement is TextElement teStat)
            {
                if (reserve < 0)
                {
                    teStat.text = "Grid Status: ⚠ CRITICAL BROWNOUT";
                }
                else if (pct < 15f)
                {
                    teStat.text = "Grid Status: ⚡ HIGH LOAD (STRAINED)";
                }
                else
                {
                    teStat.text = "Grid Status: ✔ HEALTHY";
                }
            }
        }
    }
}
