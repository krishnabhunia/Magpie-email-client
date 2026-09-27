// DpDump — dumps WPF DependencyProperty / CLR property / event metadata from reference
// assemblies (no Windows needed) so build/xaml_check.py can validate XAML statically.
//
// Usage:
//   dotnet run --project build/DpDump -- <out.json> [extra.dll ...]
//        [--wpf-ref <dir>] [--netcore-ref <dir>]
//
// Defaults: WPF refs from ~/.nuget/packages/microsoft.windowsdesktop.app.ref/<newest 8.x>/ref/net8.0,
// netcore refs from <dotnet root>/packs/Microsoft.NETCore.App.Ref/<newest 8.x>/ref/net8.0.
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

static string? NewestRefDir(string root)
{
    if (!Directory.Exists(root)) return null;
    return Directory.GetDirectories(root)
        .Select(d => Path.Combine(d, "ref", "net8.0"))
        .Where(Directory.Exists)
        .OrderByDescending(d => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(d))!), out var v) ? v : new Version(0, 0))
        .FirstOrDefault();
}

string? outPath = null, wpfRef = null, coreRef = null;
var extras = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--wpf-ref": wpfRef = args[++i]; break;
        case "--netcore-ref": coreRef = args[++i]; break;
        default:
            if (outPath == null) outPath = args[i]; else extras.Add(Path.GetFullPath(args[i]));
            break;
    }
}
if (outPath == null)
{
    Console.Error.WriteLine("usage: DpDump <out.json> [extra.dll ...] [--wpf-ref dir] [--netcore-ref dir]");
    return 2;
}

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(home, ".nuget", "packages");
wpfRef ??= NewestRefDir(Path.Combine(nugetRoot, "microsoft.windowsdesktop.app.ref"));
if (coreRef == null)
{
    var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
        ?? Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)!)!; // .../shared/Microsoft.NETCore.App/<v>
    // typeof(object).Location = <root>/shared/Microsoft.NETCore.App/<ver>/System.Private.CoreLib.dll
    var candidates = new[] { dotnetRoot, Path.GetDirectoryName(dotnetRoot)!, "/opt/dotnet", "/usr/share/dotnet", "/usr/lib/dotnet" };
    foreach (var c in candidates)
    {
        coreRef = NewestRefDir(Path.Combine(c, "packs", "Microsoft.NETCore.App.Ref"));
        if (coreRef != null) break;
    }
}
if (wpfRef == null || coreRef == null)
{
    Console.Error.WriteLine($"could not locate reference assemblies (wpf={wpfRef}, netcore={coreRef})");
    return 2;
}
Console.Error.WriteLine($"WPF refs:     {wpfRef}");
Console.Error.WriteLine($"netcore refs: {coreRef}");

var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
// Later directories win: the netcore ref pack ships facade stubs (e.g. WindowsBase.dll)
// that must be overridden by the real WPF reference assemblies.
foreach (var dir in new[] { coreRef, wpfRef }.Concat(extras.Select(Path.GetDirectoryName).Distinct()!))
    foreach (var dll in Directory.GetFiles(dir!, "*.dll"))
        paths[Path.GetFileNameWithoutExtension(dll)] = dll;
foreach (var e in extras) paths[Path.GetFileNameWithoutExtension(e)] = e;

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths.Values), "System.Runtime");

var targets = new List<Assembly>();
foreach (var n in new[] { "WindowsBase", "PresentationCore", "PresentationFramework", "System.Xaml" })
    targets.Add(mlc.LoadFromAssemblyPath(paths[n]));
// Optional WPF assemblies that also map types into the presentation XML namespace.
foreach (var n in new[] { "System.Windows.Controls.Ribbon", "WindowsFormsIntegration" })
    if (paths.TryGetValue(n, out var p)) targets.Add(mlc.LoadFromAssemblyPath(p));
foreach (var e in extras) targets.Add(mlc.LoadFromAssemblyPath(e));

var wb = targets[0];
var dpType = wb.GetType("System.Windows.DependencyProperty", true)!;
var dpKeyType = wb.GetType("System.Windows.DependencyPropertyKey", true)!;
var reType = targets[1].GetType("System.Windows.RoutedEvent", true)!;

const BindingFlags StaticDecl = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
const BindingFlags InstDecl = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

var types = new SortedDictionary<string, object>(StringComparer.Ordinal);
var xmlns = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
int failures = 0;

foreach (var asm in targets)
{
    // XmlnsDefinitionAttribute(xmlNamespace, clrNamespace)
    try
    {
        foreach (var cad in asm.GetCustomAttributesData())
        {
            if (cad.AttributeType.Name != "XmlnsDefinitionAttribute" || cad.ConstructorArguments.Count < 2) continue;
            var uri = (string)cad.ConstructorArguments[0].Value!;
            var ns = (string)cad.ConstructorArguments[1].Value!;
            if (!xmlns.TryGetValue(uri, out var set)) xmlns[uri] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(ns);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"xmlns attrs failed on {asm.GetName().Name}: {ex.Message}"); }

    Type[] exported;
    try { exported = asm.GetExportedTypes(); }
    catch (ReflectionTypeLoadException ex) { exported = ex.Types.Where(t => t != null).ToArray()!; }

    foreach (var t in exported)
    {
        if (t.IsNested || t.FullName == null) continue;
        try
        {
            var dps = new SortedSet<string>(StringComparer.Ordinal);
            var readOnlyDps = new SortedSet<string>(StringComparer.Ordinal);
            var attached = new SortedSet<string>(StringComparer.Ordinal);
            var routed = new SortedSet<string>(StringComparer.Ordinal);
            var keyFields = new HashSet<string>(StringComparer.Ordinal);

            var fields = t.GetFields(StaticDecl);
            foreach (var f in fields)
                if (f.FieldType == dpKeyType) keyFields.Add(f.Name);

            foreach (var f in fields)
            {
                if (!f.IsPublic || !f.IsInitOnly) continue;
                if (f.FieldType == dpType && f.Name.EndsWith("Property", StringComparison.Ordinal) && f.Name.Length > 8)
                {
                    var name = f.Name[..^8];
                    dps.Add(name);
                    if (keyFields.Contains(f.Name + "Key")) readOnlyDps.Add(name);
                }
                else if (f.FieldType == reType && f.Name.EndsWith("Event", StringComparison.Ordinal) && f.Name.Length > 5)
                    routed.Add(f.Name[..^5]);
            }

            var staticMethods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            var getters = new HashSet<string>(staticMethods.Where(m => m.Name.StartsWith("Get") && m.GetParameters().Length == 1).Select(m => m.Name[3..]));
            var setters = new HashSet<string>(staticMethods.Where(m => m.Name.StartsWith("Set") && m.GetParameters().Length == 2).Select(m => m.Name[3..]));
            foreach (var d in dps)
                if (getters.Contains(d) || setters.Contains(d)) attached.Add(d);

            var props = new SortedSet<string>(StringComparer.Ordinal);
            var settable = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in t.GetProperties(InstDecl))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                props.Add(p.Name);
                if (p.SetMethod is { IsPublic: true }) settable.Add(p.Name);
            }

            // Reference assemblies strip the internal DependencyPropertyKey fields, so a read-only
            // DP is recognised heuristically: its CLR wrapper exists but has no public setter and
            // there is no static SetX accessor.
            foreach (var d in dps)
                if (props.Contains(d) && !settable.Contains(d) && !setters.Contains(d)) readOnlyDps.Add(d);

            var events = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var ev in t.GetEvents(InstDecl)) events.Add(ev.Name);

            string? contentProp = null;
            foreach (var cad in t.GetCustomAttributesData())
                if (cad.AttributeType.Name == "ContentPropertyAttribute" && cad.ConstructorArguments.Count == 1)
                    contentProp = cad.ConstructorArguments[0].Value as string;

            types[t.FullName] = new
            {
                assembly = asm.GetName().Name,
                @base = t.BaseType?.FullName,
                kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static" : t.IsAbstract ? "abstract" : "class",
                dps,
                readonlyDps = readOnlyDps,
                attached,
                props,
                settableProps = settable,
                events,
                routedEvents = routed,
                contentProperty = contentProp,
            };
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"skip {t.FullName}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

var doc = new
{
    generator = "build/DpDump",
    assemblies = targets.Select(a => a.GetName().Name).ToArray(),
    xmlns,
    types,
};
var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
{
    WriteIndented = false,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
});
// one type per line keeps diffs of the committed file readable
json = json.Replace("},\"", "},\n\"");
File.WriteAllText(outPath, json + "\n");
Console.Error.WriteLine($"wrote {types.Count} types ({failures} skipped) to {outPath}");
return 0;
