using Label = Mafi.Unity.UiToolkit.Library.Label;
using Slider = Mafi.Unity.UiToolkit.Library.Slider;
using Toggle = Mafi.Unity.UiToolkit.Library.Toggle;
using System;
using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Factory.ElectricPower;
using Mafi.Core.Factory.Machines;
using Mafi.Core.Prototypes;
using Mafi.Core.Syncers;
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
                RefreshStatusIfOpen();
            }
        }

        public static void CloseWindow()
        {
            if (s_instance != null && s_instance.IsOpen)
            {
                s_instance.Close();
            }
        }

        public static void RefreshStatusIfOpen()
        {
            if (s_instance != null && s_instance.IsOpen && s_instance.m_manager?.ElectricityManager != null)
            {
                var em = s_instance.m_manager.ElectricityManager;
                s_instance.UpdateStatusText(em.DemandedThisTick, em.ConsumedThisTick, em.GenerationCapacityThisTick);
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

            // Observe electricity manager changes reactively every simulation tick
            if (m_manager?.ElectricityManager != null)
            {
                this.Observe(() => m_manager.ElectricityManager.DemandedThisTick)
                    .Observe(() => m_manager.ElectricityManager.ConsumedThisTick)
                    .Observe(() => m_manager.ElectricityManager.GenerationCapacityThisTick)
                    .Do((Electricity demand, Electricity consumed, Electricity capacity) =>
                    {
                        UpdateStatusText(demand, consumed, capacity);
                    });
            }
        }

        private void buildUi()
        {
            // 1. Grid Status Panel
            var headerPanel = new PanelWithHeader(new LocStrFormatted("Power Grid Status"));
            headerPanel.Body.PaddingTopBottom(3.pt()).Gap(4.px());

            m_lblGridStatus = new Label(new LocStrFormatted("Grid Status: Calculating..."));
            m_lblCapacity = new Label(new LocStrFormatted("Capacity: --"));
            m_lblDemand = new Label(new LocStrFormatted("Demand: --"));
            m_lblReserve = new Label(new LocStrFormatted("Net Headroom: --"));

            var statusRow1 = new Row().AlignItemsCenter().Gap(10.px());
            statusRow1.Add(m_lblGridStatus);

            var statusRow2 = new Row().AlignItemsCenter().Gap(16.px());
            statusRow2.Add(m_lblCapacity, m_lblDemand);

            var statusRow3 = new Row().AlignItemsCenter().Gap(10.px());
            statusRow3.Add(m_lblReserve);

            headerPanel.BodyAdd(statusRow1, statusRow2, statusRow3);
            AddPanel(headerPanel);

            // Populate initial status immediately
            if (m_manager?.ElectricityManager != null)
            {
                var em = m_manager.ElectricityManager;
                UpdateStatusText(em.DemandedThisTick, em.ConsumedThisTick, em.GenerationCapacityThisTick);
            }

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
                slider.MinWidth(new Px(150));
                // Silence slider's redundant internal label so only our descriptive label is shown
                slider.ValueFormatter(Option.Some<Func<Percent, LocStrFormatted>>(p => LocStrFormatted.Empty));

                var lblValue = new Label(new LocStrFormatted(FormatRuleValue(rule))).MinWidth(new Px(75));

                chkEnabled.OnValueChanged(val =>
                {
                    rule.IsEnabled = val;
                    m_manager.SaveSettings();
                });

                // Note: Mafi Slider passes (prevValue, newValue) to OnValueChanged
                slider.OnValueChanged((prev, current) =>
                {
                    rule.ThresholdValue = (float)Math.Round(current, 0);
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

        public void UpdateStatusText(Electricity demand, Electricity consumed, Electricity capacity)
        {
            if (m_lblCapacity == null) return;

            Electricity reserve = capacity - demand;
            int capKw = capacity.Value;
            int demKw = demand.Value;
            int resKw = reserve.Value;
            float pct = capKw > 0 ? ((float)resKw / (float)capKw) * 100f : 0f;

            float capMw = (float)capKw / 1000f;
            float demMw = (float)demKw / 1000f;
            float resMw = (float)resKw / 1000f;

            string capStr = (capKw >= 1000) ? $"{capMw:F2} MW ({capacity.Format()})" : capacity.Format().ToString();
            string demStr = (demKw >= 1000) ? $"{demMw:F2} MW ({demand.Format()})" : demand.Format().ToString();
            string resStr = (resKw >= 1000) ? $"{resMw:F2} MW ({pct:F1}%)" : $"{reserve.Format()} ({pct:F1}%)";

            m_lblCapacity.Value(new LocStrFormatted($"Capacity: {capStr}"));
            m_lblDemand.Value(new LocStrFormatted($"Demand: {demStr}"));
            m_lblReserve.Value(new LocStrFormatted($"Net Reserve: {resStr}"));

            if (resKw < 0)
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

        private static string FormatRuleValue(MachinePowerRule rule)
        {
            return (rule.Mode == ThresholdMode.Percentage)
                ? $"< {rule.ThresholdValue:F0}%"
                : $"< {rule.ThresholdValue:F0} MW";
        }
    }
}
