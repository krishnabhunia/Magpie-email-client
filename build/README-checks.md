# Static XAML checks

WPF validates much of its XAML only at run time, on Windows. We build on Linux and can't run
the app, so `build/xaml_check.py` catches those failures before we ship. Remindly 1.0.0 is the
example: a shared Style had `<Setter Property="WindowStartupLocation">`, and every dialog crashed.

```
python3 build/xaml_check.py                    # checks ../src/Magpie.App
python3 build/xaml_check.py path/to/App.Project
python3 build/xaml_check.py --dps build/app-types.json   # also know the app's own controls
```

The checker writes one line per problem, in the form `[category] file:line: message`. It exits
with 0 when the XAML is clean, 1 when it finds problems, and 2 when it's used wrongly. It needs
Python 3 only (stdlib expat), so no lxml.

## What is checked

| Category | Check |
|---|---|
| `setter-clr-only` | A Setter targets `WindowStartupLocation`, `Owner` or `DialogResult`. These are CLR-only properties of Window, so the Setter is always an error. |
| `setter-property` / `trigger-property` | `Setter`, `Trigger` and `Condition` `Property=` must resolve to a **DependencyProperty** on the effective target type. The target type comes from one of these: Style `TargetType`, ControlTemplate `TargetType` (inferred when omitted), the element named by `TargetName`/`SourceName` inside the template, or the qualified form `Type.Prop` / `prefix:Type.Prop`. The checker walks the base-type chain, and handles attached DPs (`Grid.Row`, `TextElement.Foreground`, `shell:WindowChrome.IsHitTestVisibleInChrome`). A CLR-only property gets its own message. An unqualified property in a Style with no TargetType is also flagged. |
| `setter-readonly` | A Setter targets a read-only DP (`IsMouseOver`, `ActualWidth`, …). Triggers on read-only DPs are still fine. |
| `setter-targetname` | Flags `TargetName` on a Style Setter, which WPF doesn't allow. Also flags a TargetName or SourceName that doesn't name an element in the template's namescope. |
| `template-binding` | `{TemplateBinding X}` must be a DP on the ControlTemplate's TargetType. |
| `based-on` | A `BasedOn` style's TargetType must be the same type as this style's TargetType, or a base type of it. |
| `static-resource` / `forward-reference` | Every `{StaticResource K}` and `<StaticResource ResourceKey=…/>` must be defined and visible, in document order. The checker models App.xaml merge order: merged dictionaries load in order, and each one loads its own MergedDictionaries before its own keys. Views can see every key that App loads. A key used before its `x:Key` in the same file is flagged as a forward reference, unless a dictionary merged earlier also defines it. References inside template content are resolved late, so they only need to exist. `{x:Type T}` keys always resolve, because theme default styles back them. |
| `dynamic-resource` | A `{DynamicResource K}` key must exist in some XAML file, or be set from code (`Resources["K"] = …`). |
| `theme-parity` | `Themes/Light.xaml` and `Themes/Dark.xaml` must define the same set of `x:Key`s. |
| `event-handler` | An event attribute (`Click="OnX"`) or `EventSetter Handler` needs a method of that name. The checker looks in the `.xaml.cs` file and in any other `partial class` file for the `x:Class`. |
| `unknown-attribute` | Every plain attribute on a WPF element must be a DP, CLR property or event of that type (base chain included). `Owner.Prop` attributes must be an attached DP or event of `Owner`. This catches typos like `Foregroud=`. |
| `unknown-type` | Flags unknown WPF elements, `TargetType`s and `{x:Type}`s, such as `TargetType="TextBlok"`. |
| `xml-parse` | The XAML file is not well-formed XML. |

Custom types (`local:`, other `clr-namespace:`) are skipped unless their metadata is loaded. To
load it, dump the app assembly and pass it with `--dps`, as shown below.

## Regenerating `wpf-dps.json`

`build/wpf-dps.json` is generated and committed. It holds the metadata for every public type in
WindowsBase, PresentationCore, PresentationFramework, System.Xaml, Ribbon and
WindowsFormsIntegration. For each type it records:

- the base type
- the DP names (the static `…Property` fields, with the suffix removed), with attached and read-only DPs marked
- the CLR instance properties
- the CLR and routed events
- the XAML xmlns→CLR namespace map

`build/DpDump` reads these from the reference assemblies with `MetadataLoadContext`, so it runs on
Linux. Regenerate the file after you change the targeting pack version:

```
export PATH=/opt/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet run --project build/DpDump -- build/wpf-dps.json
# optional: include the app's own controls (build the app first), then pass it to the checker
dotnet run --project build/DpDump -- build/app-types.json src/Magpie.App/bin/Release/net8.0-windows/Magpie.App.dll
python3 build/xaml_check.py --dps build/app-types.json
```

By default the dump uses the newest `microsoft.windowsdesktop.app.ref` package in the NuGet cache
and the newest `Microsoft.NETCore.App.Ref` pack under the dotnet root. To pick other folders, pass
`--wpf-ref <dir>` and `--netcore-ref <dir>`.

Reference assemblies strip internal fields, so a DP counts as read-only when its CLR wrapper has no
public setter and it has no static `SetX` method.
