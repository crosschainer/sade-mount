using Sandbox;

namespace Sade.Mount;

internal sealed partial class NativePedestrian
{
    private const string PosedMarker = "sade_posed";
    private ModelPhysics? posePhysics;
    private bool poseSuspended;
    private Vector3? previousPelvis;
    private int surfacedPoseBodies = -1;
    private Rigidbody? surfacedFirstBody;
    internal bool IsPosed => GameObject.Tags.Has(PosedMarker, false);

    private void InitializePosing()
    {
        posePhysics = Renderer.Components.Get<ModelPhysics>(FindMode.EverythingInSelf);
        if (posePhysics.IsValid() || IsProxy || Renderer.Model.Physics is not { Parts.Count: > 1 }) return;

        // Keep the native bodies alive before the first click: Sandbox's physgun traces
        // individual Rigidbody colliders, and unfreezes the selected body on pickup.
        // ModelPhysics drives these kinematic bodies from the animation until that happens.
        posePhysics = Renderer.Components.Create<ModelPhysics>(false);
        posePhysics.Model = Renderer.Model;
        posePhysics.Renderer = Renderer;
        posePhysics.IgnoreRoot = true;
        posePhysics.MotionEnabled = IsPosed;
        posePhysics.Enabled = true;
        // Initial bone placement during fixed-update promotion must not interpolate from
        // the newly-created bone objects' defaults before their first visible frame.
        foreach (var body in posePhysics.Bodies)
            if (body.Component.IsValid()) body.Component.Transform.ClearInterpolation();
        if (GameObject.Network.Active) GameObject.Network.Refresh();
    }

    private bool UpdatePosing()
    {
        // On proxies the built-in component and its body list can arrive after discovery.
        if (!posePhysics.IsValid())
            posePhysics = Renderer.Components.Get<ModelPhysics>(FindMode.EverythingInSelf);
        if (posePhysics is { IsValid: true } physics && physics.Bodies.Count > 1)
        {
            RestoreFleshSurfaces(physics);
            // The broad navigation capsule would intercept the ray before a limb.
            // Damage still finds NativePedestrian through the bone object's ancestors.
            var capsule = Components.Get<CapsuleCollider>(FindMode.EverythingInSelf);
            if (capsule is not null) capsule.Enabled = false;

            if (!IsProxy && !IsPosed && (physics.MotionEnabled ||
                physics.Bodies.Any(body => body.Component.IsValid() && body.Component.MotionEnabled)))
            {
                SuspendForPose();
                Tags.Add(PosedMarker);
                // Do this once only. Reassigning it later would unfreeze limbs that the
                // player deliberately froze, including poses restored from a saved scene.
                physics.MotionEnabled = true;
                if (GameObject.Network.Active) GameObject.Network.Refresh();
            }
        }
        if (!IsPosed) return false;
        SuspendForPose();
        Agent.UpdatePosition = Agent.UpdateRotation = false;
        Agent.Enabled = false;
        if (!IsProxy && posePhysics is { IsValid: true } posed)
        {
            var pelvis = posed.Bodies.FirstOrDefault(b => b.Component.IsValid() &&
                b.Component.GameObject.Name.Equals("Pelvis", StringComparison.OrdinalIgnoreCase)).Component;
            if (pelvis.IsValid())
            {
                var position = pelvis.WorldPosition;
                if (previousPelvis is { } previous) WorldPosition += position - previous;
                previousPelvis = position;
            }
            // ModelPhysics uses these local records for frozen bodies in editor mode.
            // Preserve the posed coordinates rather than the rig's initial bind pose.
            for (var i = 0; i < posed.Bodies.Count; i++)
            {
                var body = posed.Bodies[i];
                if (!body.Component.IsValid() || body.Component.MotionEnabled) continue;
                var local = posed.WorldTransform.ToLocal(body.Component.WorldTransform);
                if (!body.LocalTransform.Equals(local)) posed.Bodies[i] = body with { LocalTransform = local };
            }
        }
        return true;
    }

    private void RestoreFleshSurfaces(ModelPhysics physics)
    {
        // Old saved poses serialize their generated colliders with the previous generic
        // surface. Repair those without rebuilding bodies or disturbing frozen limbs.
        // The first body also detects a rebuilt/replaced rig with the same body count.
        var first = physics.Bodies[0].Component;
        if (surfacedPoseBodies == physics.Bodies.Count && surfacedFirstBody == first) return;
        var flesh = Surface.FindByName("flesh");
        if (flesh is null) return;
        var complete = true;
        foreach (var body in physics.Bodies)
        {
            if (!body.Component.IsValid()) { complete = false; continue; }
            var found = false;
            foreach (var collider in body.Component.Components.GetAll<Collider>(FindMode.EverythingInSelf))
            {
                collider.Surface = flesh;
                found = true;
            }
            complete &= found;
        }
        if (!complete) return; // Networked bodies/colliders can arrive in separate updates.
        surfacedPoseBodies = physics.Bodies.Count;
        surfacedFirstBody = first;
    }

    private void SuspendForPose()
    {
        if (poseSuspended) return;
        poseSuspended = true;
        StopSpeech();
        StopMoving();
        ClearAction();
        ClearAimOverrides();
        aimBlend = 0;
        ReleaseCombatTarget();
        CancelShot();
        reloadHandle?.Stop();
        reloadHandle = null;
        StopContinuousAudio();
        reloadRemaining = muzzleRemaining = 0;
        aiming = firingAction = false;
        if (muzzleLight.IsValid()) muzzleLight.Enabled = false;
        Renderer.PlaybackRate = 0;
        // Leave the weapon model attached to the physical hand. In particular, do not
        // clear bone overrides from subsequent AI/aim updates while ModelPhysics owns them.
    }
}
