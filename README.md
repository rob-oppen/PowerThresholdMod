# Power Threshold Governor (Captain of Industry Mod)

A gameplay automation mod for **Captain of Industry** that allows players to set electricity headroom thresholds—either as a **percentage of total production** or as an **absolute Megawatt (MW) headroom reserve**—before machines will unpause/activate or automatically throttle to prevent island brownouts.

---

## Features

1. **Dedicated Governor Window (`Shift + P`)**:
   - Live grid metrics: Real-time generation capacity (MW), power demand (MW), net reserve (MW), and percentage.
   - Global rules for all electric machines (Smelters, Blast Furnaces, Electric Arc Furnaces, Electrolyzers, Mills, Crushers, etc.).
   - Switch between **Percentage mode** (`%`) and **Megawatt mode** (`MW`).
   - Sliders to configure trigger thresholds (e.g., pause if headroom is below 20% or below 10 MW).

2. **Localized Machine Inspector Override**:
   - Clicking on any machine opens its inspector with a native **⚡ Power Governor** panel.
   - **Override Global Threshold**: Toggle to override the global prototype rule for this specific machine.
   - Set custom threshold and view real-time status:
     - `● Normal Operation (Reserve OK)`
     - `⏸ Auto-Throttled (Grid reserve below threshold)`

3. **Hysteresis & Anti-Flicker Protection**:
   - Built-in +2% / +1.0 MW buffer when unpausing to prevent machines from rapidly oscillating between on and off states every tick.
   - Throttled state memory ensures the mod only unpauses machines it throttled, preserving manual player pauses.

4. **Persistent Settings**:
   - Settings are automatically saved to `%APPDATA%\Captain of Industry\Mods\PowerThresholdMod\settings.json`.

---

## Controls

| Action | Control |
|---|---|
| Toggle Governor Window | `Shift + P` |
| Machine Override | Click machine -> Scroll to bottom of inspector panel |

---

## Development & Building

### Prerequisites
- .NET 8 SDK (installed on your system)
- Captain of Industry installed (`H:\SteamLibrary\steamapps\common\Captain of Industry`)

### Build & Deploy
Run in this directory:
```bash
dotnet build -c Release
```
This automatically compiles the mod against `.NET Framework 4.8` / COI game assemblies and copies the output (`PowerThresholdMod.dll`, `0Harmony.dll`, `manifest.json`) directly to:
`%APPDATA%\Captain of Industry\Mods\PowerThresholdMod`

---

## File Structure
- `Model/PowerThresholdModels.cs`: Threshold models, rule definitions, and modes (`Percentage` vs `MegaWatts`).
- `Engine/PowerGovernorManager.cs`: Grid metrics reader, periodic evaluation loop, and auto-pause/unpause engine.
- `UI/PowerGovernorWindow.cs`: Native in-game management window with live grid telemetry and per-machine-type sliders.
- `UI/MachineInspectorPatch.cs`: Harmony hook into `MachineInspector` to embed instance-level override controls.
- `PowerThresholdModEntry.cs`: Mod entry point implementing `Mafi.Core.Mods.IMod`.
