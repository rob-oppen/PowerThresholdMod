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
        // When true, governor is allowed to pause this building type when demand outpaces supply
        public bool IsEnabled { get; set; } = false;

        // Legacy compatibility properties
        public ThresholdMode Mode { get; set; } = ThresholdMode.Percentage;
        public float PercentThreshold { get; set; } = 20.0f;
        public float MwThreshold { get; set; } = 50.0f;
        public float Hysteresis { get; set; } = 2.0f;
        public float ThresholdValue { get => PercentThreshold; set => PercentThreshold = value; }

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
    public class RecipeOutputRule
    {
        // When true, governor is allowed to pause machines producing this recipe output when demand outpaces supply
        public bool IsEnabled { get; set; } = false;

        public RecipeOutputRule Clone()
        {
            return new RecipeOutputRule
            {
                IsEnabled = this.IsEnabled
            };
        }
    }

    [Serializable]
    public class MachineInstanceOverride
    {
        public bool HasOverride { get; set; } = false;
        public bool IsEnabled { get; set; } = true;

        // Legacy compatibility properties
        public ThresholdMode Mode { get; set; } = ThresholdMode.Percentage;
        public float PercentThreshold { get; set; } = 20.0f;
        public float MwThreshold { get; set; } = 50.0f;
        public float Hysteresis { get; set; } = 2.0f;
        public float ThresholdValue { get => PercentThreshold; set => PercentThreshold = value; }

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
