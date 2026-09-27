#!/usr/bin/env python3
"""Static XAML checker for WPF apps that are cross-compiled on Linux and cannot be run here.

WPF validates a lot of XAML only at run time (on Windows). This script uses the metadata
dumped from the WPF reference assemblies (build/wpf-dps.json, produced by build/DpDump) to
catch those failures before shipping.

Usage:  python3 build/xaml_check.py [app_dir] [--dps extra-types.json ...]
        (default app_dir: ../src/Magpie.App relative to this script)

Prints one line per problem:  [category] file:line: message
Exit code: 0 = clean, 1 = problems found, 2 = usage / setup error.

See build/README-checks.md for the list of checks.
"""
from __future__ import annotations

import json
import os
import re
import sys
import xml.parsers.expat
from dataclasses import dataclass, field

HERE = os.path.dirname(os.path.abspath(__file__))

NS_WPF = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
NS_X = "http://schemas.microsoft.com/winfx/2006/xaml"
NS_XML = "http://www.w3.org/XML/1998/namespace"
WPF_URIS = {
    NS_WPF,
    "http://schemas.microsoft.com/netfx/2007/xaml/presentation",
    "http://schemas.microsoft.com/netfx/2009/xaml/presentation",
}
TEMPLATE_TYPES = {"ControlTemplate", "DataTemplate", "HierarchicalDataTemplate", "ItemsPanelTemplate"}
# Plain CLR properties of Window that people keep trying to set from a Style (Remindly 1.0.0).
CLR_ONLY_WINDOW = {"WindowStartupLocation", "Owner", "DialogResult"}


# ─────────────────────────────── XML tree with line numbers ───────────────────────────────

@dataclass(eq=False)
class Node:
    uri: str
    local: str
    attrs: dict  # (uri, local) -> value ; uri "" for unqualified attributes
    line: int
    order: int
    nsmap: dict  # prefix -> uri in scope ("" = default)
    parent: "Node | None" = None
    children: list = field(default_factory=list)

    @property
    def is_wpf(self) -> bool:
        return self.uri in WPF_URIS

    @property
    def is_property_element(self) -> bool:
        return "." in self.local

    def attr(self, name: str, uri: str = "") -> str | None:
        return self.attrs.get((uri, name))

    def xattr(self, name: str) -> str | None:
        return self.attrs.get((NS_X, name))

    def ancestors(self):
        n = self.parent
        while n is not None:
            yield n
            n = n.parent

    def iter(self):
        yield self
        for c in self.children:
            yield from c.iter()


class ParseError(Exception):
    def __init__(self, msg, line):
        super().__init__(msg)
        self.line = line


def parse_xaml(path: str) -> Node:
    p = xml.parsers.expat.ParserCreate(namespace_separator="\x01")
    p.ordered_attributes = True
    stack: list[Node] = []
    ns_stack: list[dict] = [{"xml": NS_XML}]
    pending: dict = {}
    counter = [0]
    root: list[Node] = []

    def split(name):
        if "\x01" in name:
            u, l = name.split("\x01", 1)
            return u, l
        return "", name

    def start_ns(prefix, uri):
        pending[prefix or ""] = uri

    def start(name, attrs):
        nsmap = dict(ns_stack[-1])
        nsmap.update(pending)
        pending.clear()
        ns_stack.append(nsmap)
        u, l = split(name)
        ad = {}
        for i in range(0, len(attrs), 2):
            ad[split(attrs[i])] = attrs[i + 1]
        counter[0] += 1
        n = Node(u, l, ad, p.CurrentLineNumber, counter[0], nsmap)
        if stack:
            n.parent = stack[-1]
            stack[-1].children.append(n)
        else:
            root.append(n)
        stack.append(n)

    def end(name):
        stack.pop()
        ns_stack.pop()

    p.StartNamespaceDeclHandler = start_ns
    p.StartElementHandler = start
    p.EndElementHandler = end
    with open(path, "rb") as f:
        data = f.read()
    try:
        p.Parse(data, True)
    except xml.parsers.expat.ExpatError as e:
        raise ParseError(str(e), e.lineno)
    return root[0]


# ─────────────────────────────── WPF metadata ───────────────────────────────

class TypeInfo:
    __slots__ = ("full", "short", "base", "dps", "ro", "attached", "props", "settable", "events", "kind")

    def __init__(self, full, d):
        self.full = full
        self.short = full.rsplit(".", 1)[-1]
        self.base = d.get("base")
        self.dps = set(d.get("dps", ()))
        self.ro = set(d.get("readonlyDps", ()))
        self.attached = set(d.get("attached", ()))
        self.props = set(d.get("props", ()))
        self.settable = set(d.get("settableProps", ()))
        self.events = set(d.get("events", ())) | set(d.get("routedEvents", ()))
        self.kind = d.get("kind")


class Meta:
    def __init__(self, paths):
        self.types = {}
        self.xmlns = {}
        for path in ([paths] if isinstance(paths, str) else paths):
            with open(path, encoding="utf-8") as f:
                d = json.load(f)
            self.types.update({k: TypeInfo(k, v) for k, v in d["types"].items()})
            for k, v in d.get("xmlns", {}).items():
                self.xmlns.setdefault(k, [])
                self.xmlns[k] += [x for x in v if x not in self.xmlns[k]]

    def chain(self, t: TypeInfo):
        seen = 0
        while t is not None and seen < 64:
            yield t
            t = self.types.get(t.base) if t.base else None
            seen += 1

    def is_subclass(self, t: TypeInfo, base: TypeInfo) -> bool:
        return any(x.full == base.full for x in self.chain(t))

    def find_dp(self, t: TypeInfo, name: str):
        for x in self.chain(t):
            if name in x.dps:
                return x
        return None

    def has_clr_prop(self, t, name):
        return any(name in x.props for x in self.chain(t))

    def has_event(self, t, name):
        return any(name in x.events for x in self.chain(t))

    def lookup_in_uri(self, uri: str, name: str):
        """Returns TypeInfo, or None if unknown. Second value: True if the namespace is one we
        fully know (WPF), i.e. a miss is a genuine error."""
        if uri in self.xmlns:
            for ns in self.xmlns[uri]:
                for cand in (f"{ns}.{name}", f"{ns}.{name}Extension"):
                    if cand in self.types:
                        return self.types[cand], True
            return None, uri in WPF_URIS
        if uri.startswith("clr-namespace:"):
            ns = uri[len("clr-namespace:"):].split(";", 1)[0]
            for cand in (f"{ns}.{name}", f"{ns}.{name}Extension"):
                if cand in self.types:
                    return self.types[cand], True
            # A clr-namespace pointing into a WPF namespace is fully known too.
            known = any(ns in v for u, v in self.xmlns.items() if u in WPF_URIS)
            return None, known
        return None, False


# ─────────────────────────────── helpers ───────────────────────────────

MARKUP_RE = re.compile(r"^\s*\{\s*([\w:]+)\s*(.*)\}\s*$", re.S)
RES_REF_RE = re.compile(r"\{\s*(StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(\{[^{}]*\}|[^,}\s]+)")
TB_RE = re.compile(r"\{\s*TemplateBinding\s+(?:Property\s*=\s*)?([\w:.]+)")


def strip_quotes(s):
    s = s.strip()
    if len(s) >= 2 and s[0] == s[-1] and s[0] in "'\"":
        return s[1:-1]
    return s


def type_ref_name(v: str) -> str | None:
    """'{x:Type local:Foo}' / '{x:Type TypeName=Foo}' / 'local:Foo' -> 'local:Foo'."""
    if v is None:
        return None
    v = v.strip()
    m = re.match(r"^\{\s*(?:\w+:)?Type(?:Extension)?\s+(?:TypeName\s*=\s*)?([\w:.]+)\s*\}$", v)
    if m:
        return m.group(1)
    if v.startswith("{"):
        return None
    return v


def norm_key(raw: str, node: Node | None = None) -> str | None:
    """Normalise a resource key. Plain strings -> 's:Key'; {x:Type T} -> 't:T' (short name).
    Other markup keys ({x:Static ...}, ComponentResourceKey ...) -> None (not checked)."""
    raw = raw.strip()
    if raw.startswith("{"):
        t = type_ref_name(raw)
        if t is not None and raw.lstrip("{").lstrip().split()[0].split(":")[-1] in ("Type", "TypeExtension"):
            return "t:" + t.split(":")[-1]
        return None
    return "s:" + strip_quotes(raw)


def display_key(k: str) -> str:
    return k[2:] if k.startswith("s:") else "{x:Type %s}" % k[2:]


# ─────────────────────────────── the checker ───────────────────────────────

@dataclass
class ResDef:
    key: str
    file: str
    node: Node
    scope: Node  # element whose Resources hold this entry (or the root ResourceDictionary)
    order: int


class Checker:
    def __init__(self, app_dir: str, meta: Meta):
        self.app_dir = os.path.abspath(app_dir)
        self.meta = meta
        self.problems: list[tuple[str, str, int, str]] = []
        self.trees: dict[str, Node] = {}
        self.defs: dict[str, list[ResDef]] = {}  # file -> defs
        self.merged_sources: dict[str, list[tuple[Node, str | None]]] = {}  # file -> [(node, resolved path)]
        self.all_keys: set[str] = set()
        self.code_keys: set[str] = set()
        self.dict_ctx: dict[str, set] = {}
        self.app_keys: set[str] = set()
        self.opaque_files: set[str] = set()  # files that merge dictionaries we cannot resolve
        self.cs_cache: dict[str, str] = {}

    # ── reporting ──
    def report(self, cat, path, line, msg):
        self.problems.append((cat, os.path.relpath(path, self.app_dir), line, msg))

    # ── discovery ──
    def xaml_files(self):
        out = []
        for root, dirs, files in os.walk(self.app_dir):
            dirs[:] = [d for d in dirs if d not in ("bin", "obj", ".vs", "node_modules") and not d.startswith(".")]
            for f in files:
                if f.lower().endswith(".xaml"):
                    out.append(os.path.join(root, f))
        return sorted(out)

    # ── type resolution ──
    def resolve_type(self, name: str | None, node: Node, path: str, line: int | None = None, report=True):
        """Resolve 'prefix:Name' / 'Name' in node's namespace scope. Returns TypeInfo or None."""
        if not name:
            return None
        if ":" in name:
            prefix, local = name.split(":", 1)
        else:
            prefix, local = "", name
        uri = node.nsmap.get(prefix)
        if uri is None:
            if report:
                self.report("unknown-type", path, line or node.line, f"xmlns prefix '{prefix}' is not declared (type '{name}')")
            return None
        t, known = self.meta.lookup_in_uri(uri, local)
        if t is None and known and report:
            self.report("unknown-type", path, line or node.line, f"type '{name}' does not exist")
        return t

    def element_type(self, node: Node, path: str, report=False):
        if node.is_property_element:
            return None
        t, known = self.meta.lookup_in_uri(node.uri, node.local)
        if t is None and known and report:
            self.report("unknown-type", path, node.line, f"element <{node.local}> is not a known WPF type")
        return t

    def resolve_dp_ref(self, prop: str, target: TypeInfo | None, node: Node, path: str):
        """Resolve a property reference used by Setter/Trigger/TemplateBinding.
        Returns (status, owner_or_type) where status in:
          'ok', 'skip' (unknown custom type), 'clr' (CLR-only property), 'missing'."""
        prop = prop.strip()
        if "." in prop:
            owner_name, pname = prop.rsplit(".", 1)
            if owner_name.startswith("(") :
                owner_name = owner_name[1:]
            if pname.endswith(")"):
                pname = pname[:-1]
            owner = self.resolve_type(owner_name, node, path)
            if owner is None:
                return "skip", None
            found = self.meta.find_dp(owner, pname)
            if found:
                return "ok", found
            if self.meta.has_clr_prop(owner, pname):
                return "clr", owner
            return "missing", owner
        if target is None:
            return "skip", None
        found = self.meta.find_dp(target, prop)
        if found:
            return "ok", found
        if self.meta.has_clr_prop(target, prop):
            return "clr", target
        return "missing", target

    # ── template / style context ──
    def enclosing(self, node: Node, kinds):
        for a in node.ancestors():
            if a.is_wpf and not a.is_property_element and a.local in kinds:
                return a
        return None

    def template_target(self, tpl: Node, path: str):
        """TargetType of a ControlTemplate (explicit or inferred from context)."""
        if tpl.local != "ControlTemplate":
            if tpl.local in ("DataTemplate", "HierarchicalDataTemplate"):
                return self.meta.types.get("System.Windows.Controls.ContentPresenter")
            return None
        tt = type_ref_name(tpl.attr("TargetType"))
        if tt:
            return self.resolve_type(tt, tpl, path, report=False)
        p = tpl.parent
        if p is not None and p.is_property_element:
            owner, _, prop = p.local.rpartition(".")
            if prop == "Value" and owner == "Setter":
                style = self.enclosing(p, {"Style"})
                if style is not None:
                    st = type_ref_name(style.attr("TargetType"))
                    if st:
                        return self.resolve_type(st, style, path, report=False)
            elif p.uri in WPF_URIS:
                return self.meta.lookup_in_uri(p.uri, owner)[0]
        return self.meta.types.get("System.Windows.Controls.Control")

    def names_in(self, scope_root: Node):
        """x:Name / Name -> element inside a template's namescope (not descending into nested
        templates)."""
        names = {}

        def walk(n: Node, top: bool):
            if not top and n.is_wpf and not n.is_property_element and n.local in TEMPLATE_TYPES:
                return
            if not top:
                nm = n.xattr("Name") or (n.attr("Name") if not n.is_property_element else None)
                if nm:
                    names.setdefault(nm, n)
            for c in n.children:
                walk(c, False)

        walk(scope_root, True)
        return names

    # ── check a (+f): Setters / Triggers / Conditions ──
    def check_setters(self, path, root: Node):
        for n in root.iter():
            if not n.is_wpf or n.is_property_element:
                continue
            if n.local in ("Setter", "Trigger", "Condition"):
                self.check_one_setter(path, n)

    def owner_context(self, n: Node):
        """Nearest Style or FrameworkTemplate that owns this Setter/Trigger."""
        for a in n.ancestors():
            if a.is_wpf and not a.is_property_element and (a.local == "Style" or a.local in TEMPLATE_TYPES):
                return a
        return None

    def check_one_setter(self, path, n: Node):
        prop = n.attr("Property")
        if n.local == "Condition" and prop is None:
            return  # MultiDataTrigger condition (Binding=...)
        if n.local == "Setter" and prop is None:
            if n.attr("TargetName") is None and not any(c.local == "Setter.Property" for c in n.children):
                self.report("setter-property", path, n.line, "Setter has no Property")
            return
        if prop is None or prop.strip().startswith("{"):
            return
        pname_only = prop.rsplit(".", 1)[-1]
        if n.local == "Setter" and pname_only in CLR_ONLY_WINDOW:
            self.report("setter-clr-only", path, n.line,
                        f"Setter Property=\"{prop}\": {pname_only} is a plain CLR property on Window, not a "
                        f"DependencyProperty — WPF throws when the style is applied")
            return

        owner = self.owner_context(n)
        is_setter = n.local == "Setter"
        target_name = n.attr("TargetName") if is_setter else n.attr("SourceName")
        target = None
        ctx_desc = ""
        if owner is None:
            return
        if owner.local == "Style":
            if is_setter and target_name:
                self.report("setter-targetname", path, n.line,
                            f"Setter TargetName=\"{target_name}\" is not allowed inside a Style (only in templates)")
                return
            if target_name and not is_setter:
                # SourceName in a Style trigger is not allowed either
                self.report("trigger-property", path, n.line,
                            f"{n.local} SourceName=\"{target_name}\" is not allowed inside a Style")
                return
            tt = type_ref_name(owner.attr("TargetType"))
            if tt:
                target = self.resolve_type(tt, owner, path, report=False)
                if target is None:
                    return  # unknown custom type
                ctx_desc = f"Style TargetType {target.short}"
            else:
                if "." not in prop:
                    self.report("setter-property" if is_setter else "trigger-property", path, n.line,
                                f"{n.local} Property=\"{prop}\" in a Style without TargetType must be qualified (Type.{prop})")
                    return
        else:  # template
            if target_name:
                names = self.names_in(owner)
                el = names.get(target_name)
                if el is None:
                    self.report("setter-targetname", path, n.line,
                                f"{n.local} {'TargetName' if is_setter else 'SourceName'}=\"{target_name}\" "
                                f"does not name an element in this {owner.local}")
                    return
                target = self.element_type(el, path)
                if target is None:
                    return
                ctx_desc = f"element '{target_name}' ({target.short})"
            else:
                target = self.template_target(owner, path)
                if target is None:
                    return
                ctx_desc = f"{owner.local} TargetType {target.short}"

        status, where = self.resolve_dp_ref(prop, target, n, path)
        cat = "setter-property" if is_setter else "trigger-property"
        if status == "clr":
            self.report(cat, path, n.line,
                        f"{n.local} Property=\"{prop}\": '{pname_only}' is a CLR property of {where.short}, not a "
                        f"DependencyProperty (run-time crash: Set property 'System.Windows.Setter.Property' threw)")
        elif status == "missing":
            self.report(cat, path, n.line,
                        f"{n.local} Property=\"{prop}\": no DependencyProperty '{pname_only}' on "
                        f"{where.short if where else '?'}{' (' + ctx_desc + ')' if ctx_desc and '.' not in prop else ''}")
        elif status == "ok" and is_setter and pname_only in where.ro and pname_only not in where.attached:
            self.report("setter-readonly", path, n.line,
                        f"Setter Property=\"{prop}\": {where.short}.{pname_only} is a read-only DependencyProperty")

    # ── type references: TargetType / {x:Type} must exist (typos crash at load time) ──
    def check_type_refs(self, path, root: Node):
        xt = re.compile(r"\{\s*(\w+):Type(?:Extension)?\s+(?:TypeName\s*=\s*)?([\w:.]+)\s*\}")
        for n in root.iter():
            for (u, a), v in n.attrs.items():
                if u == "" and a == "TargetType" and not v.strip().startswith("{"):
                    self.resolve_type(v.strip(), n, path)
                    continue
                if ":Type" not in v:
                    continue
                for m in xt.finditer(v):
                    if n.nsmap.get(m.group(1)) == NS_X:
                        self.resolve_type(m.group(2), n, path)

    # ── check b: TemplateBinding ──
    def check_template_bindings(self, path, root: Node):
        for n in root.iter():
            for (u, a), v in n.attrs.items():
                if "TemplateBinding" not in v:
                    continue
                for m in TB_RE.finditer(v):
                    tpl = self.enclosing(n, {"ControlTemplate"} | TEMPLATE_TYPES) if not (
                        n.is_wpf and n.local in TEMPLATE_TYPES) else self.enclosing(n, TEMPLATE_TYPES)
                    if tpl is None:
                        self.report("template-binding", path, n.line,
                                    f"{{TemplateBinding {m.group(1)}}} used outside a template")
                        continue
                    if tpl.local != "ControlTemplate":
                        continue
                    target = self.template_target(tpl, path)
                    if target is None:
                        continue
                    status, where = self.resolve_dp_ref(m.group(1), target, n, path)
                    if status == "clr":
                        self.report("template-binding", path, n.line,
                                    f"{{TemplateBinding {m.group(1)}}}: CLR property on {where.short}, TemplateBinding needs a DependencyProperty")
                    elif status == "missing":
                        self.report("template-binding", path, n.line,
                                    f"{{TemplateBinding {m.group(1)}}}: no DependencyProperty '{m.group(1)}' on "
                                    f"ControlTemplate TargetType {where.short if where else '?'}")

    # ── resources: collection ──
    def resolve_source(self, src: str, from_file: str):
        s = src.strip()
        if s.startswith("{"):
            return None
        m = re.match(r"^pack://application:,,,(/.*)$", s, re.I)
        if m:
            s = m.group(1)
        m = re.match(r"^/([^;/]+);component/(.*)$", s, re.I)
        if m:
            asm = m.group(1)
            if asm.lower() != os.path.basename(self.app_dir).lower() and not self._asm_matches(asm):
                return None
            rel = m.group(2)
            return os.path.normpath(os.path.join(self.app_dir, rel))
        if s.startswith("/"):
            return os.path.normpath(os.path.join(self.app_dir, s.lstrip("/")))
        if "://" in s:
            return None
        return os.path.normpath(os.path.join(os.path.dirname(from_file), s))

    def _asm_matches(self, asm):
        for f in os.listdir(self.app_dir):
            if f.endswith(".csproj"):
                txt = open(os.path.join(self.app_dir, f), encoding="utf-8", errors="replace").read()
                m = re.search(r"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>", txt)
                name = m.group(1) if m else f[:-7]
                if name.lower() == asm.lower():
                    return True
        return False

    def is_resource_container(self, n: Node):
        """n holds resource entries as children -> returns scope node, else None."""
        if n.is_wpf and not n.is_property_element and n.local == "ResourceDictionary":
            p = n.parent
            if p is None:
                return n
            if p.is_property_element and p.local.endswith(".Resources"):
                return p.parent
            if p.is_property_element and p.local == "ResourceDictionary.MergedDictionaries":
                # inline merged dictionary: entries count for the dictionary that merges it
                d = p.parent
                return self.is_resource_container(d) if d is not None else n
            return n
        if n.is_property_element and n.local.endswith(".Resources"):
            return n.parent
        return None

    def collect_resources(self, path, root: Node):
        defs = []
        merged = []
        for n in root.iter():
            scope = self.is_resource_container(n)
            if scope is None:
                continue
            for c in n.children:
                if c.is_property_element:
                    if c.local == "ResourceDictionary.MergedDictionaries":
                        for md in c.children:
                            src = md.attr("Source")
                            if src is not None:
                                merged.append((md, self.resolve_source(src, path), scope))
                    continue
                if c.is_wpf and c.local == "ResourceDictionary" and n.is_property_element:
                    continue  # handled as container itself
                key = c.xattr("Key")
                k = None
                if key is not None:
                    k = norm_key(key, c)
                elif c.is_wpf and c.local == "Style":
                    tt = type_ref_name(c.attr("TargetType"))
                    if tt:
                        k = "t:" + tt.split(":")[-1]
                if k:
                    defs.append(ResDef(k, path, c, scope, c.order))
        self.defs[path] = defs
        self.merged_sources[path] = merged
        for d in defs:
            self.all_keys.add(d.key)

    def file_keys_recursive(self, path, seen=None):
        seen = seen if seen is not None else set()
        if path in seen or path not in self.trees:
            return set()
        seen.add(path)
        keys = set()
        for _, src, _ in self.merged_sources.get(path, []):
            if src:
                keys |= self.file_keys_recursive(src, seen)
        keys |= {d.key for d in self.defs.get(path, [])}
        return keys

    def build_app_context(self):
        """Model App.xaml: merged dictionaries load in order, each after its own merged dicts."""
        app = None
        for p, t in self.trees.items():
            if t.local == "Application" and t.is_wpf:
                app = p
                break
        self.app_file = app
        if app is None:
            return
        current: set = set()
        visiting = set()

        def load(dict_path):
            nonlocal current
            if dict_path in visiting or dict_path not in self.trees:
                return
            visiting.add(dict_path)
            self.dict_ctx.setdefault(dict_path, set(current))
            for _, src, _ in self.merged_sources.get(dict_path, []):
                if src:
                    load(src)
                    current |= self.file_keys_recursive(src)
            current |= {d.key for d in self.defs.get(dict_path, [])}
            visiting.discard(dict_path)

        for md, src, _ in self.merged_sources.get(app, []):
            if src:
                load(src)
        self.app_keys = self.file_keys_recursive(app)

    def collect_code_keys(self):
        """Resource keys added from code (Resources["X"] = ...) — accepted for DynamicResource."""
        rx = re.compile(r"Resources\s*(?:\[\s*\"([^\"]+)\"\s*\]\s*=|\.Add\(\s*\"([^\"]+)\")")
        for root, dirs, files in os.walk(self.app_dir):
            dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
            for f in files:
                if f.endswith(".cs"):
                    txt = self.read_cs(os.path.join(root, f))
                    for m in rx.finditer(txt):
                        self.code_keys.add("s:" + (m.group(1) or m.group(2)))

    # ── check d: StaticResource / DynamicResource ──
    def resource_refs(self, root: Node):
        for n in root.iter():
            if n.is_wpf and n.local in ("StaticResource", "StaticResourceExtension", "DynamicResource",
                                        "DynamicResourceExtension") and not n.is_property_element:
                k = n.attr("ResourceKey")
                if k is not None:
                    yield n, "StaticResource" if n.local.startswith("Static") else "DynamicResource", k, None
            for (u, a), v in n.attrs.items():
                if u == NS_X and a == "Key":
                    continue
                if "Resource" not in v or v.startswith("{}"):
                    continue
                for m in RES_REF_RE.finditer(v):
                    yield n, m.group(1), m.group(2), a

    def in_template(self, n: Node):
        return self.enclosing(n, TEMPLATE_TYPES) is not None

    def check_resources(self, path, root: Node):
        defs = self.defs.get(path, [])
        is_dict_file = root.is_wpf and root.local == "ResourceDictionary"
        if path == getattr(self, "app_file", None):
            external = set()
        elif is_dict_file:
            external = self.dict_ctx.get(path, self.app_keys)
        else:
            external = self.app_keys
        # keys from dictionaries merged into scopes in this file
        merged_by_scope: dict[int, set] = {}
        opaque_scopes = set()
        for md, src, scope in self.merged_sources.get(path, []):
            if src and src in self.trees:
                merged_by_scope.setdefault(id(scope), set()).update(self.file_keys_recursive(src))
            else:
                opaque_scopes.add(id(scope))
                if src and not os.path.exists(src):
                    self.report("static-resource", path, md.line, f"merged dictionary Source=\"{md.attr('Source')}\" not found")

        for n, kind, raw, attr in self.resource_refs(root):
            key = norm_key(raw, n)
            if key is None:
                continue
            if key.startswith("t:"):
                continue  # {x:Type T} keys also resolve to system theme default styles
            chain = [n] + list(n.ancestors())
            chain_ids = {id(a) for a in chain}
            if any(i in opaque_scopes for i in chain_ids):
                continue
            everywhere = key in self.all_keys or key in self.code_keys
            if kind == "DynamicResource":
                if not everywhere:
                    self.report("dynamic-resource", path, n.line, f"{{DynamicResource {display_key(key)}}}: key is not defined anywhere")
                continue
            # StaticResource
            late = self.in_template(n)
            # defs whose scope encloses the reference, excluding the resource that contains it
            in_scope = [d for d in defs if id(d.scope) in chain_ids and id(d.node) not in chain_ids]
            self_defs = [d for d in defs if d.key == key and id(d.node) in chain_ids]
            ok = key in external or any(key in merged_by_scope.get(i, ()) for i in chain_ids)
            if not ok:
                before = [d for d in in_scope if d.key == key and d.order < n.order]
                ok = bool(before)
            if ok:
                continue
            after = [d for d in in_scope if d.key == key and d.order > n.order]
            if late and (after or everywhere):
                continue  # template content is resolved when the template is instantiated
            if self_defs:
                self.report("static-resource", path, n.line,
                            f"{{StaticResource {display_key(key)}}} refers to the resource that contains it")
            elif after:
                self.report("forward-reference", path, n.line,
                            f"{{StaticResource {display_key(key)}}} is used before its x:Key definition at line "
                            f"{after[0].node.line} (StaticResource resolves in document order)")
            elif everywhere:
                where = sorted({os.path.relpath(d.file, self.app_dir) for ds in self.defs.values() for d in ds if d.key == key})
                self.report("static-resource", path, n.line,
                            f"{{StaticResource {display_key(key)}}} is not visible here (defined in {', '.join(where)}, "
                            f"which is not merged before this point)")
            else:
                self.report("static-resource", path, n.line, f"{{StaticResource {display_key(key)}}}: key is not defined")

    # ── check c: BasedOn ──
    def find_style_def(self, key, path, node: Node):
        cands = [d for d in self.defs.get(path, []) if d.key == key]
        if not cands:
            cands = [d for ds in self.defs.values() for d in ds if d.key == key]
        cands = [d for d in cands if d.node.is_wpf and d.node.local == "Style"]
        return cands[0] if cands else None

    def check_based_on(self, path, root: Node):
        for n in root.iter():
            if not (n.is_wpf and n.local == "Style" and not n.is_property_element):
                continue
            b = n.attr("BasedOn")
            if not b:
                continue
            m = RES_REF_RE.search(b)
            if not m:
                continue
            key = norm_key(m.group(2), n)
            if key is None:
                continue
            tt = type_ref_name(n.attr("TargetType"))
            this_t = self.resolve_type(tt, n, path, report=False) if tt else None
            if key.startswith("t:"):
                bt_name = type_ref_name(m.group(2))
                base_t = self.resolve_type(bt_name, n, path, report=False)
                base_desc = bt_name
            else:
                d = self.find_style_def(key, path, n)
                if d is None:
                    continue  # reported by the resource check
                btt = type_ref_name(d.node.attr("TargetType"))
                base_t = self.resolve_type(btt, d.node, d.file, report=False) if btt else None
                base_desc = f"'{display_key(key)}' (TargetType {btt})"
                if btt is None:
                    continue
            if this_t is None or base_t is None:
                if tt is None and base_t is not None:
                    self.report("based-on", path, n.line,
                                f"Style without TargetType is BasedOn {base_desc}; it needs TargetType {base_t.short} or a subclass")
                continue
            if not self.meta.is_subclass(this_t, base_t):
                self.report("based-on", path, n.line,
                            f"Style TargetType {this_t.short} cannot be BasedOn {base_desc}: "
                            f"{base_t.short} is not {this_t.short} or one of its base types")

    # ── check e: theme parity ──
    def check_theme_parity(self):
        light = os.path.join(self.app_dir, "Themes", "Light.xaml")
        dark = os.path.join(self.app_dir, "Themes", "Dark.xaml")
        if light not in self.trees or dark not in self.trees:
            return
        lk = {d.key: d for d in self.defs.get(light, []) if d.scope is self.trees[light]}
        dk = {d.key: d for d in self.defs.get(dark, []) if d.scope is self.trees[dark]}
        for k in sorted(set(lk) - set(dk)):
            self.report("theme-parity", light, lk[k].node.line, f"key '{display_key(k)}' is missing from Themes/Dark.xaml")
        for k in sorted(set(dk) - set(lk)):
            self.report("theme-parity", dark, dk[k].node.line, f"key '{display_key(k)}' is missing from Themes/Light.xaml")

    # ── check g + h: attributes and event handlers ──
    def read_cs(self, p):
        if p not in self.cs_cache:
            try:
                with open(p, encoding="utf-8-sig", errors="replace") as f:
                    self.cs_cache[p] = f.read()
            except OSError:
                self.cs_cache[p] = ""
        return self.cs_cache[p]

    def code_behind_methods(self, path, cls):
        texts = []
        cb = path + ".cs"
        if os.path.exists(cb):
            texts.append(self.read_cs(cb))
        short = cls.rsplit(".", 1)[-1]
        rx_cls = re.compile(r"\bpartial\s+(?:class|record)\s+" + re.escape(short) + r"\b")
        for root, dirs, files in os.walk(self.app_dir):
            dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
            for f in files:
                fp = os.path.join(root, f)
                if f.endswith(".cs") and fp != cb and rx_cls.search(self.read_cs(fp)):
                    texts.append(self.read_cs(fp))
        methods = set()
        rx = re.compile(r"[\w>\]?)]\s+(\w+)\s*(?:<[^>()]*>)?\s*\([^;{}()]*(?:\([^()]*\)[^;{}()]*)*\)\s*(?:\{|=>|where\b)")
        for t in texts:
            methods.update(m.group(1) for m in rx.finditer(t))
        return methods, bool(texts)

    def check_attributes(self, path, root: Node):
        cls = root.xattr("Class")
        methods, have_cb = (self.code_behind_methods(path, cls) if cls else (set(), False))
        for n in root.iter():
            if not n.is_wpf or n.is_property_element:
                continue
            t = self.element_type(n, path, report=True)
            if t is None:
                continue
            for (u, a), v in n.attrs.items():
                if u:  # x:, d:, mc:, xml:, custom-namespace attributes
                    continue
                if "." in a:
                    self.check_attached_attr(path, n, a)
                    continue
                is_event = self.meta.has_event(t, a)
                is_prop = self.meta.find_dp(t, a) is not None or self.meta.has_clr_prop(t, a)
                if not is_event and not is_prop:
                    self.report("unknown-attribute", path, n.line,
                                f"<{n.local} {a}=...>: '{a}' is not a property or event of {t.short}")
                    continue
                if is_event and not is_prop:
                    self.check_handler(path, n, a, v, cls, methods, have_cb)
            if n.local == "EventSetter":
                h = n.attr("Handler")
                if h:
                    self.check_handler(path, n, "Handler", h, cls, methods, have_cb)

    def check_attached_attr(self, path, n: Node, a: str):
        """Owner.Prop="..." (attached property / attached event / qualified own property)."""
        owner_name, pname = a.rsplit(".", 1)
        owner = self.resolve_type(owner_name, n, path)
        if owner is None:
            return
        if self.meta.find_dp(owner, pname) or self.meta.has_event(owner, pname) or self.meta.has_clr_prop(owner, pname):
            return
        self.report("unknown-attribute", path, n.line,
                    f"<{n.local} {a}=...>: {owner.short} has no attachable property or event '{pname}'")

    def check_handler(self, path, n, attr, value, cls, methods, have_cb):
        v = value.strip()
        if v.startswith("{"):
            return
        if not cls:
            self.report("event-handler", path, n.line,
                        f"{attr}=\"{v}\": event handlers need an x:Class with code-behind")
        elif not have_cb:
            self.report("event-handler", path, n.line,
                        f"{attr}=\"{v}\": no code-behind found for {cls}")
        elif v not in methods:
            self.report("event-handler", path, n.line,
                        f"{attr}=\"{v}\": no method '{v}' in code-behind of {cls}")

    # ── driver ──
    def run(self):
        files = self.xaml_files()
        for p in files:
            try:
                self.trees[p] = parse_xaml(p)
            except ParseError as e:
                self.report("xml-parse", p, e.line or 0, str(e))
            except OSError as e:
                self.report("xml-parse", p, 0, str(e))
        for p, t in self.trees.items():
            self.collect_resources(p, t)
        self.build_app_context()
        self.collect_code_keys()
        for p, t in self.trees.items():
            self.check_type_refs(p, t)
            self.check_setters(p, t)
            self.check_template_bindings(p, t)
            self.check_based_on(p, t)
            self.check_resources(p, t)
            self.check_attributes(p, t)
        self.check_theme_parity()
        # de-duplicate, stable order
        seen = set()
        out = []
        for pr in sorted(self.problems, key=lambda x: (x[1], x[2], x[0], x[3])):
            if pr not in seen:
                seen.add(pr)
                out.append(pr)
        return out, len(files)


def main(argv):
    args = [a for a in argv[1:]]
    dps = [os.path.join(HERE, "wpf-dps.json")]
    extra = []
    while "--dps" in args:  # additional metadata files, e.g. the app's own assembly dumped by DpDump
        i = args.index("--dps")
        extra.append(args[i + 1])
        del args[i:i + 2]
    dps += extra
    if any(a in ("-h", "--help") for a in args):
        print(__doc__)
        return 0
    app_dir = args[0] if args else os.path.normpath(os.path.join(HERE, "..", "src", "Magpie.App"))
    if not os.path.isdir(app_dir):
        print(f"xaml_check: app directory not found: {app_dir}", file=sys.stderr)
        return 2
    for f in dps:
        if not os.path.exists(f):
            print(f"xaml_check: metadata not found: {f} (run: dotnet run --project build/DpDump -- {f})", file=sys.stderr)
            return 2
    checker = Checker(app_dir, Meta(dps))
    problems, nfiles = checker.run()
    for cat, f, line, msg in problems:
        print(f"[{cat}] {f}:{line}: {msg}")
    print(f"xaml_check: {nfiles} XAML file(s) checked, {len(problems)} problem(s)", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
