using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

// Load the SAME shipping assembly against each SDK's runtime dependencies. This
// catches missing interface implementations (including Emby 4.10 AddConsumer),
// while the separate compile matrix catches source/API signature drift.
if (args.Length != 2) throw new ArgumentException("Usage: <shipping plugin DLL> <SDK publish directory>");
var shipping = Path.GetFullPath(args[0]);
var sdk = Path.GetFullPath(args[1]);
using var context = new SdkContext(sdk, Path.GetDirectoryName(shipping)!);
try
{
    var plugin = context.LoadFromAssemblyPath(shipping);
    var mappings = 0;
    var liveStream = false;
    foreach (var type in plugin.GetTypes().Where(type => !type.IsAbstract && !type.IsInterface))
    foreach (var contract in type.GetInterfaces())
    {
        var map = type.GetInterfaceMap(contract);
        if (map.TargetMethods.Any(method => method == null || method.IsAbstract)) throw new TypeLoadException(type.FullName);
        mappings++;
        if (contract.Name == "ILiveStream") liveStream = true;
    }
    if (!liveStream || mappings == 0) throw new TypeLoadException("No live-stream interface mapping was verified");
    Console.WriteLine($"PASS shipping plugin loads with {Path.GetFileName(sdk)}: {mappings} interface mappings checked");
}
catch (ReflectionTypeLoadException error)
{
    foreach (var cause in error.LoaderExceptions) Console.Error.WriteLine(cause);
    throw;
}

sealed class SdkContext : AssemblyLoadContext, IDisposable
{
    private readonly string[] _directories;
    internal SdkContext(params string[] directories) : base(isCollectible: true) => _directories = directories;
    protected override Assembly Load(AssemblyName name)
    {
        if (name.Name!.StartsWith("System.") || name.Name.StartsWith("Microsoft."))
        {
            try { return Default.LoadFromAssemblyName(name); }
            catch (FileNotFoundException) { }
        }
        foreach (var directory in _directories)
        {
            var file = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(file)) return LoadFromAssemblyPath(file);
        }
        return null!;
    }
    public void Dispose() => Unload();
}
