using Label = Mafi.Unity.UiToolkit.Library.Label;
using Toggle = Mafi.Unity.UiToolkit.Library.Toggle;
using Display = Mafi.Unity.Ui.Library.Display;
using System;
using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.ElectricPower;
using Mafi.Core.Factory.Machines;
using Mafi.Core.Factory.Recipes;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Core.Syncers;
using Mafi.Localization;
using Mafi.Unity;
using Mafi.Unity.Camera;
using Mafi.Unity.Ui;
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
        private readonly InspectorsManager m_inspectorsManager;
        private readonly CameraController m_cameraController;

        private Label m_lblGridStatus;
        private ButtonText m_btnResumeAll;
        private Label m_lblRecoveryStatus;
        private Label m_lblCapacity;
        private Label m_lblProduction;
        private Label m_lblDemand;
        private Label m_lblConsumed;

        // Paused Buildings Tab UI components
        private Column m_pausedRowsContainer;
        private Label m_lblPausedSummary;
        private ButtonText m_btnResumeAllInTab;
        private int m_lastRenderedPausedHash = -1;

        private readonly List<Action> m_rowRefreshActions = new List<Action>();
        private Dictionary<IEntityProto, ElectricityManager.ConsumptionPerProto> m_cachedConsumptionStats;

        private class ProductMetrics
        {
            public int BuiltCount;
            public int PausedCount;
            public Electricity Consumed = Electricity.Zero;
            public Electricity MaxPower = Electricity.Zero;
            public List<int> PausedPriorities = new List<int>();
        }

        private readonly Dictionary<string, ProductMetrics> m_cachedProductMetrics = new Dictionary<string, ProductMetrics>();

        public static bool IsWindowOpen => s_instance != null && s_instance.IsOpen;

        public static void ToggleWindow(ProtosDb protosDb, PowerGovernorManager manager, UiRoot uiRoot, InspectorsManager inspectorsManager = null, CameraController cameraController = null)
        {
            if (s_instance == null)
            {
                s_instance = new PowerGovernorWindow(protosDb, manager, inspectorsManager, cameraController);
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

        public static void OpenWindow(ProtosDb protosDb, PowerGovernorManager manager, UiRoot uiRoot, InspectorsManager inspectorsManager = null, CameraController cameraController = null)
        {
            if (s_instance == null)
            {
                s_instance = new PowerGovernorWindow(protosDb, manager, inspectorsManager, cameraController);
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
                Electricity cap = (concrete != null && concrete.MaxGenerationCapacity.IsPositive) ? concrete.MaxGenerationCapacity : em.GenerationCapacityThisTick;
                Electricity prod = (concrete != null) ? concrete.GeneratedThisTick : em.ConsumedThisTick;
                s_instance.UpdateStatusText(em.DemandedThisTick, em.ConsumedThisTick, prod, cap);

                // Update row statistics
                if (concrete != null)
                {
                    var statsMap = new Dictionary<IEntityProto, ElectricityManager.ConsumptionPerProto>();
                    foreach (var stat in concrete.GetConsumptionStatsPerProto())
                    {
                        if (stat.ConsumerProto != null)
                        {
                            statsMap[stat.ConsumerProto] = stat;
                        }
                    }
                    s_instance.m_cachedConsumptionStats = statsMap;
                }

                s_instance.updateCachedProductMetrics();

                foreach (var action in s_instance.m_rowRefreshActions)
                {
                    action();
                }

                s_instance.refreshPausedBuildingsIfChanged();
            }
        }

        private void updateCachedProductMetrics()
        {
            m_cachedProductMetrics.Clear();
            if (m_manager?.EntitiesManager != null)
            {
                var allMachines = m_manager.EntitiesManager.GetAllEntitiesOfType<Machine>();
                foreach (var machine in allMachines)
                {
                    if (!machine.Prototype.ElectricityConsumed.IsPositive && !machine.ElectricityConsumer.HasValue) continue;

                    var outputs = PowerGovernorManager.GetActiveOutputProducts(machine);
                    if (outputs.Count == 0) continue;

                    bool isThrottled = m_manager.IsMachineThrottled(machine);
                    int prio = PowerGovernorManager.GetEntityPriority(machine);
                    Electricity pwr = machine.PowerRequired.IsPositive ? machine.PowerRequired : machine.Prototype.ElectricityConsumed;

                    foreach (var prod in outputs)
                    {
                        string pid = prod.Id.Value;
                        if (!m_cachedProductMetrics.TryGetValue(pid, out var metric))
                        {
                            metric = new ProductMetrics();
                            m_cachedProductMetrics[pid] = metric;
                        }

                        metric.BuiltCount++;
                        metric.MaxPower += pwr;
                        if (isThrottled)
                        {
                            metric.PausedCount++;
                            metric.PausedPriorities.Add(prio);
                        }
                        else
                        {
                            metric.Consumed += pwr;
                        }
                    }
                }
            }
        }

        public PowerGovernorWindow(ProtosDb protosDb, PowerGovernorManager manager, InspectorsManager inspectorsManager = null, CameraController cameraController = null)
            : base(new LocStrFormatted("⚡ Electric Power Governor"), addFullscreenButton: false)
        {
            m_protosDb = protosDb;
            m_manager = manager;
            m_inspectorsManager = inspectorsManager;
            m_cameraController = cameraController;

            WindowSize(930.px(), 690.px());
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
            m_lblGridStatus = new Label(new LocStrFormatted("Grid Status: Calculating...")).MinWidth(new Px(420));
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

            // Prime consumption stats immediately so rows have real data on first render
            if (m_manager?.ElectricityManager is ElectricityManager concreteEm)
            {
                var statsMap = new Dictionary<IEntityProto, ElectricityManager.ConsumptionPerProto>();
                foreach (var stat in concreteEm.GetConsumptionStatsPerProto())
                {
                    if (stat.ConsumerProto != null)
                    {
                        statsMap[stat.ConsumerProto] = stat;
                    }
                }
                m_cachedConsumptionStats = statsMap;
            }

            updateCachedProductMetrics();

            // 2. Tabbed Section: Building Types, Recipe Outputs, Paused Buildings, and User Guide
            var tabContainer = new TabContainer();
            tabContainer.Height(new Px(450));

            var rulesContent = buildRulesTab();
            var recipeOutputsContent = buildRecipeOutputsTab();
            var pausedContent = buildPausedBuildingsTab();
            var guideContent = buildGuideTab();

            tabContainer.AddTab(new LocStrFormatted("⚡ Building Types"), rulesContent, scroll: false);
            tabContainer.AddTab(new LocStrFormatted("📦 Recipe Outputs"), recipeOutputsContent, scroll: false);
            tabContainer.AddTab(new LocStrFormatted("⏸ Paused Buildings"), pausedContent, scroll: false);
            tabContainer.AddTab(new LocStrFormatted("📖 Guide & Help"), guideContent, scroll: false);

            Body.Add(tabContainer);

            // Populate initial status and row statistics immediately
            foreach (var action in m_rowRefreshActions)
            {
                action();
            }

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

            // Column Header Row
            var headerRow = new Row().AlignItemsCenter().Gap(8.px()).PaddingTopBottom(4.px());
            headerRow.Add(new Label(new LocStrFormatted("Shed")).FontBold().Color(ColorRgba.LightGray).Width(32.px()).NoShrink());
            headerRow.Add(new Label(LocStrFormatted.Empty).Width(22.px()).NoShrink()); // Icon spacer
            headerRow.Add(new Label(new LocStrFormatted("Built")).FontBold().Color(ColorRgba.LightGray).Width(38.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Building Type")).FontBold().Color(ColorRgba.LightGray).Width(190.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Power Usage")).FontBold().Color(ColorRgba.LightGray).Width(150.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Governor Status (Priority-Based)")).FontBold().Color(ColorRgba.LightGray).MinWidth(new Px(240)));
            scroll.Add(headerRow);

            // Group buildings by display name to prevent duplicates (e.g. lift height variants)
            var protoGroups = m_protosDb.Filter<LayoutEntityProto>(p => p is IProtoWithPowerConsumption pc && pc.ElectricityConsumed.IsPositive)
                .GroupBy(p => p.Strings.Name.ToString())
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in protoGroups)
            {
                var proto = group.First();
                string protoName = group.Key;
                var protoList = group.ToList();

                var rule = m_manager.GetGlobalRule(proto.Id.Value);

                // Synchronize all variants in the group so they share consistent state
                bool isAnyEnabled = protoList.Any(p => m_manager.GetGlobalRule(p.Id.Value).IsEnabled);
                foreach (var p in protoList)
                {
                    var r = m_manager.GetGlobalRule(p.Id.Value);
                    r.IsEnabled = isAnyEnabled;
                }

                var row = new Row().AlignItemsCenter().Gap(8.px()).PaddingTopBottom(3.px());

                // Standalone toggle: select which building types the governor can pause
                var chkEnabled = new Toggle(standalone: true).Value(isAnyEnabled);
                chkEnabled[1].Visible(false);
                chkEnabled.Width(32.px()).NoShrink();

                // Add official building icon
                UiComponent iconComp;
                if (proto is IProtoWithIcon pwi)
                {
                    iconComp = new Icon(pwi).Size(22.px(), 22.px()).NoShrink();
                }
                else
                {
                    iconComp = new Icon(Assets.Unity.UserInterface.Toolbar.Machines_svg).Size(22.px(), 22.px()).NoShrink();
                }

                var dispCount = new Display().MinDigits(2);
                dispCount.Width(38.px()).NoShrink();

                var lblName = new Label(new LocStrFormatted(protoName))
                    .Width(190.px()).NoShrink().NoTextWrap();

                var lblPower = new Label(new LocStrFormatted("-- / --"))
                    .Width(150.px()).NoShrink().NoTextWrap();

                var lblStatus = new Label(LocStrFormatted.Empty)
                    .MinWidth(new Px(240)).NoTextWrap();

                chkEnabled.OnValueChanged(val =>
                {
                    foreach (var p in protoList)
                    {
                        var r = m_manager.GetGlobalRule(p.Id.Value);
                        r.IsEnabled = val;
                        if (!val)
                        {
                            m_manager.ResumeAllOfProto(p.Id.Value);
                        }
                    }
                    m_manager.SaveSettings();
                    RefreshStatusIfOpen();
                });

                // Row refresh hook to update building count, power usage, and throttled status
                m_rowRefreshActions.Add(() =>
                {
                    int totalBuilt = 0;
                    Electricity totalConsumed = Electricity.Zero;
                    Electricity totalMax = Electricity.Zero;

                    if (m_cachedConsumptionStats != null)
                    {
                        foreach (var p in protoList)
                        {
                            if (m_cachedConsumptionStats.TryGetValue(p, out var stat))
                            {
                                totalBuilt += stat.EntitiesTotal;
                                totalConsumed += stat.LastTick.Consumed;
                                totalMax += stat.LastTick.MaxPossibleConsumption;
                            }
                        }
                    }

                    dispCount.SetValue(totalBuilt.ToStringCached().AsLoc());

                    if (totalBuilt > 0)
                    {
                        if (totalMax.IsZero && proto is IProtoWithPowerConsumption pwc && pwc.ElectricityConsumed.IsPositive)
                        {
                            totalMax = pwc.ElectricityConsumed * totalBuilt;
                        }
                        lblPower.Value(new LocStrFormatted($"{totalConsumed.Format()} / {totalMax.Format()}"));
                        lblPower.Color(totalConsumed.IsPositive ? ColorRgba.White : new ColorRgba(180, 190, 205, 255));
                    }
                    else
                    {
                        lblPower.Value(new LocStrFormatted("-- / --"));
                        lblPower.Color(new ColorRgba(120, 130, 150, 180));
                    }

                    bool isEnabledNow = protoList.Any(p => m_manager.GetGlobalRule(p.Id.Value).IsEnabled);
                    bool isTesting = protoList.Any(p => m_manager.IsProtoBeingTested(p.Id.Value));

                    if (!isEnabledNow)
                    {
                        lblStatus.Value(new LocStrFormatted("Exempt (Never Paused)"));
                        lblStatus.Color(new ColorRgba(120, 135, 155, 180));
                    }
                    else if (isTesting)
                    {
                        lblStatus.Value(new LocStrFormatted("🔄 Testing unpause..."));
                        lblStatus.Color(ColorRgba.Cyan);
                    }
                    else
                    {
                        int pausedCount = protoList.Sum(p => m_manager.GetThrottledCountForProto(p.Id.Value));
                        if (pausedCount > 0)
                        {
                            var pausedList = m_manager.GetThrottledBuildings().Where(b => protoList.Any(p => p.Id.Value == b.ProtoId)).ToList();
                            if (pausedList.Count > 0)
                            {
                                int minP = pausedList.Min(b => b.GeneralPriority);
                                int maxP = pausedList.Max(b => b.GeneralPriority);
                                string prioSpan = (minP == maxP) ? $"P{minP}" : $"P{minP}..P{maxP}";
                                lblStatus.Value(new LocStrFormatted($"⏸ {pausedCount} off ({prioSpan})"));
                            }
                            else
                            {
                                lblStatus.Value(new LocStrFormatted($"⏸ {pausedCount} off"));
                            }
                            lblStatus.Color(ColorRgba.Orange);
                        }
                        else
                        {
                            lblStatus.Value(new LocStrFormatted("Active (Protected by P1..P10)"));
                            lblStatus.Color(new ColorRgba(100, 230, 120, 255));
                        }
                    }
                });

                row.Add(chkEnabled, iconComp, dispCount, lblName, lblPower, lblStatus);
                scroll.Add(row);
            }

            return scroll;
        }

        private UiComponent buildRecipeOutputsTab()
        {
            var scroll = new ScrollColumn().MaxHeight(new Px(410)).AlignItemsStretch();

            // Column Header Row
            var headerRow = new Row().AlignItemsCenter().Gap(8.px()).PaddingTopBottom(4.px());
            headerRow.Add(new Label(new LocStrFormatted("Shed")).FontBold().Color(ColorRgba.LightGray).Width(32.px()).NoShrink());
            headerRow.Add(new Label(LocStrFormatted.Empty).Width(22.px()).NoShrink()); // Icon spacer
            headerRow.Add(new Label(new LocStrFormatted("Built")).FontBold().Color(ColorRgba.LightGray).Width(38.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Recipe Output")).FontBold().Color(ColorRgba.LightGray).Width(190.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Power Usage")).FontBold().Color(ColorRgba.LightGray).Width(150.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Governor Status (Priority-Based)")).FontBold().Color(ColorRgba.LightGray).MinWidth(new Px(240)));
            scroll.Add(headerRow);

            // Find all output products produced by electricity-consuming machines
            var machineProtos = m_protosDb.Filter<MachineProto>(p => p.ElectricityConsumed.IsPositive);
            var productDict = new Dictionary<string, ProductProto>();

            foreach (var mp in machineProtos)
            {
                foreach (var recipe in mp.Recipes)
                {
                    foreach (var output in recipe.AllUserVisibleOutputs)
                    {
                        if (output.Product != null && !output.Product.Id.Value.StartsWith("__PHANTOM__"))
                        {
                            productDict[output.Product.Id.Value] = output.Product;
                        }
                    }
                }
            }

            var orderedProducts = productDict.Values
                .OrderBy(p => p.Strings.Name.ToString())
                .ToList();

            foreach (var product in orderedProducts)
            {
                string prodId = product.Id.Value;
                string prodName = product.Strings.Name.ToString();
                var rule = m_manager.GetRecipeOutputRule(prodId);

                var row = new Row().AlignItemsCenter().Gap(8.px()).PaddingTopBottom(3.px());

                var chkEnabled = new Toggle(standalone: true).Value(rule.IsEnabled);
                chkEnabled[1].Visible(false);
                chkEnabled.Width(32.px()).NoShrink();

                var iconComp = new Icon(product).Size(22.px(), 22.px()).NoShrink();

                var dispCount = new Display().MinDigits(2);
                dispCount.Width(38.px()).NoShrink();

                var lblName = new Label(new LocStrFormatted(prodName))
                    .Width(190.px()).NoShrink().NoTextWrap();

                var lblPower = new Label(new LocStrFormatted("-- / --"))
                    .Width(150.px()).NoShrink().NoTextWrap();

                var lblStatus = new Label(LocStrFormatted.Empty)
                    .MinWidth(new Px(240)).NoTextWrap();

                chkEnabled.OnValueChanged(val =>
                {
                    rule.IsEnabled = val;
                    if (!val)
                    {
                        m_manager.ResumeAllOfRecipeOutput(prodId);
                    }
                    m_manager.SaveSettings();
                    RefreshStatusIfOpen();
                });

                m_rowRefreshActions.Add(() =>
                {
                    m_cachedProductMetrics.TryGetValue(prodId, out var metric);
                    int totalBuilt = metric?.BuiltCount ?? 0;
                    int pausedCount = metric?.PausedCount ?? 0;
                    Electricity totalConsumed = metric?.Consumed ?? Electricity.Zero;
                    Electricity totalMax = metric?.MaxPower ?? Electricity.Zero;

                    dispCount.SetValue(totalBuilt.ToStringCached().AsLoc());

                    if (totalBuilt > 0)
                    {
                        lblPower.Value(new LocStrFormatted($"{totalConsumed.Format()} / {totalMax.Format()}"));
                        lblPower.Color(totalConsumed.IsPositive ? ColorRgba.White : new ColorRgba(180, 190, 205, 255));
                    }
                    else
                    {
                        lblPower.Value(new LocStrFormatted("-- / --"));
                        lblPower.Color(new ColorRgba(120, 130, 150, 180));
                    }

                    if (!rule.IsEnabled)
                    {
                        lblStatus.Value(new LocStrFormatted("Exempt (Always On)"));
                        lblStatus.Color(new ColorRgba(130, 145, 165, 200));
                    }
                    else
                    {
                        if (pausedCount > 0 && metric != null && metric.PausedPriorities.Count > 0)
                        {
                            int minP = metric.PausedPriorities.Min();
                            int maxP = metric.PausedPriorities.Max();
                            string prioSpan = (minP == maxP) ? $"P{minP}" : $"P{minP}..P{maxP}";
                            lblStatus.Value(new LocStrFormatted($"⏸ {pausedCount}/{totalBuilt} off ({prioSpan})"));
                            lblStatus.Color(ColorRgba.Orange);
                        }
                        else
                        {
                            lblStatus.Value(new LocStrFormatted("Active (Protected by P1..P10)"));
                            lblStatus.Color(new ColorRgba(100, 230, 120, 255));
                        }
                    }
                });

                row.Add(chkEnabled, iconComp, dispCount, lblName, lblPower, lblStatus);
                scroll.Add(row);
            }

            return scroll;
        }

        private UiComponent buildPausedBuildingsTab()
        {
            var scroll = new ScrollColumn().MaxHeight(new Px(410)).AlignItemsStretch();
            var col = new Column().Padding(8.px()).Gap(8.px());

            // Summary Header Card
            var headerCard = createCard();
            var topRow = new Row().AlignItemsCenter().Gap(10.px());
            topRow.Add(new Icon(Assets.Unity.UserInterface.Toolbar.PauseTool_svg).Size(20.px(), 20.px()));

            m_lblPausedSummary = new Label(new LocStrFormatted("Currently Paused: 0 Buildings")).FontBold().MinWidth(new Px(380));
            m_btnResumeAllInTab = new ButtonText(new LocStrFormatted("Resume All"), null).MinWidth(new Px(140));
            m_btnResumeAllInTab.OnClick(() =>
            {
                m_manager.ResumeAllThrottled();
                RefreshStatusIfOpen();
            });

            topRow.Add(m_lblPausedSummary, m_btnResumeAllInTab);

            var lblDesc = new Label(new LocStrFormatted(
                "Buildings paused during power shortages. Shedding starts at Priority 10 first down to Priority 1 last. Restoration occurs in reverse (P1 first up to P10)."
            ));

            headerCard.Add(topRow, lblDesc);

            // Container for dynamic paused rows
            m_pausedRowsContainer = new Column().Gap(4.px());

            col.Add(headerCard, m_pausedRowsContainer);
            scroll.Add(col);

            renderPausedBuildingsRows();

            return scroll;
        }

        private void renderPausedBuildingsRows()
        {
            if (m_pausedRowsContainer == null) return;
            m_pausedRowsContainer.Clear();

            var pausedList = m_manager.GetThrottledBuildings();
            int count = pausedList.Count;

            if (m_lblPausedSummary != null)
            {
                m_lblPausedSummary.Value(new LocStrFormatted(
                    count == 0 ? "Currently Paused: 0 Buildings (Supply Meets Demand)" : $"Currently Paused: {count} Building{(count > 1 ? "s" : "")}"
                ));
            }

            if (m_btnResumeAllInTab != null)
            {
                m_btnResumeAllInTab.Visible(count > 0);
            }

            if (count == 0)
            {
                // Empty State Card
                var emptyCard = createCard();
                var emptyRow = new Row().AlignItemsCenter().Gap(10.px()).Padding(10.px());
                emptyRow.Add(new Icon(Assets.Unity.UserInterface.General.Checkmark_v2_svg).Size(28.px(), 28.px()));

                var emptyTextCol = new Column().Gap(4.px());
                emptyTextCol.Add(new Label(new LocStrFormatted("All Selected Buildings Are Running")).FontBold().Color(ColorRgba.Green));
                emptyTextCol.Add(new Label(new LocStrFormatted(
                    "Power supply satisfies 100% of demand. If demand outpaces supply, the governor will automatically pause non-essential machines based on their in-game priority."
                )));

                emptyRow.Add(emptyTextCol);
                emptyCard.Add(emptyRow);
                m_pausedRowsContainer.Add(emptyCard);
                return;
            }

            var groups = pausedList.GroupBy(b => b.Name).OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                var first = group.First();
                int groupCount = group.Count();

                var groupCard = new Column()
                    .Padding(8.px(), 10.px())
                    .Gap(6.px())
                    .Background(new ColorRgba(20, 26, 36, 215))
                    .Border(1.px(), color: new ColorRgba(50, 65, 85, 150), radius: 6);

                // Group Header Row
                var headerRow = new Row().AlignItemsCenter().Gap(8.px());

                UiComponent iconComp = (first.Proto is IProtoWithIcon pwi)
                    ? new Icon(pwi).Size(24.px(), 24.px())
                    : new Icon(Assets.Unity.UserInterface.Toolbar.Machines_svg).Size(24.px(), 24.px());

                int minGroupPrio = group.Min(g => g.GeneralPriority);
                int maxGroupPrio = group.Max(g => g.GeneralPriority);
                string groupPrioSpan = (minGroupPrio == maxGroupPrio) ? $"[P{minGroupPrio}]" : $"[P{minGroupPrio}..P{maxGroupPrio}]";

                var lblTitle = new Label(new LocStrFormatted($"{first.Name} ({groupCount} paused  {groupPrioSpan})"))
                    .FontBold()
                    .MinWidth(new Px(250));

                var btnResumeType = new ButtonText(new LocStrFormatted($"Resume All ({groupCount})"), null)
                    .MinWidth(new Px(130));

                btnResumeType.OnClick(() =>
                {
                    foreach (var item in group)
                    {
                        m_manager.ResumeSingleBuilding(item.Entity.Id.Value);
                    }
                    RefreshStatusIfOpen();
                });

                headerRow.Add(iconComp, lblTitle, btnResumeType);
                groupCard.Add(headerRow);

                // Individual instances list under this building type (sorted P1 to P10 so next-to-restore appears first)
                var instancesCol = new Column().PaddingLeft(16.px()).Gap(4.px());
                foreach (var item in group)
                {
                    var row = new Row().AlignItemsCenter().Gap(8.px()).PaddingTopBottom(2.px());

                    var lblId = new Label(new LocStrFormatted($"• #{item.Entity.Id.Value}"))
                        .MinWidth(new Px(75));

                    var lblPrio = new Label(new LocStrFormatted($"[ P{item.GeneralPriority} ]"))
                        .FontBold()
                        .Color(GetPriorityColor(item.GeneralPriority))
                        .MinWidth(new Px(55));

                    UiComponent badgeComp;
                    if (item.IsLocalOverride)
                    {
                        badgeComp = new Label(new LocStrFormatted("[ Local Override ]"))
                            .Color(ColorRgba.Cyan)
                            .MinWidth(new Px(130));
                    }
                    else
                    {
                        badgeComp = new Label(new LocStrFormatted("[ Auto-Shed ]"))
                            .Color(ColorRgba.LightBlue)
                            .MinWidth(new Px(130));
                    }

                    Label recipeBadge = null;
                    if (!string.IsNullOrEmpty(item.ActiveRecipeOutputsText))
                    {
                        recipeBadge = new Label(new LocStrFormatted($"[ {item.ActiveRecipeOutputsText} ]"))
                            .Color(new ColorRgba(200, 220, 255, 255))
                            .MinWidth(new Px(130));
                    }

                    var btnFocus = new ButtonText(new LocStrFormatted("View"), null).MinWidth(new Px(60));
                    var entityRef = item.Entity;
                    btnFocus.OnClick(() =>
                    {
                        if (entityRef != null)
                        {
                            m_cameraController?.PanTo(entityRef.Position2f);
                            m_inspectorsManager?.TryActivateFor(entityRef);
                        }
                    });

                    int entityId = item.Entity.Id.Value;
                    var btnResumeSingle = new ButtonText(new LocStrFormatted("Resume"), null).MinWidth(new Px(70));
                    btnResumeSingle.OnClick(() =>
                    {
                        m_manager.ResumeSingleBuilding(entityId);
                        RefreshStatusIfOpen();
                    });

                    if (recipeBadge != null)
                    {
                        row.Add(lblId, lblPrio, badgeComp, recipeBadge, btnFocus, btnResumeSingle);
                    }
                    else
                    {
                        row.Add(lblId, lblPrio, badgeComp, btnFocus, btnResumeSingle);
                    }
                    instancesCol.Add(row);
                }

                groupCard.Add(instancesCol);
                m_pausedRowsContainer.Add(groupCard);
            }
        }

        private void refreshPausedBuildingsIfChanged()
        {
            if (m_pausedRowsContainer == null) return;

            // Hash currently throttled IDs to avoid rebuilding unless changed
            int hash = 17;
            unchecked
            {
                foreach (int id in m_manager.ThrottledEntityIds)
                {
                    hash = hash * 31 + id;
                }
            }

            if (hash != m_lastRenderedPausedHash)
            {
                m_lastRenderedPausedHash = hash;
                renderPausedBuildingsRows();
            }
        }

        private UiComponent buildGuideTab()
        {
            var scroll = new ScrollColumn().MaxHeight(new Px(410)).AlignItemsStretch();
            var col = new Column().Padding(8.px()).Gap(10.px());

            // Header Banner
            var titleRow = new Row().AlignItemsCenter().Gap(8.px());
            titleRow.Add(new Icon(Assets.Unity.UserInterface.General.ElectricityColored_svg).Size(24.px(), 24.px()));
            titleRow.Add(new Label(new LocStrFormatted("ELECTRIC POWER GOVERNOR - PRIORITY USER GUIDE")).FontBold());

            // Section 1: How Demand-Driven Load Shedding Works
            var card1 = createCard();
            var sec1Header = new Row().AlignItemsCenter().Gap(6.px());
            sec1Header.Add(new Icon(Assets.Unity.UserInterface.Toolbar.Stats_svg).Size(18.px(), 18.px()));
            sec1Header.Add(new Label(new LocStrFormatted("1. How Demand-Driven Load Shedding Works")).FontBold());

            var p1 = new Label(new LocStrFormatted(
                "No complicated percentage or megawatt math required:\n\n" +
                "• 1. Flexible Selection (Building Types & Recipe Outputs):\n" +
                "   - ⚡ Building Types: Check entire machine types (e.g. Arc Furnaces, Computing Data Centers, Research Labs) to shed.\n" +
                "   - 📦 Recipe Outputs: Check specific product outputs (e.g. Steel, Molten Iron, Silicon, Fertilizer). All machines currently configured to produce that product are selected, regardless of which recipe variant is active (e.g. Steel from Iron Ore, Scrap Iron, or Crushed Iron).\n\n" +
                "• 2. Demand Outpaces Supply: The governor ONLY engages when total island electrical demand exceeds generator supply or when a blackout deficit is detected.\n\n" +
                "• 3. Priority Shedding (P10 down to P1): When demand outpaces supply, the governor pauses Priority 10 items first, stepping down until demand and supply are balanced. Critical machines (P1) remain running!\n\n" +
                "• 4. Stepped Restoration (P1 up to P10): When the power grid stabilizes, restoration begins at Priority 1 FIRST, then slowly ramps up to Priority 10."
            ));
            card1.Add(sec1Header, p1);

            // Section 2: Priority Strategy & Tiers
            var card2 = createCard();
            var sec2Header = new Row().AlignItemsCenter().Gap(6.px());
            sec2Header.Add(new Icon(Assets.Unity.UserInterface.Toolbar.Power_svg).Size(18.px(), 18.px()));
            sec2Header.Add(new Label(new LocStrFormatted("2. Recommended Strategy")).FontBold());

            var pTiersIntro = new Label(new LocStrFormatted(
                "Keep vital life-support infrastructure unchecked so it can never be paused:"
            ));

            var tierAlwaysOn = createTierBox(
                iconPath: Assets.Unity.UserInterface.General.Checkmark_v2_svg,
                title: "ALWAYS ON (CRITICAL - LEAVE UNCHECKED)",
                buildings: "Storage, Balancers, Electric Boilers, Maintenance Depots, Water Pumps, Fuel Refineries, Sorting Plants, Lifts, Sorters",
                desc: "• Why: If maintenance depots, water/fuel pumps, or logistics lose power, generators starve of coal/water and trigger an unrecoverable blackout death spiral.\n• Action: Leave the checkbox UNCHECKED for these so they always receive full power.",
                titleColor: ColorRgba.Green
            );

            var priorityBox = createTierBox(
                iconPath: Assets.Unity.UserInterface.Toolbar.Stats_svg,
                title: "BUILT-IN GENERAL PRIORITY INTEGRATION (P1 - P10)",
                buildings: "All checked machines on your island",
                desc: "• In-Game Priority Integration: The governor directly reads the in-game General Priority (P1 to P10) set on each individual machine.\n• Shedding Order (P10 -> P1): If you have 10 Arc Furnaces and power is insufficient, Priority 10 items pause FIRST, stepping down to Priority 1 LAST until supply and demand match.\n• Stepped Recovery (P1 -> P10): When the power grid stabilizes, restoration begins at Priority 1 FIRST, then slowly ramps up to Priority 10.\n• Quick Setup: Click any machine and set its priority slider, or press 'P' to open the game's built-in priority tool.",
                titleColor: ColorRgba.LightBlue
            );

            card2.Add(sec2Header, pTiersIntro, tierAlwaysOn, priorityBox);

            // Section 3: How Stepped Restoration Works
            var card3 = createCard();
            var sec3Header = new Row().AlignItemsCenter().Gap(6.px());
            sec3Header.Add(new Icon(Assets.Unity.UserInterface.General.InfoBubble_svg).Size(18.px(), 18.px()));
            sec3Header.Add(new Label(new LocStrFormatted("3. Stepped Restoration (Blackout Loop Prevention)")).FontBold());

            var p3 = new Label(new LocStrFormatted(
                "In Captain of Industry, generators only produce what buildings demand. When buildings pause, demand drops and generators throttle down.\n\n" +
                "• Avoid Sudden Crashes: If all machines unpaused at once, grid demand would spike instantly and overwhelm recovering boilers, causing machines to rapidly flicker ON and OFF in an endless blackout loop.\n" +
                "• 10-Second Stability Check: When the grid satisfies 100% of demand for 10 continuous seconds, the governor safely unpauses ONE single building (starting at Priority 1) to test real generator headroom.\n" +
                "• Progressive Ramp-Up: If your generators absorb the load smoothly, that building remains active, and 10 seconds later the next machine (P1, P2... up to P10) is tested.\n" +
                "• Overload Protection & Backoff: If reviving a machine causes demand to outpace supply, it is immediately re-paused within 1 second and the mod enters a 20-second cooldown so fuel buffers can rebuild.\n" +
                "• Manual Override: Click [Resume All] at the top anytime if you know your fuel and steam supplies have fully recovered."
            ));
            card3.Add(sec3Header, p3);

            col.Add(titleRow, card1, card2, card3);
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

            var lblBuildings = new Label(new LocStrFormatted(buildings)).FontBold().Color(ColorRgba.White);
            var lblDesc = new Label(new LocStrFormatted(desc));

            box.Add(headerRow, lblBuildings, lblDesc);
            return box;
        }

        private void UpdateStatusText(Electricity demand, Electricity consumed, Electricity production, Electricity capacity)
        {
            int capKw = capacity.Value;
            int prodKw = production.Value;
            int demKw = demand.Value;
            int conKw = consumed.Value;

            float capMw = (float)capKw / 1000f;
            float prodMw = (float)prodKw / 1000f;
            float demMw = (float)demKw / 1000f;
            float conMw = (float)conKw / 1000f;

            string capStr = (capKw >= 1000) ? $"{capMw:F2} MW" : capacity.Format().ToString();
            string prodStr = (prodKw >= 1000) ? $"{prodMw:F2} MW" : production.Format().ToString();
            string demStr = (demKw >= 1000) ? $"{demMw:F2} MW" : demand.Format().ToString();
            string conStr = (conKw >= 1000) ? $"{conMw:F2} MW" : consumed.Format().ToString();

            m_lblCapacity.Value(new LocStrFormatted($"Grid Capacity: {capStr}"));
            m_lblProduction.Value(new LocStrFormatted($"Current Production: {prodStr}"));
            m_lblDemand.Value(new LocStrFormatted($"Demand: {demStr}"));
            m_lblConsumed.Value(new LocStrFormatted($"Consumed: {conStr}"));

            bool demandOutpacesSupply = demKw > capKw || demKw > conKw;
            if (demandOutpacesSupply)
            {
                int deficitKw = demKw - Math.Min(capKw, conKw);
                float defMw = (float)deficitKw / 1000f;
                m_lblGridStatus.Value(new LocStrFormatted($"Grid Status: ⚠️ Deficit! Demand exceeds supply (+{defMw:F2} MW)"));
                m_lblGridStatus.Color(new ColorRgba(255, 110, 100, 255));
            }
            else
            {
                int surplusKw = Math.Max(0, capKw - demKw);
                float surpMw = (float)surplusKw / 1000f;
                m_lblGridStatus.Value(new LocStrFormatted($"Grid Status: Normal (Surplus: +{surpMw:F2} MW)"));
                m_lblGridStatus.Color(new ColorRgba(100, 230, 120, 255));
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

        private static ColorRgba GetPriorityColor(int priority)
        {
            if (priority <= 3) return new ColorRgba(100, 230, 120, 255); // Green (High priority / restored first)
            if (priority <= 7) return new ColorRgba(245, 210, 80, 255);  // Yellow/Amber (Medium priority)
            return new ColorRgba(255, 120, 100, 255);                    // Red/Coral (Low priority / paused first)
        }
    }
}
