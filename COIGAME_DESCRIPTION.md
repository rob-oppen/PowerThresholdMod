# Power Threshold Governor

An intelligent electrical load-shedding and power management system for your island. Protect your factory from catastrophic blackout death spirals by automatically pausing non-critical, heavy consumers during fuel or power shortages, and smoothly bringing them back online in controlled stages.

---

### ⚡ 1. Building Selection & Demand-Driven Shedding
Select which building types you want the governor to manage during power shortages—no complex percentage or megawatt math required.

- **Zero-Fuss Selection:** Simply check the box for each building type you want to pause (e.g. Arc Furnaces, Computing Data Centers, Research Labs).
- **Demand-Driven Engagement:** The governor only engages when island electrical demand outpaces generator supply.
- **Priority-Based Shedding (P10 down to P1):** Pauses lowest priority machines first until power demand and supply match. Critical machines (Priority 1) remain running!

![Global Rules Window Screenshot](https://raw.githubusercontent.com/rob-oppen/PowerThresholdMod/master/docs/global_rules.png)

---

### 🎯 2. Built-in General Priority Integration (P1 - P10)
Directly leverages Captain of Industry's built-in General Priority system (P1 through P10) to decide which specific machines shut down first and recover first.

- **Priority Shedding (Priority 10 -> Priority 1):** If you have 10 Arc Furnaces and power is insufficient, the governor pauses the Priority 10 machine first, stepping all the way down to Priority 1 last.
- **Stepped Recovery (Priority 1 -> Priority 10):** As your power grid stabilizes, restoration starts at Priority 1 first, slowly increasing towards Priority 10.
- **Color-Coded Badges:** View the priority of each paused machine (`[ P10 ]`, `[ P1 ]`) in the Paused Buildings tab and machine inspector.

---

### 📊 3. Live Island Telemetry & Power Tracking
Keep complete visibility over your island's electrical demand right inside the Governor interface.

- **Built Machine Counter:** Displays the exact count of built, active machines of each type across your entire island (e.g. `[ 9 ] Boilers`, `[ 12 ] Electrolyzers`).
- **Real-Time vs Max Draw:** Tracks live power draw alongside maximum potential consumption (e.g. `36 MW / 81 MW`).
- **Zero Simulation Overhead:** Reuses the game engine's internal statistics cache—virtually zero performance impact on your game loop.

![Island Power Telemetry Screenshot](https://raw.githubusercontent.com/rob-oppen/PowerThresholdMod/master/docs/telemetry.png)

---

### ⏸ 3. Paused Buildings Tab & Bulk Resume
Never wonder which machines were turned off or why.

- **Grouped Overview:** See all machines currently throttled by the governor, grouped cleanly by building type.
- **Rule Source Attribution:** Shows whether each building was paused by a Global Rule or a Local Override.
- **One-Click Recovery:** Click `[Resume All]` or unpause specific building categories on demand.

![Paused Buildings Tab Screenshot](https://raw.githubusercontent.com/rob-oppen/PowerThresholdMod/master/docs/paused_buildings.png)

---

### 🛡️ 4. Staged Grid Restoration (Blackout Loop Prevention)
In Captain of Industry, generators only produce what buildings demand. When buildings pause, demand drops and generators throttle down. Unpausing everything at once creates an immediate load surge that trips recovering boilers back into a blackout.

- **10-Second Stability Check:** When the grid satisfies 100% of demand (zero deficit) for 10 continuous seconds, the governor safely unpauses **ONE** single machine to verify real generator headroom.
- **Progressive Ramp-Up:** If generators absorb the load smoothly, that building remains active, and 10 seconds later the next building is tested.
- **Instant Deficit Abort:** If reviving a machine creates a power deficit, it is safely paused within 1 second and recovery enters a 20-second cooldown so steam and fuel buffers can rebuild.
- **Manual Override:** Click `[Resume All]` at any time to bypass the test and restart all machines immediately.

![Staged Grid Restoration Screenshot](https://raw.githubusercontent.com/rob-oppen/PowerThresholdMod/master/docs/staged_restoration.png)

---

### 🔧 5. Local Machine Inspector Overrides
Need a specific critical facility to stay running while others pause?

- **Native Inspector Integration:** Click any machine on the map to open its inspector with the embedded **⚡ Power Governor** override panel.
- **Custom Thresholds:** Set dedicated individual thresholds for mission-critical smelters, pumps, or labs.
- **Live Status:** Directly view whether the machine is operating normally or auto-throttled.

![Machine Inspector Override Screenshot](https://raw.githubusercontent.com/rob-oppen/PowerThresholdMod/master/docs/inspector_override.png)

---

### 🖱️ Controls & Shortcuts

| Action | Shortcut |
|---|---|
| **Toggle Governor Window** | `Alt + P` or `F8` or Click the ⚡ icon in the top calendar HUD |
| **Machine Override Panel** | Click any machine -> Scroll to the bottom of the inspector panel |
| **Close Window** | `Esc` or `Alt + P` or `F8` |

---

### 💾 Compatibility
- **Compatible with vanilla saves:** Safely added to or removed from existing saves at any time.
- **Game Version:** Tested for Captain of Industry v0.8.0+.
