# Power Threshold Governor (Captain of Industry Mod)

![Power Threshold Governor Thumbnail](thumbnail.png)

An intelligent electrical load-shedding and power management system for **Captain of Industry**. Protect your factory from catastrophic blackout death spirals by automatically pausing heavy consumers (such as Smelters, Electrolyzers, Arc Furnaces, Computing Data Centers, Research Labs, etc.) during power or fuel shortages, and smoothly bringing them back online in controlled stages.

Compatible with vanilla saves — safely added to or removed from existing saves at any time.

---

## Features

### 1. Dedicated Governor Window (`Alt + P`, `F8`, or Top HUD Icon)
- **⚡ Global Rules Tab:**
  - View live grid capacity, production, demand, and net reserve.
  - **Live Island Telemetry:** Displays the exact count of built machines on the island and their live power consumption alongside maximum potential draw (e.g. `36 MW / 81 MW`).
  - Configure pause thresholds per machine type using either **Percentage mode** (`%` of grid capacity) or exact **Megawatt mode** (`MW`).
  - Real-time status indicators showing how many units are currently throttled or being tested.
- **⏸ Paused Buildings Tab:**
  - Grouped overview of all currently throttled machines categorized by building type.
  - Displays whether a machine was paused by a Global Rule or an individual Local Override.
  - Bulk resume buttons (`Resume All [Building]`) or global `Resume All`.
- **📖 Guide & Help Tab:**
  - In-game strategy guide for configuring priority tiers (Always On critical life-support, Tier I core refining, Tier II manufacturing, Tier III heavy consumers).
  - Detailed explanation of how Staged Grid Restoration works.

### 2. Staged Grid Restoration (Blackout Loop Prevention)
In Captain of Industry, power generators only produce what buildings demand. When buildings pause, demand drops and generators throttle down. Unpausing everything at once would create an immediate load shock and cause endless blackout flickering.
- **10-Second Stability Check:** When the grid satisfies 100% of demand (zero deficit) for 10 continuous seconds, the governor safely unpauses **ONE** single building to test real generator headroom.
- **Progressive Ramp-Up:** If generators absorb the load smoothly, that building remains active, and 10 seconds later the next building is tested.
- **Overload Protection & Backoff:** If unpausing triggers a deficit (generators still lack steam/fuel), the building is immediately re-paused within 1 second and the mod enters a 20-second cooldown to let fuel buffers rebuild.
- **Manual Override:** Click `[Resume All]` at any time to bypass the test and restart all machines immediately.

### 3. Local Machine Inspector Override
- Clicking any machine on the map opens its inspector with a native **⚡ Power Governor** override panel.
- **Override Global Threshold:** Toggle to assign custom thresholds to high-priority individual machines.
- Real-time status display (`● Normal Operation` vs `⏸ Auto-Throttled`).

### 4. Zero Simulation Overhead
- Leverages the game's internal `ElectricityManager` cached prototype statistics.
- When the window is closed, there is **zero extra CPU overhead** on tick simulation.

---

## Controls

| Action | Shortcut |
|---|---|
| Toggle Governor Window | `Alt + P` or `F8` or Click the ⚡ icon in the top calendar HUD |
| Machine Inspector Override | Click machine -> Scroll to bottom of inspector panel |
| Close Window | `Esc` or `Alt + P` or `F8` |

---

## Installation & Upload

### For Players
Extract the release `.zip` into:
```
%APPDATA%\Captain of Industry\Mods\
```
Ensure the folder structure is:
`%APPDATA%\Captain of Industry\Mods\PowerThresholdMod\` containing `manifest.json`, `PowerThresholdMod.dll`, `0Harmony.dll`, and `thumbnail.png`.

### Development & Building
Run in the project directory:
```bash
dotnet build -c Release
```
This automatically compiles against `.NET Framework 4.8` / COI game assemblies and deploys directly to your `%APPDATA%\Captain of Industry\Mods\PowerThresholdMod` folder.

## License & Legal Notices

### Mod License
This mod is licensed under the **Captain of Industry Open License (COI-Open)**. See [LICENSE](file:///x:/coding/Personal/PowerThresholdMod/LICENSE) for full terms.

### Captain of Industry Modding Policy Notice
> *This Mod includes short excerpts or references to Captain of Industry Game Code. Any such Game Code is © MaFi Games and is used only under the Captain of Industry Modding Policy.*

- **Author:** rob-oppen
- Developed for **Captain of Industry** (v0.8.0+).
- Modding Policy: [https://coigame.com/Legal/Modding-Policy](https://coigame.com/Legal/Modding-Policy)

