using System;

namespace PowerThresholdMod.Model
{
    public enum ThresholdMode
    {
        Percentage = 0,
        MegaWatts = 1
    }

    [Serializable]
    public class MachinePowerRule
    {
        public bool IsEnabled { get; set; } = true;
        public ThresholdMode Mode { get; set; } = ThresholdMode.Percentage;
        public float ThresholdValue { get; set; } = 20.0f; // 20% or 10 MW default
        public float Hysteresis { get; set; } = 2.0f;     // 2% or 1 MW hysteresis

        public MachinePowerRule Clone()
        {
            return new MachinePowerRule
            {
                IsEnabled = this.IsEnabled,
                Mode = this.Mode,
                ThresholdValue = this.ThresholdValue,
                Hysteresis = this.Hysteresis
            };
        }
    }

    [Serializable]
    public class MachineInstanceOverride
    {
        public bool HasOverride { get; set; } = false;
        public bool IsEnabled { get; set; } = true;
        public ThresholdMode Mode { get; set; } = ThresholdMode.Percentage;
        public float ThresholdValue { get; set; } = 20.0f;
        public float Hysteresis { get; set; } = 2.0f;

        public MachineInstanceOverride Clone()
        {
            return new MachineInstanceOverride
            {
                HasOverride = this.HasOverride,
                IsEnabled = this.IsEnabled,
                Mode = this.Mode,
                ThresholdValue = this.ThresholdValue,
                Hysteresis = this.Hysteresis
            };
        }
    }
}
