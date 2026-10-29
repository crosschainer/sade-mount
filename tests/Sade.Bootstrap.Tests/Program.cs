using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 2) throw new ArgumentException("Usage: Sade.Bootstrap.Tests <sade.dll> <sbox-directory>");
var engine = Path.GetFullPath(args[1]);
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var name = new AssemblyName(e.Name).Name!;
    var path = Path.Combine(engine, "bin", "managed", name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var mountPath = Path.GetFullPath(args[0]);
if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Sade.Contracts"))
    throw new Exception("The regression requires a fresh process with no preloaded reader contract.");
var assembly = Assembly.LoadFile(mountPath);
var type = assembly.GetTypes().Single(t => t.Name == "SadeMount");
var mount = Activator.CreateInstance(type)!;
if (!Equals(type.GetProperty("Ident")!.GetValue(mount), "sade")) throw new Exception("Wrong mount identifier.");
// Resolve every signature, including contract types, as the host's reflection code does.
foreach (var t in assembly.GetTypes())
{
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) _ = p.PropertyType;
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) _ = f.FieldType;
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        _ = m.ReturnType;
        foreach (var p in m.GetParameters()) _ = p.ParameterType;
    }
}
var contract = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Sade.Contracts");
var expectedContract = Path.Combine(Path.GetDirectoryName(mountPath)!, "Sade.Contracts.dll");
if (!string.Equals(contract.Location, expectedContract, StringComparison.OrdinalIgnoreCase))
    throw new Exception($"Private reader contract loaded from {contract.Location}, expected {expectedContract}.");
if (AssemblyLoadContext.GetLoadContext(contract) != AssemblyLoadContext.Default)
    throw new Exception("Reader contract must be shared through the default load context.");
var stablePrefab = assembly.GetType("Sade.Mount.StablePrefab", throwOnError: true)!;
var reload = stablePrefab.GetField("Reload", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) as MethodInfo;
if (reload is null || reload.ReturnType != typeof(bool) || reload.GetParameters().Length != 0)
    throw new Exception("Installed s&box does not provide the prefab registry/cache reload lifecycle required for stable saved prefabs.");
Console.WriteLine("PASS: mount loads through Assembly.LoadFile with private contracts and no engine DLL copies.");
