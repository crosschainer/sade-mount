using Sandbox;

namespace Sade.Mount;

internal static class SpawnedProp
{
    internal static void Release(GameObject root)
    {
        // Sandbox's install prompt checks for a Prop in OnStart/OnRefresh. Once a
        // successfully loaded model becomes an actor, that Prop no longer exists.
        // Disable the prompt before removing it, including when discovery happens
        // before the first OnStart. Keep its metadata and the root's undo ownership.
        foreach (var component in root.Components.GetAll<Component>(FindMode.EverythingInSelf))
            if (component.GetType().FullName == "MountMetadata") component.Enabled = false;
        foreach (var prop in root.Components.GetAll<Prop>(FindMode.EverythingInSelf).ToArray())
        {
            prop.Enabled = false;
            prop.Destroy();
        }
    }
}
