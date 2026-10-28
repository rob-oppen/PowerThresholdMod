using Label = Mafi.Unity.UiToolkit.Library.Label;
using Slider = Mafi.Unity.UiToolkit.Library.Slider;
using Toggle = Mafi.Unity.UiToolkit.Library.Toggle;
using System;
using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.ElectricPower;
using Mafi.Core.Factory.Machines;
using Mafi.Core.Prototypes;
using Mafi.Core.Syncers;
using Mafi.Localization;
using Mafi.Unity;
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
        private ButtonText m_btnResumeAll;
        private Label m_lblRecoveryStatus;
        private Label m_lblCapacity;
        private Label m_lblProduction;
        private Label m_lblDemand;
        private Label m_lblConsumed;

        private readonly List<Action> m_rowRefreshActions = new List<Action>();

        public static bool IsWindowOpen => s_instance != null && s_instance.IsOpen;

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
                RefreshStatusIfOpen();
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
                var concrete = em as ElectricityManager;

                Electricity cap = (concrete != null && concrete.MaxGenerationCapacity.IsPositive)
                    ? concrete.MaxGenerationCapacity
                    : em.GenerationCapacityThisTick;

                Electricity prod = (concrete != null)
                    ? concrete.GeneratedThisTick
                    : em.ConsumedThisTick;

                Electricity dem = em.DemandedThisTick;
                Electricity con = em.ConsumedThisTick;

                s_instance.UpdateStatusText(dem, con, prod, cap);

                foreach (var action in s_instance.m_rowRefreshActions)
                {
                    action();
                }
            }
        }

        public PowerGovernorWindow(ProtosDb protosDb, PowerGovernorManager manager)
            : base(new LocStrFormatted("⚡ Electric Power Governor"), addFullscreenButton: false)
        {
            m_protosDb = protosDb;
            m_manager = manager;

            // Comfortable dimensions for electrical building list, tabs, icons, and guide
            WindowSize(800.px(), 690.px());
            MakeMovableAndEnablePositionSaving();
            EnablePinning();

            buildUi();

            // Observe electricity manager changes reactively every simulation tick
            if (m_manager?.ElectricityManager != null)
            {
                var em = m_manager.ElectricityManager;
                var concrete = em as ElectricityManager;

                this.Observe(() => em.DemandedThisTick)
                    .Observe(() => em.ConsumedThisTick)
                    .Observe(() => concrete != null ? concrete.GeneratedThisTick : em.ConsumedThisTick)
                    .Observe(() => (concrete != null && concrete.MaxGenerationCapacity.IsPositive) ? concrete.MaxGenerationCapacity : em.GenerationCapacityThisTick)
                    .Do((Electricity dem, Electricity con, Electricity prod, Electricity cap) =>
                    {
                        UpdateStatusText(dem, con, prod, cap);
                    });
            }
        }

        private void buildUi()
        {
            m_rowRefreshActions.Clear();

            // 1. Grid Status Panel
            var headerPanel = new PanelWithHeader(new LocStrFormatted("Power Grid Status"));
            headerPanel.Body.PaddingTopBottom(4.pt()).Gap(4.px());

            // Fixed minimum widths prevent text changes from shifting or moving UI elements
            m_lblGridStatus = new Label(new LocStrFormatted("Grid Status: Calculating...")).MinWidth(new Px(400));
            m_btnResumeAll = new ButtonText(new LocStrFormatted("Resume All"), null).MinWidth(new Px(160));
            m_btnResumeAll.Visible(false);

            m_lblRecoveryStatus = new Label(new LocStrFormatted("Auto-Recovery: Initializing...")).MinWidth(new Px(560));

            m_lblCapacity = new Label(new LocStrFormatted("Grid Capacity: --")).MinWidth(new Px(270));
            m_lblProduction = new Label(new LocStrFormatted("Current Production: --")).MinWidth(new Px(300));
            m_lblDemand = new Label(new LocStrFormatted("Demand: --")).MinWidth(new Px(270));
            m_lblConsumed = new Label(new LocStrFormatted("Consumed: --")).MinWidth(new Px(300));

            var statusRow1 = new Row().AlignItemsCenter().Gap(12.px());
            statusRow1.Add(m_lblGridStatus, m_btnResumeAll);

            var statusRow2 = new Row().AlignItemsCenter().Gap(10.px());
            statusRow2.Add(m_lblRecoveryStatus);

            var statusRow3 = new Row().AlignItemsCenter().Gap(16.px());
            statusRow3.Add(m_lblCapacity, m_lblProduction);

            var statusRow4 = new Row().AlignItemsCenter().Gap(16.px());
            statusRow4.Add(m_lblDemand, m_lblConsumed);

            headerPanel.BodyAdd(statusRow1, statusRow2, statusRow3, statusRow4);
            AddPanel(headerPanel);

            m_btnResumeAll.OnClick(() =>
            {
                m_manager.ResumeAllThrottled();
                RefreshStatusIfOpen();
            });

            // 2. Tabbed Section: Building Rules & User Guide
            var tabContainer = new TabContainer();
            tabContainer.Height(new Px(450));

            var rulesContent = buildRulesTab();
            var guideContent = buildGuideTab();

            tabContainer.AddTab(new LocStrFormatted("⚡ Building Rules"), rulesContent, scroll: false);
            tabContainer.AddTab(new LocStrFormatted("📖 Guide & Help"), guideContent, scroll: true);

            Body.Add(tabContainer);

            // Populate initial status immediately
            if (m_manager?.ElectricityManager != null)
            {
                var em = m_manager.ElectricityManager;
                var concrete = em as ElectricityManager;
                Electricity cap = (concrete != null && concrete.MaxGenerationCapacity.IsPositive) ? concrete.MaxGenerationCapacity : em.GenerationCapacityThisTick;
                Electricity prod = (concrete != null) ? concrete.GeneratedThisTick : em.ConsumedThisTick;
                UpdateStatusText(em.DemandedThisTick, em.ConsumedThisTick, prod, cap);
            }
        }

        private UiComponent buildRulesTab()
        {
            var scroll = new ScrollColumn().MaxHeight(new Px(410)).AlignItemsStretch();

            // Filter ALL buildings with electricity consumption (Machines, Data Centers, Research Labs, Sorters, Depots, etc.)
            var electricProtos = m_protosDb.Filter<LayoutEntityProto>(p => p is IProtoWithPowerConsumption pc && pc.ElectricityConsumed.IsPositive)
                .OrderBy(p => p.Strings.Name.ToString())
                .ToList();

            foreach (var proto in electricProtos)
            {
                var rule = m_manager.GetGlobalRule(proto.Id.Value);
                string protoName = proto.Strings.Name.ToString();
                string protoId = proto.Id.Value;

                var row = new Row().AlignItemsCenter().Gap(8.px()).PaddingTopBottom(2.px());

                var chkEnabled = new Toggle(rule.IsEnabled);

                // Add official building icon
                UiComponent iconComp;
                if (proto is IProtoWithIcon pwi)
                {
                    iconComp = new Icon(pwi).Size(22.px(), 22.px());
                }
                else
                {
                    iconComp = new Icon(Assets.Unity.UserInterface.Toolbar.Machines_svg).Size(22.px(), 22.px());
                }

                var lblName = new Label(new LocStrFormatted(protoName)).MinWidth(new Px(165));

                // 65px MinWidth ensures mode text never wraps
                var btnMode = new ButtonText(new LocStrFormatted(rule.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"), null)
                    .MinWidth(new Px(65));

                float maxSliderRange = (rule.Mode == ThresholdMode.Percentage)
                    ? 100f
                    : Math.Max(100f, (float)Math.Ceiling(m_manager.LastCapacityMw / 50f) * 50f);

                var slider = new Slider();
                slider.Range(0f, maxSliderRange);
                slider.Value(rule.ThresholdValue, notify: false);
                slider.MinWidth(new Px(140));
                // Silence slider internal label to prevent layout distortion
                slider.ValueFormatter(Option.Some<Func<Percent, LocStrFormatted>>(p => LocStrFormatted.Empty));

                var lblValue = new Label(new LocStrFormatted(FormatRuleValue(rule))).MinWidth(new Px(110));
                var lblStatus = new Label(LocStrFormatted.Empty).MinWidth(new Px(120));

                chkEnabled.OnValueChanged(val =>
                {
                    rule.IsEnabled = val;
                    m_manager.SaveSettings();
                });

                // Updates threshold cleanly without mixing up percentages and megawatts
                slider.OnValueChanged((prev, current) =>
                {
                    rule.ThresholdValue = (float)Math.Round(current, 0);
                    lblValue.Value(new LocStrFormatted(FormatRuleValue(rule)));
                    m_manager.SaveSettings();
                });

                btnMode.OnClick(() =>
                {
                    // Toggle mode cleanly
                    rule.Mode = (rule.Mode == ThresholdMode.Percentage) ? ThresholdMode.MegaWatts : ThresholdMode.Percentage;

                    float newMax = (rule.Mode == ThresholdMode.Percentage)
                        ? 100f
                        : Math.Max(100f, (float)Math.Ceiling(m_manager.LastCapacityMw / 50f) * 50f);

                    slider.Range(0f, newMax);
                    slider.Value(rule.ThresholdValue, notify: false);

                    btnMode.Value(new LocStrFormatted(rule.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"));
                    lblValue.Value(new LocStrFormatted(FormatRuleValue(rule)));
                    m_manager.SaveSettings();
                });

                // Row refresh hook to update throttled counts and unpause indicators
                m_rowRefreshActions.Add(() =>
                {
                    if (m_manager.IsProtoBeingTested(protoId))
                    {
                        lblStatus.Value(new LocStrFormatted("🔄 Testing unpause..."));
                    }
                    else
                    {
                        int pausedCount = m_manager.GetThrottledCountForProto(protoId);
                        if (pausedCount > 0)
                        {
                            lblStatus.Value(new LocStrFormatted($"⏸ {pausedCount} off"));
                        }
                        else
                        {
                            lblStatus.Value(LocStrFormatted.Empty);
                        }
                    }
                });

                row.Add(chkEnabled, iconComp, lblName, btnMode, slider, lblValue, lblStatus);
                scroll.Add(row);
            }

            return scroll;
        }

        private UiComponent buildGuideTab()
        {
            var scroll = new ScrollColumn().MaxHeight(new Px(410)).AlignItemsStretch();
            var col = new Column().Padding(8.px()).Gap(10.px());

            // Header Banner
            var titleRow = new Row().AlignItemsCenter().Gap(8.px());
            titleRow.Add(new Icon(Assets.Unity.UserInterface.General.ElectricityColored_svg).Size(24.px(), 24.px()));
            titleRow.Add(new Label(new LocStrFormatted("ELECTRIC POWER GOVERNOR - COMPREHENSIVE GUIDE")).FontBold());

            // Section 1: How Thresholds Work (Easy Numbers)
            var card1 = createCard();
            var sec1Header = new Row().AlignItemsCenter().Gap(6.px());
            sec1Header.Add(new Icon(Assets.Unity.UserInterface.Toolbar.Stats_svg).Size(18.px(), 18.px()));
            sec1Header.Add(new Label(new LocStrFormatted("1. How Thresholds Work (Simple Round Numbers)")).FontBold());

            var p1 = new Label(new LocStrFormatted(
                "Suppose your island has a total generator capacity of 100 MW:\n\n" +
                "• [%] Percentage Mode: Pauses the building when power production drops below a percentage of grid capacity.\n" +
                "   - Set to 20%  -> Pauses when production drops below 20 MW (only during severe shortages).\n" +
                "   - Set to 50%  -> Pauses when production drops below 50 MW (sheds load at moderate deficits).\n" +
                "   - Set to 75%  -> Pauses when production drops below 75 MW (first to shut down as soon as power slips).\n\n" +
                "• [MW] MegaWatts Mode: Pauses when production drops below an exact megawatt number (e.g. Pause if < 30 MW)."
            ));
            card1.Add(sec1Header, p1);

            // Section 2: Priority Strategy & Tiers
            var card2 = createCard();
            var sec2Header = new Row().AlignItemsCenter().Gap(6.px());
            sec2Header.Add(new Icon(Assets.Unity.UserInterface.Toolbar.Power_svg).Size(18.px(), 18.px()));
            sec2Header.Add(new Label(new LocStrFormatted("2. Recommended Priority Strategy")).FontBold());

            var pTiersIntro = new Label(new LocStrFormatted(
                "Configure your buildings into clear priority tiers so vital infrastructure never loses power:"
            ));

            var tierAlwaysOn = createTierBox(
                iconPath: Assets.Unity.UserInterface.General.Checkmark_v2_svg,
                title: "ALWAYS ON (CRITICAL - DO NOT PAUSE)",
                buildings: "Storage, Balancers, Electric Boilers, Maintenance I & II Depots, Water Pumps, Fuel Refineries",
                desc: "• Why: If maintenance depots or water/fuel pumps lose power, machines break down and boilers starve, triggering an unrecoverable blackout death spiral.\n• Action: Leave the checkbox UNCHECKED for these so they always receive full power.",
                titleColor: ColorRgba.Green
            );

            var tier1 = createTierBox(
                iconPath: Assets.Unity.UserInterface.Toolbar.Metallurgy_svg,
                title: "TIER I - CORE SMELTERS & REFINERIES (e.g. 15% - 25% Low Threshold)",
                buildings: "Core Smelters & Blast Furnaces (Iron, Steel, Copper), Metal Casters, Oil Refinery",
                desc: "• Why: These produce the metal ingots and diesel fuel essential to manufacture Maintenance I & II parts.\n• Action: Set a low threshold (e.g. 20%) so they only shut down in severe, near-total power emergencies.",
                titleColor: ColorRgba.Gold
            );

            var tier2 = createTierBox(
                iconPath: Assets.Unity.UserInterface.Toolbar.Machines_svg,
                title: "TIER II - NON-CORE SMELTERS & SECONDARY GOODS (e.g. 35% - 50% Mid Threshold)",
                buildings: "Non-Core Smelters (Glass, Gold, Silicon), Concrete Mixers, Chemical Plants, Assemblers",
                desc: "• Why: These produce construction parts and advanced materials that are useful but not immediately life-or-death.\n• Action: Set a moderate threshold (e.g. 40%) to shed load and protect Tier I supplies during fuel dips.",
                titleColor: ColorRgba.Cyan
            );

            var tier3 = createTierBox(
                iconPath: Assets.Unity.UserInterface.Toolbar.Research_svg,
                title: "TIER III - HEAVY CONSUMERS (FIRST TO PAUSE - e.g. 60% - 80% High Threshold)",
                buildings: "Arc Furnaces, Data Centres / Mainframes (Computing), Research Labs, Sorting Plants, Lifts, Sorters",
                desc: "• Why: Arc Furnaces draw enormous electricity (1.5MW - 3MW each!), and Computing/Research can easily pause without harming factory survival.\n• Action: Set a high threshold (e.g. 65% - 75%) so they are the FIRST to pause, instantly stabilizing the grid.",
                titleColor: ColorRgba.Orange
            );

            card2.Add(sec2Header, pTiersIntro, tierAlwaysOn, tier1, tier2, tier3);

            // Section 3: How Anti-Flap Auto-Recovery Works
            var card3 = createCard();
            var sec3Header = new Row().AlignItemsCenter().Gap(6.px());
            sec3Header.Add(new Icon(Assets.Unity.UserInterface.General.InfoBubble_svg).Size(18.px(), 18.px()));
            sec3Header.Add(new Label(new LocStrFormatted("3. Anti-Flap Auto-Recovery (Demand-Driven Grids)")).FontBold());

            var p3 = new Label(new LocStrFormatted(
                "In Captain of Industry, generators only produce what buildings demand. When buildings pause, demand drops and generators throttle down.\n\n" +
                "• Stability Evaluation: When production satisfies 100% of demand (no deficit) for 10 continuous seconds, the governor safely unpauses ONE single building to test grid capacity.\n" +
                "• Test Success: If power plants handle the load smoothly, that building stays running, and 10 seconds later the next building is tested.\n" +
                "• Test Failure & Backoff: If unpausing causes a deficit (generators still lack steam/coal), the building is immediately paused within 1 second and the mod waits 20 seconds before testing again. This prevents rapid pause/unpause flickering!\n" +
                "• Manual Override: Click [Resume All] at the top anytime if you know your fuel supplies have fully recovered."
            ));
            card3.Add(sec3Header, p3);

            // Section 4: Vehicles vs Static Grid Electricity
            var card4 = createCard();
            var sec4Header = new Row().AlignItemsCenter().Gap(6.px());
            sec4Header.Add(new Icon(Assets.Unity.UserInterface.General.Warning_v2_svg).Size(18.px(), 18.px()));
            sec4Header.Add(new Label(new LocStrFormatted("4. Important: Static Buildings vs Mining Vehicles")).FontBold());

            var p4 = new Label(new LocStrFormatted(
                "• The electric grid powers static buildings, smelters, labs, computing mainframes, and conveyor equipment.\n" +
                "• Mining excavators, diggers, and haul trucks in Captain of Industry run on DIESEL (or Hydrogen), NOT the electric grid.\n" +
                "• If excavators stop, check your Fuel Stations and Oil Refinery diesel output, not the power grid!"
            ));
            card4.Add(sec4Header, p4);

            col.Add(titleRow, card1, card2, card3, card4);
            scroll.Add(col);
            return scroll;
        }

        private static Column createCard()
        {
            return new Column()
                .Padding(8.px(), 10.px())
                .Gap(6.px())
                .Background(new ColorRgba(24, 30, 42, 220))
                .Border(1.px(), color: new ColorRgba(50, 65, 90, 160), radius: 6);
        }

        private static Column createTierBox(string iconPath, string title, string buildings, string desc, ColorRgba titleColor)
        {
            var box = new Column()
                .Padding(6.px(), 8.px())
                .Gap(4.px())
                .Background(new ColorRgba(18, 22, 32, 200))
                .Border(1.px(), color: new ColorRgba(60, 75, 100, 130), radius: 4);

            var headerRow = new Row().AlignItemsCenter().Gap(6.px());
            headerRow.Add(new Icon(iconPath).Size(16.px(), 16.px()));
            var lblTitle = new Label(new LocStrFormatted(title)).FontBold().Color(titleColor);
            headerRow.Add(lblTitle);

            var lblBuildings = new Label(new LocStrFormatted($"• Buildings: {buildings}"));
            var lblDesc = new Label(new LocStrFormatted(desc));

            box.Add(headerRow, lblBuildings, lblDesc);
            return box;
        }

        public void UpdateStatusText(Electricity demand, Electricity consumed, Electricity production, Electricity capacity)
        {
            if (m_lblCapacity == null) return;

            int capKw = capacity.Value;
            int prodKw = production.Value;
            int demKw = demand.Value;
            int conKw = consumed.Value;

            float capMw = (float)capKw / 1000f;
            float prodMw = (float)prodKw / 1000f;
            float demMw = (float)demKw / 1000f;
            float conMw = (float)conKw / 1000f;

            float prodPct = (capKw > 0) ? ((float)prodKw / (float)capKw) * 100f : 0f;

            string capStr = (capKw >= 1000) ? $"{capMw:F2} MW" : capacity.Format().ToString();
            string prodStr = (prodKw >= 1000) ? $"{prodMw:F2} MW" : production.Format().ToString();
            string demStr = (demKw >= 1000) ? $"{demMw:F2} MW" : demand.Format().ToString();
            string conStr = (conKw >= 1000) ? $"{conMw:F2} MW" : consumed.Format().ToString();

            m_lblCapacity.Value(new LocStrFormatted($"Grid Capacity: {capStr}"));
            m_lblProduction.Value(new LocStrFormatted($"Current Production: {prodStr}"));
            m_lblDemand.Value(new LocStrFormatted($"Demand: {demStr}"));
            m_lblConsumed.Value(new LocStrFormatted($"Consumed: {conStr}"));

            if (prodKw < demKw)
            {
                m_lblGridStatus.Value(new LocStrFormatted($"Grid Status: Deficit is detected ({prodPct:F1}% of capacity)"));
            }
            else
            {
                m_lblGridStatus.Value(new LocStrFormatted($"Grid Status: Normal is detected ({prodPct:F1}% of capacity)"));
            }

            m_lblRecoveryStatus.Value(new LocStrFormatted($"Auto-Recovery: {m_manager.GetRecoveryStatusText()}"));

            int throttledCount = m_manager.ThrottledCount;
            if (throttledCount > 0)
            {
                m_btnResumeAll.Value(new LocStrFormatted($"Resume All ({throttledCount})"));
                m_btnResumeAll.Visible(true);
            }
            else
            {
                m_btnResumeAll.Visible(false);
            }
        }

        private static string FormatRuleValue(MachinePowerRule rule)
        {
            return (rule.Mode == ThresholdMode.Percentage)
                ? $"Pause if < {rule.ThresholdValue:F0}%"
                : $"Pause if < {rule.ThresholdValue:F0} MW";
        }
    }
}

