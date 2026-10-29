namespace Sade.Mount;

/// <summary>Filters measured movement across render/physics ticks without restarting a gait on brief stalls.</summary>
internal sealed class LocomotionPlayback
{
    internal float Speed { get; private set; }
    internal bool Walking { get; private set; }
    internal bool Running { get; private set; }
    private float stoppedFor;

    internal void Update(float measuredSpeed, float delta, float walkSpeed, bool wantsRun)
    {
        measuredSpeed = float.IsFinite(measuredSpeed) ? Math.Max(0, measuredSpeed) : 0;
        delta = float.IsFinite(delta) ? Math.Clamp(delta, 0, .1f) : 0;
        Speed = delta > 0 ? Speed + (measuredSpeed - Speed) * (1 - MathF.Exp(-delta / .1f)) : measuredSpeed;
        stoppedFor = measuredSpeed < 1.5f ? stoppedFor + delta : 0;
        if (!Walking) Walking = Speed > 3;
        else if (stoppedFor >= .2f && Speed < 3) Walking = false;
        Running = Walking && (wantsRun || Speed > Math.Max(1, walkSpeed) * (Running ? 1.25f : 1.65f));
    }
}
