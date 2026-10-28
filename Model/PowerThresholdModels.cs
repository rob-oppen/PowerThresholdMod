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
        public float PercentThreshold { get; set; } = 20.0f; // 0% to 100%
        public float MwThreshold { get; set; } = 50.0f;      // 0 MW to Capacity MW
        public float Hysteresis { get; set; } = 2.0f;

        public float ThresholdValue
        {
            get => (Mode == ThresholdMode.Percentage) ? PercentThreshold : MwThreshold;
            set
            {
                if (Mode == ThresholdMode.Percentage)
                {
                    PercentThreshold = Math.Max(0f, Math.Min(100f, value));
                }
                else
                {
                    MwThreshold = Math.Max(0f, value);
                }
            }
        }

        public MachinePowerRule Clone()
        {
            return new MachinePowerRule
            {
                IsEnabled = this.IsEnabled,
                Mode = this.Mode,
                PercentThreshold = this.PercentThreshold,
                MwThreshold = this.MwThreshold,
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
        public float PercentThreshold { get; set; } = 20.0f;
        public float MwThreshold { get; set; } = 50.0f;
        public float Hysteresis { get; set; } = 2.0f;

        public float ThresholdValue
        {
            get => (Mode == ThresholdMode.Percentage) ? PercentThreshold : MwThreshold;
            set
            {
                if (Mode == ThresholdMode.Percentage)
                {
                    PercentThreshold = Math.Max(0f, Math.Min(100f, value));
                }
                else
                {
                    MwThreshold = Math.Max(0f, value);
                }
            }
        }

        public MachineInstanceOverride Clone()
        {
            return new MachineInstanceOverride
            {
                HasOverride = this.HasOverride,
                IsEnabled = this.IsEnabled,
                Mode = this.Mode,
                PercentThreshold = this.PercentThreshold,
                MwThreshold = this.MwThreshold,
                Hysteresis = this.Hysteresis
            };
        }
    }
}
