using System.Reflection;
using Sandbox;
using Sandbox.Mounting;

namespace Sade.Mount;

internal static class StablePrefab
{
    // PrefabBuilder assigns fresh IDs and registers the original root before returning.
    // The engine exposes the JSON but not its GUID registry refresh; use the existing
    // reload lifecycle so registry entries and any cached scene are updated together.
    private static readonly MethodInfo Reload = typeof(GameResource).GetMethod("PostReloadInternal",
        BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new MissingMethodException(typeof(GameResource).FullName, "PostReloadInternal");

    internal static PrefabFile Create(PrefabBuilder builder, string path)
    {
        var prefab = builder.Create() ?? throw new InvalidDataException($"Could not build prefab {path}.");
        StablePrefabIds.Apply(prefab.RootObject, path);
        if (Reload.Invoke(prefab, null) is not true)
            throw new InvalidOperationException($"Could not finalize stable prefab identities for {path}.");
        return prefab;
    }
}
