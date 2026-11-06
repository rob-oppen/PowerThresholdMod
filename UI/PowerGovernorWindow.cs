using Label = Mafi.Unity.UiToolkit.Library.Label;
using Slider = Mafi.Unity.UiToolkit.Library.Slider;
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

                s_instance.refreshPausedTab();
            }
        }

        public PowerGovernorWindow(ProtosDb protosDb, PowerGovernorManager manager, InspectorsManager inspectorsManager = null, CameraController cameraController = null)
            : base(new LocStrFormatted("⚡ Electric Power Governor"), addFullscreenButton: false)
        {
            m_protosDb = protosDb;
            m_manager = manager;
            m_inspectorsManager = inspectorsManager;
            m_cameraController = cameraController;

            // Comfortable dimensions for electrical building list, tabs, icons, and guide
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

            // 2. Tabbed Section: Global Rules, Paused Buildings, and User Guide
            var tabContainer = new TabContainer();
            tabContainer.Height(new Px(450));

            var rulesContent = buildRulesTab();
            var pausedContent = buildPausedBuildingsTab();
            var guideContent = buildGuideTab();

            tabContainer.AddTab(new LocStrFormatted("⚡ Global Rules"), rulesContent, scroll: false);
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
            headerRow.Add(new Label(LocStrFormatted.Empty).Width(22.px()).NoShrink()); // Toggle spacer
            headerRow.Add(new Label(LocStrFormatted.Empty).Width(22.px()).NoShrink()); // Icon spacer
            headerRow.Add(new Label(new LocStrFormatted("Built")).FontBold().Color(ColorRgba.LightGray).Width(38.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Building Type")).FontBold().Color(ColorRgba.LightGray).Width(170.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Power Usage")).FontBold().Color(ColorRgba.LightGray).Width(140.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Mode")).FontBold().Color(ColorRgba.LightGray).Width(55.px()).NoShrink());
            headerRow.Add(new Label(new LocStrFormatted("Threshold")).FontBold().Color(ColorRgba.LightGray).Width(125.px()).NoShrink());
            headerRow.Add(new Label(LocStrFormatted.Empty).Width(105.px()).NoShrink()); // Value spacer
            headerRow.Add(new Label(new LocStrFormatted("Status")).FontBold().Color(ColorRgba.LightGray).Width(115.px()).NoShrink());
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
                    r.Mode = rule.Mode;
                    r.PercentThreshold = rule.PercentThreshold;
                    r.MwThreshold = rule.MwThreshold;
                }

                var row = new Row().AlignItemsCenter().Gap(8.px()).PaddingTopBottom(2.px());

                // Standalone toggle with initial value properly set and empty label hidden
                var chkEnabled = new Toggle(standalone: true).Value(isAnyEnabled);
                chkEnabled[1].Visible(false);
                chkEnabled.Width(22.px()).NoShrink();

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
                    .Width(170.px()).NoShrink().NoTextWrap();

                var lblPower = new Label(new LocStrFormatted("-- / --"))
                    .Width(140.px()).NoShrink().NoTextWrap();

                var btnMode = new ButtonText(new LocStrFormatted(rule.Mode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"), null)
                    .Width(55.px()).NoShrink();

                float maxSliderRange = (rule.Mode == ThresholdMode.Percentage)
                    ? 100f
                    : Math.Max(100f, (float)Math.Ceiling(m_manager.LastCapacityMw / 50f) * 50f);

                var slider = new Slider();
                // CRITICAL: Set ValueFormatter BEFORE Range() and Value() so internal label is silenced from the start
                slider.ValueFormatter(Option.Some<Func<Percent, LocStrFormatted>>(p => LocStrFormatted.Empty));
                slider[2].Visible(false); // Hide internal m_valueLabel completely so it takes 0 space
                slider[2].Width(0.px());
                slider.Range(0f, maxSliderRange);
                slider.Value(rule.ThresholdValue, notify: false);
                slider.Width(125.px()).NoShrink();

                var lblValue = new Label(new LocStrFormatted(FormatRuleValue(rule)))
                    .Width(105.px()).NoShrink().NoTextWrap();
                var lblStatus = new Label(LocStrFormatted.Empty)
                    .Width(115.px()).NoShrink().NoTextWrap();

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

                // Updates threshold cleanly across all variants in the group
                slider.OnValueChanged((prev, current) =>
                {
                    float rounded = (float)Math.Round(current, 0);
                    foreach (var p in protoList)
                    {
                        var r = m_manager.GetGlobalRule(p.Id.Value);
                        r.ThresholdValue = rounded;
                    }
                    lblValue.Value(new LocStrFormatted(FormatRuleValue(rule)));
                    m_manager.SaveSettings();
                });

                btnMode.OnClick(() =>
                {
                    var newMode = (rule.Mode == ThresholdMode.Percentage) ? ThresholdMode.MegaWatts : ThresholdMode.Percentage;
                    foreach (var p in protoList)
                    {
                        var r = m_manager.GetGlobalRule(p.Id.Value);
                        r.Mode = newMode;
                    }

                    float newMax = (newMode == ThresholdMode.Percentage)
                        ? 100f
                        : Math.Max(100f, (float)Math.Ceiling(m_manager.LastCapacityMw / 50f) * 50f);

                    slider.Range(0f, newMax);
                    slider.Value(rule.ThresholdValue, notify: false);

                    btnMode.Value(new LocStrFormatted(newMode == ThresholdMode.Percentage ? "[ % ]" : "[ MW ]"));
                    lblValue.Value(new LocStrFormatted(FormatRuleValue(rule)));
                    m_manager.SaveSettings();
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

                    bool isTesting = protoList.Any(p => m_manager.IsProtoBeingTested(p.Id.Value));
                    if (isTesting)
                    {
                        lblStatus.Value(new LocStrFormatted("🔄 Testing unpause..."));
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
                        }
                        else
                        {
                            lblStatus.Value(LocStrFormatted.Empty);
                        }
                    }
                });

                row.Add(chkEnabled, iconComp, dispCount, lblName, lblPower, btnMode, slider, lblValue, lblStatus);
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
                "Buildings automatically paused by the governor to prevent brownouts. Click 'View' to focus the camera on any building."
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
                    count == 0 ? "Currently Paused: 0 Buildings (Grid Stable)" : $"Currently Paused: {count} Building{(count > 1 ? "s" : "")}"
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
                emptyTextCol.Add(new Label(new LocStrFormatted("All Configured Buildings Are Running")).FontBold().Color(ColorRgba.Green));
                emptyTextCol.Add(new Label(new LocStrFormatted(
                    "No buildings are currently throttled by the Power Governor. When power production drops below your configured thresholds, paused buildings will appear here along with their rule source (Global or Local)."
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
                string groupName = group.Key;
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

                // Individual instances list under this building type
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

                    string ruleValStr = FormatRuleValue(item.EffectiveRule);
                    UiComponent badgeComp;
                    if (item.IsLocalOverride)
                    {
                        badgeComp = new Label(new LocStrFormatted($"[ Local: {ruleValStr} ]"))
                            .Color(ColorRgba.Cyan)
                            .MinWidth(new Px(210));
                    }
                    else
                    {
                        badgeComp = new Label(new LocStrFormatted($"[ Global: {ruleValStr} ]"))
                            .Color(ColorRgba.LightBlue)
                            .MinWidth(new Px(210));
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

                    row.Add(lblId, lblPrio, badgeComp, btnFocus, btnResumeSingle);
                    instancesCol.Add(row);
                }

                groupCard.Add(instancesCol);
                m_pausedRowsContainer.Add(groupCard);
            }
        }

        private void refreshPausedTab()
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
                buildings: "Storage, Balancers, Electric Boilers, Maintenance I & II Depots, Water Pumps, Fuel Refineries, Sorting Plants, Lifts, Sorters",
                desc: "• Why: If maintenance depots, water/fuel pumps, or resource sorting/logistics lose power, machines break down and boilers starve, triggering an unrecoverable blackout death spiral.\n• Action: Leave the checkbox UNCHECKED for these so they always receive full power.",
                titleColor: ColorRgba.Green
            );

            var tier1 = createTierBox(
                iconPath: Assets.Unity.UserInterface.Toolbar.Metallurgy_svg,
                title: "TIER I - CORE REFINING & CRITICAL PROCESSING (e.g. 15% - 25% Low Threshold)",
                buildings: "Alloy Mixers, Copper Electrolysis, Oxygen Furnaces, Oil Refining & Fuel Processing",
                desc: "• Why: These electric machines produce materials and diesel fuel essential for maintenance.\n• Action: Set a low threshold (e.g. 20%) so they only shut down in severe emergencies.\n(Note: Blast Furnaces and Metal Casters do not use electricity and run without grid power).",
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
                buildings: "Arc Furnaces, Data Centres / Mainframes (Computing), Research Labs",
                desc: "• Why: Arc Furnaces draw enormous electricity (1.5MW - 3MW each!), and Computing/Research can easily pause without harming factory survival.\n• Action: Set a high threshold (e.g. 65% - 75%) so they are the FIRST to pause, instantly stabilizing the grid.",
                titleColor: ColorRgba.Orange
            );

            var priorityBox = createTierBox(
                iconPath: Assets.Unity.UserInterface.Toolbar.Stats_svg,
                title: "BUILT-IN GENERAL PRIORITY INTEGRATION (P1 - P10)",
                buildings: "All configured buildings and machines on your island",
                desc: "• Game Priority Integration: The governor directly reads the in-game General Priority (P1 to P10) set on each individual building.\n• Smart Shedding (P10 -> P1): If you have multiple machines (e.g. 10 Arc Furnaces) and power is insufficient, Priority 10 items pause FIRST, stepping all the way down to Priority 1 LAST.\n• Stepped Recovery (P1 -> P10): When the power grid stabilizes, restoration begins at Priority 1 FIRST, then slowly ramps up to Priority 10.\n• Quick Setup: Click any machine and set its priority slider, or press 'P' to open the game's built-in priority tool.",
                titleColor: ColorRgba.LightBlue
            );

            card2.Add(sec2Header, pTiersIntro, tierAlwaysOn, tier1, tier2, tier3, priorityBox);

            // Section 3: How Staged Grid Restoration Works (Blackout Loop Prevention)
            var card3 = createCard();
            var sec3Header = new Row().AlignItemsCenter().Gap(6.px());
            sec3Header.Add(new Icon(Assets.Unity.UserInterface.General.InfoBubble_svg).Size(18.px(), 18.px()));
            sec3Header.Add(new Label(new LocStrFormatted("3. Staged Grid Restoration (Blackout Loop Prevention)")).FontBold());

            var p3 = new Label(new LocStrFormatted(
                "In Captain of Industry, generators only produce what buildings demand. When buildings pause, demand drops and generators throttle down.\n\n" +
                "• Avoid Sudden Crashes: If all machines unpaused at once, grid demand would spike instantly and overwhelm recovering boilers, causing machines to rapidly flicker ON and OFF in a blackout loop.\n" +
                "• 10-Second Stability Check: When the grid satisfies 100% of demand (zero deficit) for 10 continuous seconds, the governor safely unpauses ONE single building to test real generator headroom.\n" +
                "• Progressive Ramp-Up: If your generators absorb the new load smoothly, that building remains active, and 10 seconds later the next building is tested.\n" +
                "• Overload Protection & Backoff: If reviving a machine creates a power deficit (generators still lack steam/coal), it is immediately re-paused within 1 second and the mod enters a 20-second cooldown so fuel buffers can rebuild.\n" +
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

        private static ColorRgba GetPriorityColor(int priority)
        {
            if (priority <= 3) return new ColorRgba(100, 230, 120, 255); // Green (High priority / restored first)
            if (priority <= 7) return new ColorRgba(245, 210, 80, 255);  // Yellow/Amber (Medium priority)
            return new ColorRgba(255, 120, 100, 255);                    // Red/Coral (Low priority / paused first)
        }

        private static string FormatRuleValue(MachinePowerRule rule)
        {
            return (rule.Mode == ThresholdMode.Percentage)
                ? $"Pause if < {rule.ThresholdValue:F0}%"
                : $"Pause if < {rule.ThresholdValue:F0} MW";
        }
    }
}



