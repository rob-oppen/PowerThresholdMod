using System;
using System.Collections;
using System.Reflection;
using Mafi;
using Mafi.Core.Prototypes;
using Mafi.Localization;
using Mafi.Unity;
using Mafi.Unity.Ui.Hud;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using PowerThresholdMod.Engine;
using UnityEngine;

namespace PowerThresholdMod.UI
{
    public class PowerGovernorHostMb : MonoBehaviour, IRootEscapeHandler
    {
        private HudController m_hudController;
        private UiRoot m_uiRoot;
        private IRootEscapeManager m_escapeManager;
        private ProtosDb m_protosDb;
        private PowerGovernorManager m_governorManager;

        private bool m_buttonAdded;
        private bool m_initialized;
        private bool m_escapeRegistered;
        private int m_openFrame;
        private int m_frameCounter;

        public void Initialize(HudController hudController, UiRoot uiRoot, IRootEscapeManager escapeManager, ProtosDb protosDb, PowerGovernorManager governorManager)
        {
            m_hudController = hudController;
            m_uiRoot = uiRoot;
            m_escapeManager = escapeManager;
            m_protosDb = protosDb;
            m_governorManager = governorManager;

            if (!m_initialized)
            {
                m_initialized = true;
                StartCoroutine(AddHudButtonDeferred());
                TryRegisterModSettingsTab();
            }
        }

        private IEnumerator AddHudButtonDeferred()
        {
            yield return new WaitForSeconds(1.5f);
            AddHudButton();
            if (!m_buttonAdded)
            {
                yield return new WaitForSeconds(2.0f);
                AddHudButton();
            }
        }

        private void AddHudButton()
        {
            if (m_buttonAdded || m_hudController == null) return;

            try
            {
                FieldInfo field = typeof(HudController).GetField("m_calendarControls", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null && field.GetValue(m_hudController) is UiComponent calendarNode)
                {
                    UiComponent best = null;
                    int bestCount = 0;
                    findNodeWithMostChildren(calendarNode, ref best, ref bestCount, 0);

                    if (best != null && bestCount >= 3)
                    {
                        var button = new ButtonIconGlow("Assets/Unity/UserInterface/Toolbar/Power.svg", ToggleWindow);
                        button.Tooltip(new LocStrFormatted("⚡ Electric Power Governor (Alt+P or F8)"));
                        best.InsertAt(0, button);
                        m_buttonAdded = true;
                        Log.Info("PowerThresholdMod: Successfully added Power Governor HUD button!");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PowerThresholdMod: Failed to attach HUD button: {ex.Message}");
            }
        }

        private static void findNodeWithMostChildren(UiComponent node, ref UiComponent best, ref int bestCount, int depth)
        {
            if (depth > 10 || node == null) return;
            int count = node.ChildrenCount;
            if (count > bestCount)
            {
                best = node;
                bestCount = count;
            }
            foreach (var child in node.AllChildren)
            {
                findNodeWithMostChildren(child, ref best, ref bestCount, depth + 1);
            }
        }

        private void Update()
        {
            bool altP = (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) && Input.GetKeyDown(KeyCode.P);
            bool f8 = Input.GetKeyDown(KeyCode.F8);

            if (altP || f8)
            {
                ToggleWindow();
            }

            // Periodically refresh window status display every ~15 render frames
            if (PowerGovernorWindow.IsWindowOpen)
            {
                m_frameCounter++;
                if (m_frameCounter >= 15)
                {
                    m_frameCounter = 0;
                    PowerGovernorWindow.RefreshStatusIfOpen();
                }
            }
        }

        public void ToggleWindow()
        {
            if (m_protosDb == null || m_governorManager == null || m_uiRoot == null) return;

            if (PowerGovernorWindow.IsWindowOpen)
            {
                PowerGovernorWindow.CloseWindow();
                clearEscapeHandler();
            }
            else
            {
                PowerGovernorWindow.OpenWindow(m_protosDb, m_governorManager, m_uiRoot);
                m_openFrame = Time.frameCount;
                if (!m_escapeRegistered && m_escapeManager != null)
                {
                    m_escapeManager.AddRootEscapeHandler(this);
                    m_escapeRegistered = true;
                }
            }
        }

        public bool OnEscape()
        {
            if (!PowerGovernorWindow.IsWindowOpen || Time.frameCount <= m_openFrame)
            {
                return false;
            }
            PowerGovernorWindow.CloseWindow();
            // Note: IRootEscapeHandler is automatically unregistered by COI when returning true.
            m_escapeRegistered = false;
            return true;
        }

        private void clearEscapeHandler()
        {
            if (m_escapeRegistered && m_escapeManager != null)
            {
                m_escapeManager.ClearRootEscapeHandler(this);
                m_escapeRegistered = false;
            }
        }

        private void TryRegisterModSettingsTab()
        {
            try
            {
                var hostObj = GameObject.Find("CoI.AutoHelpers.ModSettingsHost");
                if (hostObj == null) return;

                foreach (var mb in hostObj.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var registerMethod = mb.GetType().GetMethod("RegisterExternalTab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new[] { typeof(string), typeof(LocStrFormatted), typeof(LocStrFormatted), typeof(int), typeof(Func<UiComponent>), typeof(string) }, null);

                    if (registerMethod != null)
                    {
                        Func<UiComponent> buildTab = () =>
                        {
                            var win = new PowerGovernorWindow(m_protosDb, m_governorManager);
                            return win;
                        };

                        registerMethod.Invoke(mb, new object[] {
                            "PowerThresholdMod",
                            new LocStrFormatted("Power Governor"),
                            new LocStrFormatted("⚡ Power Governor"),
                            90,
                            buildTab,
                            "Assets/Unity/UserInterface/Toolbar/Power.svg"
                        });
                        Log.Info("PowerThresholdMod: Successfully registered with ModSettings tab!");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PowerThresholdMod: ModSettings integration notice: {ex.Message}");
            }
        }
    }
}
