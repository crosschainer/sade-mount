using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Sade.Contracts;

namespace Sade.Mount;

internal static class Bootstrap
{
    // s&box loads mount DLLs with Assembly.LoadFile; adjacent dependencies are not probed automatically.
    // GetTypes runs before this initializer. Private contract value types must therefore stay out
    // of instance fields (including closure fields and generic field arguments) in this assembly.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Mount plugins need private dependency resolution before the host constructs the mount.")]
    [ModuleInitializer]
    internal static void InstallResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name);
            if (name.Name != "Sade.Contracts") return null;
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
            if (loaded is not null) return loaded;
            var location = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location)!, "Sade.Contracts.dll");
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(location);
        };
    }

    internal static (IGameReader Reader, ReaderContext Context) CreateReader()
    {
        var folder = System.IO.Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location)!;
        var path = System.IO.Path.Combine(folder, "backend", "Sade.Reader.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("The SADE reader is missing. Re-run scripts/Install.ps1.", path);
        var context = new ReaderContext(path);
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);
            var type = assembly.GetTypes().Single(t => !t.IsAbstract && typeof(IGameReader).IsAssignableFrom(t));
            return ((IGameReader)Activator.CreateInstance(type)!, context);
        }
        catch { context.Unload(); throw; }
    }
}

internal sealed class ReaderContext(string path) : AssemblyLoadContext("SADE reader", isCollectible: true)
{
    private readonly AssemblyDependencyResolver resolver = new(path);
    private readonly string folder = System.IO.Path.GetDirectoryName(path)!;

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IGameReader).Assembly.GetName().Name) return typeof(IGameReader).Assembly;
        if (name.Name?.StartsWith("Sandbox.", StringComparison.Ordinal) == true)
            throw new InvalidOperationException("The asset reader must not load engine assemblies.");
        var resolved = resolver.ResolveAssemblyToPath(name);
        if (resolved is null)
        {
            var adjacent = System.IO.Path.Combine(folder, name.Name + ".dll");
            if (File.Exists(adjacent)) resolved = adjacent;
        }
        return resolved is null ? null : LoadFromAssemblyPath(resolved);
    }

    protected override nint LoadUnmanagedDll(string name)
    {
        var resolved = resolver.ResolveUnmanagedDllToPath(name);
        return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
    }
}
