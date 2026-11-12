#!/usr/bin/env python3
"""
Generate a synthetic codebase with a known answer key, to test an extractor against.

One seeded, language-neutral model (modules, classes, interfaces, inheritance, overrides, overloads, generics,
nested types, statics, lambdas, tests, an entry point, planted dead code, Angular components and services) is
written out as C#, Java, TypeScript, JavaScript or Angular, in one of several constructions (a file per class,
a file per module, one file, several projects). expected.json lists every method, type and edge the graph
should hold, named the way that language's extractor names them.

    python3 gen.py --lang java --profile classic --out /tmp/corpus --seed 7

Standard library only, so it runs in any container with Python 3.10 or later.
"""
from __future__ import annotations

import argparse
import json
import os
import random
import re
from dataclasses import dataclass, field

LANGS = ["csharp", "java", "typescript", "javascript", "angular"]

# Constructions a language cannot express: JavaScript has no namespaces to put several modules in one file, Java
# allows one package per file, and Angular decorators make a file a module (no global namespaces).
UNSUPPORTED = {("javascript", "single-file"), ("java", "single-file"), ("angular", "single-file")}

# Size and shape of each profile. Probabilities are per class or per method; counts are per module.
PROFILES: dict[str, dict] = {
    "tiny": dict(modules=1, classes=3, methods=2, ifaces=0, inherit=0.0, depth=1, fanout=1, overload=0.0, static=0.0,
                 generic=0.0, nested=0.0, lambda_=0.0, tests=1, dead=1.0, override=0.0, super_=0.0, iface_inherit=0.0),
    "classic": dict(modules=3, classes=6, methods=3, ifaces=2, inherit=0.5, depth=3, fanout=3, overload=0.3, static=0.2,
                    generic=0.15, nested=0.15, lambda_=0.2, tests=3, dead=0.3, override=0.5, super_=0.5, iface_inherit=0.3),
    "deep-hierarchy": dict(modules=2, classes=12, methods=3, ifaces=1, inherit=1.0, depth=24, fanout=2, overload=0.1,
                           static=0.0, generic=0.0, nested=0.0, lambda_=0.0, tests=2, dead=0.2, override=0.8, super_=0.7,
                           iface_inherit=0.0, chain=True),
    "wide-fanout": dict(modules=2, classes=30, methods=2, ifaces=1, inherit=0.1, depth=2, fanout=1, overload=0.0,
                        static=0.1, generic=0.0, nested=0.0, lambda_=0.0, tests=2, dead=0.1, override=0.3, super_=0.3,
                        iface_inherit=0.0, hub=True),
    "generics-nested": dict(modules=2, classes=10, methods=3, ifaces=2, inherit=0.3, depth=2, fanout=3, overload=0.3,
                            static=0.2, generic=0.6, nested=0.5, lambda_=0.6, tests=2, dead=0.3, override=0.5, super_=0.5,
                            iface_inherit=0.5),
    "collisions": dict(modules=4, classes=5, methods=3, ifaces=1, inherit=0.4, depth=2, fanout=3, overload=0.5,
                       static=0.2, generic=0.0, nested=0.2, lambda_=0.2, tests=2, dead=0.3, override=0.5, super_=0.5,
                       iface_inherit=0.0, collide=True),
    "single-file": dict(modules=3, classes=6, methods=3, ifaces=2, inherit=0.5, depth=3, fanout=3, overload=0.3,
                        static=0.2, generic=0.15, nested=0.15, lambda_=0.2, tests=2, dead=0.3, override=0.5, super_=0.5,
                        iface_inherit=0.3, layout="single"),
    "per-module": dict(modules=3, classes=6, methods=3, ifaces=2, inherit=0.5, depth=3, fanout=3, overload=0.3,
                       static=0.2, generic=0.15, nested=0.15, lambda_=0.2, tests=2, dead=0.3, override=0.5, super_=0.5,
                       iface_inherit=0.3, layout="module"),
    "multi-project": dict(modules=4, classes=5, methods=3, ifaces=2, inherit=0.5, depth=3, fanout=3, overload=0.3,
                          static=0.2, generic=0.1, nested=0.1, lambda_=0.2, tests=2, dead=0.3, override=0.5, super_=0.5,
                          iface_inherit=0.3, multi=True),
    "large": dict(modules=20, classes=25, methods=5, ifaces=4, inherit=0.5, depth=4, fanout=4, overload=0.2, static=0.2,
                  generic=0.1, nested=0.1, lambda_=0.2, tests=5, dead=0.2, override=0.5, super_=0.5, iface_inherit=0.3),
}

WORDS = ["Ledger", "Order", "Invoice", "Parcel", "Router", "Cache", "Shelf", "Quote", "Basket", "Meter", "Tariff",
         "Voucher", "Batch", "Signal", "Tracker", "Folio", "Gauge", "Rack", "Permit", "Ticket", "Bundle", "Crate",
         "Docket", "Ember", "Ferry", "Grove", "Harbor", "Ingot", "Jetty", "Kiosk"]
VERBS = ["load", "save", "apply", "compute", "merge", "split", "resolve", "emit", "scan", "price"]
IFACE_WORDS = ["Shape", "Store", "Clock", "Codec", "Probe", "Sink", "Gate", "Feed"]


@dataclass(eq=False)
class Method:
    name: str
    arity: int
    owner: "Type"
    static: bool = False
    private: bool = False
    abstract: bool = False
    ctor: bool = False
    test: bool = False
    entry: bool = False
    dead: bool = False
    lifecycle: bool = False
    overrides: "Method | None" = None
    implements: list["Method"] = field(default_factory=list)
    calls: list[tuple] = field(default_factory=list)  # (kind, target Method, extra)

    @property
    def generic_param(self) -> bool:
        return self.name == "put" and self.owner.generic


@dataclass(eq=False)
class Type:
    name: str
    module: "Module"
    kind: str = "class"  # class | interface
    base: "Type | None" = None
    ifaces: list["Type"] = field(default_factory=list)
    methods: list[Method] = field(default_factory=list)
    abstract: bool = False
    generic: bool = False
    outer: "Type | None" = None
    nested: list["Type"] = field(default_factory=list)
    test: bool = False
    program: bool = False
    role: str | None = None  # component | service (Angular)
    selector: str | None = None
    renders: list["Type"] = field(default_factory=list)
    template_calls: list[Method] = field(default_factory=list)
    injects: list[tuple["Type", str]] = field(default_factory=list)  # (service, "ctor" | "inject")
    seq: int = 0

    @property
    def path(self) -> list["Type"]:
        return (self.outer.path if self.outer else []) + [self]

    def ancestors(self):
        t = self.base
        while t:
            yield t
            t = t.base

    def all_ifaces(self) -> set["Type"]:
        out = set()
        def add(i):
            if i not in out:
                out.add(i)
                for parent in i.ifaces:
                    add(parent)
        for t in [self, *self.ancestors()]:
            for i in t.ifaces:
                add(i)
        return out

    def ctor(self) -> Method:
        return next(m for m in self.methods if m.ctor)


def closure(i: "Type") -> set["Type"]:
    out = {i}
    for parent in i.ifaces:
        out |= closure(parent)
    return out


@dataclass(eq=False)
class Module:
    name: str
    index: int
    types: list[Type] = field(default_factory=list)


class Model:
    def __init__(self, lang: str, profile: str, seed: int):
        self.lang, self.profile, self.p = lang, profile, dict(PROFILES[profile])
        self.rng = random.Random(f"{lang}:{profile}:{seed}")
        self.modules: list[Module] = []
        self.seq = 0
        if lang == "javascript":  # no interfaces, generics or abstract members in plain JavaScript
            self.p.update(ifaces=0, generic=0.0, iface_inherit=0.0)
        if lang in ("typescript", "javascript", "angular"):  # one implementation per name: no overloading by arity
            self.p.update(overload=0.0)
        self.build()

    # ---- construction -------------------------------------------------------------------------------------------
    def build(self):
        p, rng = self.p, self.rng
        for i in range(p["modules"]):
            module = Module(f"m{i}", i)
            self.modules.append(module)
            for k in range(p["ifaces"]):
                iface = Type(("I" + IFACE_WORDS[k % len(IFACE_WORDS)] + ("" if p.get("collide") else str(i)) + (str(k // len(IFACE_WORDS)) if k >= len(IFACE_WORDS) else "")), module, kind="interface")
                iface.seq = self.next_seq()
                parents = [t for m in self.modules[: i + 1] for t in m.types if t.kind == "interface"]
                if parents and rng.random() < p["iface_inherit"]:
                    iface.ifaces.append(rng.choice(parents))
                for n in range(rng.randint(1, 2)):
                    word = iface.name[1:].lower()
                    iface.methods.append(Method(f"{['handle', 'probe'][n]}{iface.name[1:]}", rng.randint(0, 2), iface, abstract=True))
                module.types.append(iface)
            for j in range(p["classes"]):
                self.add_class(module, j)
            self.add_tests(module)
        if self.lang == "angular":
            for module in self.modules:
                self.add_angular(module)
        self.modules[-1].types.append(self.program_type(self.modules[-1]))
        self.assign_calls()
        if p.get("hub"):
            self.add_hub()

    def next_seq(self) -> int:
        self.seq += 1
        return self.seq

    def class_name(self, module: Module, j: int) -> str:
        word = WORDS[j % len(WORDS)] + (str(j // len(WORDS)) if j >= len(WORDS) else "")
        return word if self.p.get("collide") else f"{word}{module.index}"

    def concrete_classes(self, upto: int) -> list[Type]:
        return [t for m in self.modules[: upto + 1] for t in self.all_types(m)
                if t.kind == "class" and not t.abstract and not t.test and not t.program and t.role is None]

    def all_types(self, module: Module):
        stack = list(module.types)
        while stack:
            t = stack.pop(0)
            yield t
            stack[0:0] = t.nested

    def add_class(self, module: Module, j: int):
        p, rng = self.p, self.rng
        t = Type(self.class_name(module, j), module)
        t.seq = self.next_seq()
        candidates = [c for c in (self.concrete_classes(module.index) + [c for c in self.all_types(module) if c.abstract])
                      if not c.generic and c.outer is None and len(list(c.ancestors())) + 1 < p["depth"]
                      and (not p.get("collide") or c.module is module)]
        if p.get("chain"):
            previous = [c for m in self.modules[: module.index + 1] for c in m.types if c.kind == "class" and not c.test]
            if previous:
                t.base = previous[-1]
        elif candidates and rng.random() < p["inherit"]:
            t.base = rng.choice(candidates)
        nested_host = [c for c in module.types if c.kind == "class" and not c.generic and c.outer is None and not c.test]
        if t.base is None and not p.get("chain") and rng.random() < p["generic"]:
            t.generic = True
        elif t.base is None and nested_host and rng.random() < p["nested"] and self.lang != "javascript":
            t.outer = rng.choice(nested_host)
        if t.base is None and not t.generic and t.outer is None and self.lang != "javascript" and rng.random() < 0.15 and not p.get("chain"):
            t.abstract = True
        ifaces = [i for m in self.modules[: module.index + 1] for i in m.types if i.kind == "interface"]
        if ifaces and not t.generic and rng.random() < 0.6:
            inherited = t.base.all_ifaces() if t.base else set()
            for i in rng.sample(ifaces, min(len(ifaces), rng.randint(1, 2))):
                # No overlap with what the base already implements or with another chosen interface, so every
                # member has exactly one declaration to implement it and none re-implements an inherited one.
                names = {m.name for x in closure(i) for m in x.methods}
                taken = {m.name for x in [t, *t.ancestors()] for m in x.methods} | {m.name for x in t.ifaces for y in closure(x) for m in y.methods}
                if not (closure(i) & inherited) and all(not (closure(i) & closure(x)) for x in t.ifaces) and not (names & taken):
                    t.ifaces.append(i)

        t.methods.append(Method("<init>", 1, t, ctor=True))
        tag = self.tag(t)
        for k in range(p["methods"]):
            name = f"{VERBS[(t.seq + k) % len(VERBS)]}{tag}{k}"
            static = not t.generic and rng.random() < p["static"]
            t.methods.append(Method(name, rng.randint(0, 3), t, static=static))
            if rng.random() < p["overload"]:
                arity = rng.choice([a for a in range(4) if a != t.methods[-1].arity])
                t.methods.append(Method(name, arity, t, static=static))
        if t.generic:
            t.methods.append(Method("put", 1, t))
        if t.abstract:
            t.methods.append(Method(f"area{tag}", rng.randint(0, 2), t, abstract=True))
        # Overrides: the nearest declaration of each inherited instance method, always for abstract ones.
        seen = set()
        for ancestor in t.ancestors():
            for m in ancestor.methods:
                key = (m.name, m.arity)
                if m.ctor or m.static or m.private or key in seen:
                    continue
                seen.add(key)
                must = m.abstract and not t.abstract
                if must or rng.random() < p["override"]:
                    o = Method(m.name, m.arity, t, overrides=m)
                    o.implements = []
                    if rng.random() < p["super_"] and not m.abstract:
                        o.calls.append(("super", m, None))
                    t.methods.append(o)
        # Interface members: declared on the implementing type, for its interfaces and theirs.
        def iface_members(i: Type):
            for parent in i.ifaces:
                yield from iface_members(parent)
            yield from i.methods
        for i in t.ifaces:
            for im in iface_members(i):
                if not any(m.name == im.name and m.arity == im.arity for m in t.methods):
                    t.methods.append(Method(im.name, im.arity, t, implements=[im]))
        if rng.random() < p["dead"]:
            t.methods.append(Method(f"unused{tag}", 1, t, static=self.lang not in ("typescript", "javascript", "angular"), private=True, dead=True))
        if t.outer:
            t.outer.nested.append(t)
        else:
            module.types.append(t)

    def tag(self, t: Type) -> str:
        # Distinct per class along any inheritance chain, so an own method never hides or overrides by accident.
        n = t.seq if not self.p.get("collide") else 1 + sum(1 for x in self.all_types(t.module) if x.kind == "class")
        letters = ""
        while True:
            letters = chr(ord("A") + n % 26) + letters
            n //= 26
            if n == 0:
                return letters

    def add_tests(self, module: Module):
        t = Type(f"{module.name.upper()}Tests", module, test=True)
        t.seq = self.next_seq()
        t.methods.append(Method("<init>", 0, t, ctor=True))
        for k in range(self.p["tests"]):
            t.methods.append(Method(f"test{k}", 0, t, test=True))
        module.types.append(t)

    def program_type(self, module: Module) -> Type:
        t = Type("Program", module, program=True)
        t.methods.append(Method("main", 1 if self.lang == "java" else 0, t, static=True, entry=True))
        return t

    def add_angular(self, module: Module):
        rng = self.rng
        services, components = [], []
        for k in range(max(1, self.p["classes"] // 3)):
            s = Type(f"{WORDS[(k + 7) % len(WORDS)]}Service{module.index}", module, role="service")
            s.seq = self.next_seq()
            s.methods.append(Method("<init>", 0, s, ctor=True))
            for n in range(2):
                s.methods.append(Method(f"fetch{self.tag(s)}{n}", rng.randint(0, 2), s))
            earlier = [x for m in self.modules[: module.index + 1] for x in m.types if x.role == "service" and x is not s]
            if earlier and rng.random() < 0.5:
                s.injects.append((rng.choice(earlier), rng.choice(["ctor", "inject"])))
            module.types.append(s)
            services.append(s)
        for k in range(max(1, self.p["classes"] // 2)):
            c = Type(f"{WORDS[(k + 13) % len(WORDS)]}{module.index}Component", module, role="component")
            c.seq = self.next_seq()
            c.selector = f"app-{module.name}-c{k}"
            c.methods.append(Method("<init>", 0, c, ctor=True))
            for n in range(2):
                c.methods.append(Method(f"show{self.tag(c)}{n}", 0, c))
            if rng.random() < 0.6:
                c.methods.append(Method("ngOnInit", 0, c, lifecycle=True))
            c.methods.append(Method(f"unused{self.tag(c)}", 0, c, private=True, dead=True))
            c.template_calls = [m for m in c.methods if m.name.startswith("show") and rng.random() < 0.7]
            pool = [x for m in self.modules[: module.index + 1] for x in m.types if x.role == "service"]
            for s in rng.sample(pool, min(len(pool), rng.randint(1, 2))):
                c.injects.append((s, rng.choice(["ctor", "inject"])))
            earlier = [x for m in self.modules[: module.index + 1] for x in m.types if x.role == "component" and x is not c] + components
            for child in rng.sample(earlier, min(len(earlier), rng.randint(0, 2))):
                if child not in c.renders:
                    c.renders.append(child)
            module.types.append(c)
            components.append(c)
        for x in services + components:  # injected constructor parameters are the constructor's parameters
            x.ctor().arity = sum(1 for _, how in x.injects if how == "ctor")

    # ---- bodies ---------------------------------------------------------------------------------------------------
    def callable_targets(self, upto: int):
        out = []
        for t in self.concrete_classes(upto):
            for m in t.methods:
                if not (m.ctor or m.private or m.abstract):
                    out.append(m)
        return out

    def iface_targets(self, upto: int):
        out = []
        concrete = self.concrete_classes(upto)
        for m in self.modules[: upto + 1]:
            for i in m.types:
                if i.kind != "interface":
                    continue
                impls = [c for c in concrete if i in c.all_ifaces()]
                for im in i.methods:
                    if impls:
                        out.append((im, impls))
        return out

    def assign_calls(self):
        p, rng = self.p, self.rng
        for module in self.modules:
            targets = self.callable_targets(module.index)
            ifaces = self.iface_targets(module.index)
            for t in list(self.all_types(module)):
                for m in t.methods:
                    if m.abstract or m.dead or m.ctor or t.kind == "interface":
                        if m.ctor and t.base:
                            m.calls.append(("base-ctor", t.base.ctor(), None))
                        continue
                    if m.test:
                        own = [x for x in targets if x.owner.module is module] or targets
                        for target in rng.sample(own, min(len(own), rng.randint(1, 3))):
                            m.calls.append(("new" if not target.static else "static", target, None))
                        continue
                    if m.entry:
                        for target in rng.sample(targets, min(len(targets), 4)):
                            m.calls.append(("new" if not target.static else "static", target, None))
                        continue
                    for _ in range(rng.randint(0, p["fanout"])):
                        roll = rng.random()
                        if ifaces and roll < 0.25:
                            im, impls = rng.choice(ifaces)
                            m.calls.append(("iface", im, rng.choice(impls)))
                        elif roll < 0.25 + 0.15 and not m.static:
                            own = [x for x in t.methods if x is not m and not (x.ctor or x.abstract or x.dead or x.test or x.generic_param)]
                            if own:
                                m.calls.append(("self", rng.choice(own), None))
                        elif targets:
                            target = rng.choice(targets)
                            if target is m:
                                continue
                            kind = "static" if target.static else "new"
                            if kind == "new" and rng.random() < p["lambda_"]:
                                kind = "lambda"
                            m.calls.append((kind, target, None))
            for t in list(self.all_types(module)):
                if t.role == "component":
                    for n, m in enumerate(x for x in t.methods if x.name.startswith("show") or x.lifecycle):
                        for s, _ in t.injects[:1]:
                            sm = [x for x in s.methods if not x.ctor]
                            m.calls.append(("field", rng.choice(sm), f"s{t.injects.index((s, _))}"))
                elif t.role == "service":
                    for m in t.methods:
                        if not m.ctor and targets and rng.random() < 0.7:
                            target = rng.choice(targets)
                            m.calls.append(("static" if target.static else "new", target, None))

    def add_hub(self):
        hub = Type("Hub", self.modules[0])
        hub.seq = self.next_seq()
        hub.methods.append(Method("<init>", 1, hub, ctor=True))
        ping = Method("ping", 0, hub, static=True)
        storm = Method("storm", 0, hub, static=True)
        hub.methods += [ping, storm]
        self.modules[0].types.insert(0, hub)
        for module in self.modules:
            for t in self.all_types(module):
                for m in t.methods:
                    if not (m.ctor or m.abstract or m.dead or t.kind == "interface" or m is ping or m is storm) and t.role is None:
                        m.calls.append(("static", ping, None))
        everything = [m for m in self.callable_targets(0) if m.owner is not hub]
        storm.calls = [("static" if x.static else "new", x, None) for x in everything]
        self.modules[-1].types[-1].methods[0].calls.append(("static", storm, None))


# ---- naming: the full name each extractor gives a node, after check.py's normalisation ----------------------------------

class Writer:
    """Emits one language. Subclasses fill files{} and give each type and member the name its extractor reports."""

    def __init__(self, model: Model):
        self.m = model
        self.files: dict[str, str] = {}
        self.layout = model.p.get("layout", "class")
        self.multi = model.p.get("multi", False)

    def type_fq(self, t: Type) -> str: ...

    def member_fq(self, m: Method) -> str:
        return f"{self.type_fq(m.owner)}.{'<init>' if m.ctor else self.member_name(m)}"

    def member_name(self, m: Method) -> str:
        return m.name

    def key(self, m: Method) -> str:
        return f"{self.member_fq(m)}/{m.arity}"

    def args(self, n: int) -> str:
        return ", ".join(["1"] * n)


class CSharp(Writer):
    def ns(self, module: Module) -> str:
        return f"Corpus.{module.name.upper()}"

    def type_fq(self, t: Type) -> str:
        return ".".join([self.ns(t.module)] + [x.name for x in t.path])

    def ref(self, t: Type, generic_arg: bool = True) -> str:
        return "global::" + self.type_fq(t) + ("<int>" if t.generic and generic_arg else "")

    def params(self, m: Method) -> str:
        if m.generic_param:
            return "T v"
        return ", ".join(f"int p{i}" for i in range(m.arity))

    def call(self, kind, target: Method, extra, n: int) -> str:
        a = self.args(target.arity)
        if kind == "new":
            return f"r += new {self.ref(target.owner)}(1).{target.name}({a});"
        if kind == "static":
            return f"r += {self.ref(target.owner)}.{target.name}({a});"
        if kind == "lambda":
            return f"System.Func<int, int> g{n} = x => new {self.ref(target.owner)}(1).{target.name}({a}); r += g{n}(1);"
        if kind == "iface":
            return f"{{ {self.ref(target.owner)} f{n} = new {self.ref(extra)}(1); r += f{n}.{target.name}({a}); }}"
        if kind == "super":
            return f"r += base.{target.name}({a});"
        if kind == "self":
            return f"r += {'this.' if not target.static else self.ref(target.owner) + '.'}{target.name}({a});"
        raise ValueError(kind)

    def body(self, m: Method) -> list[str]:
        lines = ["int r = 0;"] + [self.call(k, t, e, n) for n, (k, t, e) in enumerate(m.calls)]
        return lines + ["return r;"]

    def type_text(self, t: Type, indent: str) -> list[str]:
        out = []
        if t.kind == "interface":
            bases = " : " + ", ".join(self.ref(i) for i in t.ifaces) if t.ifaces else ""
            out.append(f"{indent}public interface {t.name}{bases}")
            out.append(indent + "{")
            for m in t.methods:
                out.append(f"{indent}    int {m.name}({self.params(m)});")
            out.append(indent + "}")
            return out
        if t.program:
            out += [f"{indent}public static class Program", indent + "{", f"{indent}    public static void Main()", indent + "    {", f"{indent}        int r = 0;"]
            out += [f"{indent}        {self.call(k, x, e, n)}" for n, (k, x, e) in enumerate(t.methods[0].calls)]
            out += [f"{indent}        System.Console.WriteLine(r);", indent + "    }", indent + "}"]
            return out
        supers = ([self.ref(t.base)] if t.base else []) + [self.ref(i) for i in t.ifaces]
        out.append(f"{indent}public {'abstract ' if t.abstract else ''}class {t.name}{'<T>' if t.generic else ''}{' : ' + ', '.join(supers) if supers else ''}")
        out.append(indent + "{")
        for m in t.methods:
            if m.ctor:
                base = f" : base({self.args(t.base.ctor().arity)})" if t.base else ""
                out.append(f"{indent}    public {t.name}({self.params(m)}){base} {{ }}")
                continue
            if m.test:
                out.append(f"{indent}    [Xunit.Fact]")
                out.append(f"{indent}    public void {m.name}()")
                out.append(indent + "    {")
                out += [f"{indent}        {line}" for line in self.body(m)[:-1]]
                out.append(indent + "    }")
                continue
            if m.dead:
                out.append(f"{indent}    private static int {m.name}(int p0) {{ return p0; }}")
                continue
            if m.abstract:
                out.append(f"{indent}    public abstract int {m.name}({self.params(m)});")
                continue
            modifier = "static " if m.static else "override " if m.overrides else "virtual "
            out.append(f"{indent}    public {modifier}int {m.name}({self.params(m)})")
            out.append(indent + "    {")
            out += [f"{indent}        {line}" for line in self.body(m)]
            out.append(indent + "    }")
        for n in t.nested:
            out += self.type_text(n, indent + "    ")
        out.append(indent + "}")
        return out

    def write(self, root: str):
        support = "namespace Xunit\n{\n    [System.AttributeUsage(System.AttributeTargets.Method)]\n    public sealed class FactAttribute : System.Attribute { }\n}\n"
        project = lambda exe, refs: (
            '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n'
            f'    <OutputType>{"Exe" if exe else "Library"}</OutputType>\n    <Nullable>disable</Nullable>\n'
            '    <NoWarn>CS0114;CS0219;CS8321</NoWarn>\n  </PropertyGroup>\n'
            + ("  <ItemGroup>\n" + "".join(f'    <ProjectReference Include="../{r}/{r}.csproj" />\n' for r in refs) + "  </ItemGroup>\n" if refs else "")
            + "</Project>\n")
        def namespace_block(module: Module, types: list[Type]) -> str:
            lines = [f"namespace {self.ns(module)}", "{"]
            for t in types:
                lines += self.type_text(t, "    ")
            return "\n".join(lines + ["}", ""])
        names = []
        for module in self.m.modules:
            folder = module.name.upper() if self.multi else ""
            if self.layout == "class":
                for t in module.types:
                    self.files[os.path.join(folder, module.name, f"{t.name}.cs")] = namespace_block(module, [t])
            elif self.layout == "module":
                self.files[os.path.join(folder, f"{module.name}.cs")] = namespace_block(module, module.types)
            if self.multi:
                names.append(folder)
                self.files[os.path.join(folder, f"{folder}.csproj")] = project(module is self.m.modules[-1], [x.name.upper() for x in self.m.modules[: module.index]])
                self.files[os.path.join(folder, "Support.cs")] = support if module.index == 0 else ""
        if self.layout == "single":
            self.files["All.cs"] = support + "\n".join(namespace_block(m, m.types) for m in self.m.modules)
        elif not self.multi:
            self.files["Support.cs"] = support
        if self.multi:
            self.files["Corpus.slnx"] = "<Solution>\n" + "".join(f'  <Project Path="{n}/{n}.csproj" />\n' for n in names) + "</Solution>\n"
            self.target = "Corpus.slnx"
        else:
            self.files["Corpus.csproj"] = project(True, [])
            self.target = "Corpus.csproj"
        self.files = {k: v for k, v in self.files.items() if v}


class Java(Writer):
    def pkg(self, module: Module) -> str:
        return f"corpus.{module.name}"

    def holder(self, module: Module) -> str | None:
        return module.name.upper() if self.layout in ("module", "single") else None

    def type_fq(self, t: Type) -> str:
        holder = self.holder(t.module) if not t.path[0].test else None  # test classes are top-level files in src/test
        parts = [self.pkg(t.module)] + ([holder] if holder else []) + [x.name for x in t.path]
        return ".".join(parts)

    def ref(self, t: Type, generic_arg: bool = True) -> str:
        return self.type_fq(t) + ("<Integer>" if t.generic and generic_arg else "")

    def params(self, m: Method) -> str:
        return "T v" if m.generic_param else ", ".join(f"int p{i}" for i in range(m.arity))

    def call(self, kind, target: Method, extra, n: int) -> str:
        a = self.args(target.arity)
        if kind == "new":
            return f"r += new {self.ref(target.owner)}(1).{target.name}({a});"
        if kind == "static":
            return f"r += {self.ref(target.owner, False)}.{target.name}({a});"
        if kind == "lambda":
            return f"java.util.function.IntUnaryOperator g{n} = x -> new {self.ref(target.owner)}(1).{target.name}({a}); r += g{n}.applyAsInt(1);"
        if kind == "iface":
            return f"{{ {self.ref(target.owner)} f{n} = new {self.ref(extra)}(1); r += f{n}.{target.name}({a}); }}"
        if kind == "super":
            return f"r += super.{target.name}({a});"
        if kind == "self":
            return f"r += {'this.' if not target.static else self.ref(target.owner, False) + '.'}{target.name}({a});"
        raise ValueError(kind)

    def body(self, m: Method) -> list[str]:
        return ["int r = 0;"] + [self.call(k, t, e, n) for n, (k, t, e) in enumerate(m.calls)] + ["return r;"]

    def type_text(self, t: Type, indent: str, top: bool) -> list[str]:
        static = "" if top else "static "
        visibility = "public " if top or True else ""
        out = []
        if t.kind == "interface":
            bases = " extends " + ", ".join(self.ref(i) for i in t.ifaces) if t.ifaces else ""
            out += [f"{indent}{visibility}interface {t.name}{bases} {{"] + [f"{indent}    int {m.name}({self.params(m)});" for m in t.methods] + [indent + "}"]
            return out
        if t.program:
            out += [f"{indent}{visibility}{static}class Program {{", f"{indent}    public static void main(String[] args) {{", f"{indent}        int r = 0;"]
            out += [f"{indent}        {self.call(k, x, e, n)}" for n, (k, x, e) in enumerate(t.methods[0].calls)]
            return out + [f"{indent}        System.out.println(r);", indent + "    }", indent + "}"]
        heads = f"{' extends ' + self.ref(t.base) if t.base else ''}{' implements ' + ', '.join(self.ref(i) for i in t.ifaces) if t.ifaces else ''}"
        out.append(f"{indent}{visibility}{static}{'abstract ' if t.abstract else ''}class {t.name}{'<T>' if t.generic else ''}{heads} {{")
        for m in t.methods:
            if m.ctor:
                call = f" super({self.args(t.base.ctor().arity)});" if t.base else ""
                out.append(f"{indent}    public {t.name}({self.params(m)}) {{{call} }}")
            elif m.test:
                out += [f"{indent}    @org.junit.jupiter.api.Test", f"{indent}    void {m.name}() {{"]
                out += [f"{indent}        {line}" for line in self.body(m)[:-1]] + [indent + "    }"]
            elif m.dead:
                out.append(f"{indent}    private static int {m.name}(int p0) {{ return p0; }}")
            elif m.abstract:
                out.append(f"{indent}    public abstract int {m.name}({self.params(m)});")
            else:
                if m.overrides or m.implements:
                    out.append(f"{indent}    @Override")
                out.append(f"{indent}    public {'static ' if m.static else ''}int {m.name}({self.params(m)}) {{")
                out += [f"{indent}        {line}" for line in self.body(m)] + [indent + "    }"]
        for n in t.nested:
            out += self.type_text(n, indent + "    ", False)
        return out + [indent + "}"]

    def write(self, root: str):
        junit = ("  <dependencies>\n    <dependency>\n      <groupId>org.junit.jupiter</groupId>\n      <artifactId>junit-jupiter</artifactId>\n"
                 "      <version>5.11.4</version>\n      <scope>test</scope>\n    </dependency>\n{extra}  </dependencies>\n")
        props = ("  <properties>\n    <maven.compiler.release>21</maven.compiler.release>\n"
                 "    <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>\n  </properties>\n")
        head = '<?xml version="1.0" encoding="UTF-8"?>\n<project xmlns="http://maven.apache.org/POM/4.0.0">\n  <modelVersion>4.0.0</modelVersion>\n'
        for module in self.m.modules:
            base = module.name if self.multi else ""
            pkg_dir = self.pkg(module).replace(".", "/")
            mains = [t for t in module.types if not t.test]
            tests = [t for t in module.types if t.test]
            for group, sub in ((mains, "main"), (tests, "test")):
                if not group:
                    continue
                if self.holder(module) and sub == "main":
                    lines = [f"package {self.pkg(module)};", "", f"public final class {self.holder(module)} {{"]
                    for t in group:
                        lines += self.type_text(t, "    ", False)
                    self.files[os.path.join(base, "src", sub, "java", pkg_dir, f"{self.holder(module)}.java")] = "\n".join(lines + ["}", ""])
                else:
                    for t in group:
                        lines = [f"package {self.pkg(module)};", ""] + self.type_text(t, "", True)
                        self.files[os.path.join(base, "src", sub, "java", pkg_dir, f"{t.name}.java")] = "\n".join(lines + [""])
            if self.multi:
                deps = "".join(f"    <dependency>\n      <groupId>corpus</groupId>\n      <artifactId>{x.name}</artifactId>\n      <version>1.0</version>\n    </dependency>\n" for x in self.m.modules[: module.index])
                self.files[os.path.join(base, "pom.xml")] = (head + "  <parent>\n    <groupId>corpus</groupId>\n    <artifactId>parent</artifactId>\n    <version>1.0</version>\n  </parent>\n"
                    f"  <artifactId>{module.name}</artifactId>\n" + junit.format(extra=deps) + "</project>\n")
        if self.multi:
            modules = "".join(f"    <module>{m.name}</module>\n" for m in self.m.modules)
            self.files["pom.xml"] = head + "  <groupId>corpus</groupId>\n  <artifactId>parent</artifactId>\n  <version>1.0</version>\n  <packaging>pom</packaging>\n" + props + f"  <modules>\n{modules}  </modules>\n</project>\n"
        else:
            self.files["pom.xml"] = head + "  <groupId>corpus</groupId>\n  <artifactId>corpus</artifactId>\n  <version>1.0</version>\n" + props + junit.format(extra="") + "</project>\n"
        self.target = "pom.xml"


class TypeScript(Writer):
    ext = "ts"

    def __init__(self, model: Model):
        super().__init__(model)
        self.typed = model.lang != "javascript"

    def kebab(self, name: str) -> str:
        return re.sub(r"(?<!^)(?=[A-Z])", "-", name).lower()

    def file_of(self, t: Type) -> str:
        top = t.path[0]
        if t.test:
            return f"src/{t.module.name}/{t.module.name}.spec"
        if self.layout == "single":
            return "src/all"
        if self.layout == "module":
            return f"src/{t.module.name}/index"
        return f"src/{t.module.name}/{self.kebab(top.name)}"

    def type_fq(self, t: Type) -> str:
        inner = ".".join(x.name for x in t.path)
        if self.layout == "single":
            inner = f"{t.module.name}.{inner}"
        return f"{self.file_of(t)}:{inner}"

    def test_fq(self, m: Method) -> str:
        return f"{self.file_of(m.owner)}:{m.owner.name}.{m.name}"

    def member_fq(self, m: Method) -> str:
        if m.test:
            return self.test_fq(m)
        return super().member_fq(m)

    def member_name(self, m: Method) -> str:
        return ("#" + m.name) if (m.dead and not self.typed) else m.name

    # A reference from the file being written to type t.
    def ref(self, t: Type, here: str, generic_arg: bool = True) -> str:
        name = ".".join(x.name for x in t.path)
        if self.layout == "single":
            name = f"{t.module.name}.{name}"
        elif self.file_of(t) != here:
            alias = f"{t.module.name}_{t.path[0].name}"
            self.imports.setdefault(here, {})[alias] = (t.path[0].name, self.file_of(t))
            name = ".".join([alias] + [x.name for x in t.path[1:]])
        return name + ("<number>" if t.generic and generic_arg and self.typed else "")

    def params(self, m: Method) -> str:
        if not self.typed:
            return ", ".join(f"p{i}" for i in range(m.arity))
        if m.generic_param:
            return "v: T"
        return ", ".join(f"p{i}: number" for i in range(m.arity))

    def call(self, kind, target: Method, extra, n: int, here: str) -> str:
        a = self.args(target.arity)
        name = self.member_name(target)
        if kind == "new":
            return f"r += new {self.ref(target.owner, here)}(1).{name}({a});"
        if kind == "static":
            return f"r += {self.ref(target.owner, here, False)}.{name}({a});"
        if kind == "lambda":
            return f"const g{n} = ({'x: number' if self.typed else 'x'}) => new {self.ref(target.owner, here)}(1).{name}({a}); r += g{n}(1);"
        if kind == "iface":
            return f"{{ const f{n}: {self.ref(target.owner, here)} = new {self.ref(extra, here)}(1); r += f{n}.{name}({a}); }}"
        if kind == "super":
            return f"r += super.{name}({a});"
        if kind == "self":
            return f"r += {'this.' if not target.static else self.ref(target.owner, here, False) + '.'}{name}({a});"
        if kind == "field":
            return f"r += this.{extra}.{name}({a});"
        raise ValueError(kind)

    def body(self, m: Method, here: str) -> list[str]:
        return ["let r = 0;"] + [self.call(k, t, e, n, here) for n, (k, t, e) in enumerate(m.calls)] + ["return r;"]

    def number(self) -> str:
        return ": number" if self.typed else ""

    def class_text(self, t: Type, here: str, indent: str, export: bool = True) -> list[str]:
        ex = "export " if export else ""
        out = []
        if t.kind == "interface":
            bases = " extends " + ", ".join(self.ref(i, here) for i in t.ifaces) if t.ifaces else ""
            return [f"{indent}{ex}interface {t.name}{bases} {{"] + [f"{indent}  {m.name}({self.params(m)}): number;" for m in t.methods] + [indent + "}"]
        if t.program:
            return [f"{indent}{ex}function main(){': void' if self.typed else ''} {{", f"{indent}  let r = 0;"] + [
                f"{indent}  {self.call(k, x, e, n, here)}" for n, (k, x, e) in enumerate(t.methods[0].calls)] + [f"{indent}  console.log(r);", indent + "}"]
        if t.test:
            lines = [f"{indent}describe('{t.name}', () => {{"]
            for m in t.methods:
                if m.test:
                    lines += [f"{indent}  it('{m.name}', () => {{"] + [f"{indent}    {line}" for line in self.body(m, here)[:-1]] + [indent + "  });"]
            return lines + [indent + "});"]
        decorator = []
        if t.role == "service":
            decorator = [f"{indent}@Injectable({{ providedIn: 'root' }})"]
        elif t.role == "component":
            template = " ".join([f"<{c.selector}></{c.selector}>" for c in t.renders] + [f"<span (click)=\"{m.name}()\">{{{{ {m.name}() }}}}</span>" for m in t.template_calls])
            imports = ", ".join(self.ref(c, here) for c in t.renders)
            decorator = [f"{indent}@Component({{ selector: '{t.selector}', imports: [{imports}], template: '{template}' }})"]
        heads = (" extends " + self.ref(t.base, here) if t.base else "") + (" implements " + ", ".join(self.ref(i, here) for i in t.ifaces) if t.ifaces and self.typed else "")
        out += decorator + [f"{indent}{ex}{'abstract ' if t.abstract and self.typed else ''}class {t.name}{'<T>' if t.generic and self.typed else ''}{heads} {{"]
        for k, (s, how) in enumerate(t.injects):
            if how == "inject":
                out.append(f"{indent}  private readonly s{k} = inject({self.ref(s, here)});")
        for m in t.methods:
            if m.ctor:
                ctor_params = [f"private readonly s{k}: {self.ref(s, here)}" for k, (s, how) in enumerate(t.injects) if how == "ctor"]
                own = "" if t.role else self.params(m)
                params = ", ".join(([own] if own else []) + ctor_params)
                call = f" super({self.args(t.base.ctor().arity)});" if t.base else ""
                out.append(f"{indent}  constructor({params}) {{{call} }}")
            elif m.dead:
                out.append(f"{indent}  {'private ' if self.typed else ''}{self.member_name(m)}({'p0: number' if self.typed and m.arity else 'p0' if m.arity else ''}){self.number()} {{ return {'p0' if m.arity else '0'}; }}")
            elif m.abstract:
                out.append(f"{indent}  {'abstract ' if self.typed else ''}{m.name}({self.params(m)}){self.number()}{';' if self.typed else ' { return 0; }'}")
            else:
                modifier = ("static " if m.static else "") + ("override " if m.overrides and self.typed else "")
                out.append(f"{indent}  {modifier}{m.name}({self.params(m)}){self.number()} {{")
                out += [f"{indent}    {line}" for line in self.body(m, here)] + [indent + "  }"]
        out.append(indent + "}")
        if t.nested:
            out.append(f"{indent}{ex}namespace {t.name} {{")
            for n in t.nested:
                out += self.class_text(n, here, indent + "  ")
            out.append(indent + "}")
        return out

    def ordered(self, types: list[Type]) -> list[Type]:
        # A class must come after the class it extends when both share a file.
        done, out = set(), []
        def visit(t):
            if t in done:
                return
            done.add(t)
            if t.base and t.base in types:
                visit(t.base)
            out.append(t)
        for t in types:
            visit(t)
        return out

    def write(self, root: str):
        self.imports: dict[str, dict] = {}
        bodies: dict[str, list[str]] = {}
        all_types = [t for m in self.m.modules for t in m.types]
        if self.layout == "single":
            lines = []
            for module in self.m.modules:
                lines.append(f"namespace {module.name} {{")
                for t in self.ordered([x for x in module.types if not x.test]):
                    lines += self.class_text(t, "src/all", "  ")
                lines.append("}")
                for t in (x for x in module.types if x.test):  # tests keep their own spec file, as in a real repo
                    bodies[self.file_of(t)] = self.class_text(t, self.file_of(t), "")
            bodies["src/all"] = lines
        else:
            for t in self.ordered(all_types):
                here = self.file_of(t)
                bodies.setdefault(here, [])
                bodies[here] += self.class_text(t, here, "")
        angular = self.m.lang == "angular"
        for here, lines in bodies.items():
            head = []
            if angular and any(x.role for x in all_types if self.file_of(x) == here):
                head.append("import { Component, Injectable, inject } from '@angular/core';")
            for alias, (name, path) in sorted(self.imports.get(here, {}).items()):
                rel = os.path.relpath(path, os.path.dirname(here))
                rel = rel if rel.startswith(".") else "./" + rel
                head.append(f"import {{ {name} as {alias} }} from '{rel}{'.js' if not self.typed else ''}';")
            self.files[f"{here}.{self.ext}"] = "\n".join(head + ([""] if head else []) + lines + [""])
        if not self.typed:
            self.files = {k.replace(".ts", ".js"): v for k, v in self.files.items()}
        options = {"strict": False, "target": "ES2022", "module": "ES2022", "moduleResolution": "bundler", "noEmit": True,
                   "experimentalDecorators": True, "lib": ["ES2022", "DOM"]}
        if not self.typed:
            options.update(allowJs=True, checkJs=False)
        self.files["src/globals.d.ts"] = "declare function describe(name: string, fn: () => void): void;\ndeclare function it(name: string, fn: () => void): void;\n"
        include = ["src/**/*.ts"] + (["src/**/*.js"] if not self.typed else [])
        if angular:
            options["paths"] = {"@angular/core": ["./stubs/angular-core.d.ts"]}
            self.files["stubs/angular-core.d.ts"] = (
                "export declare function Component(options: { selector?: string; template?: string; imports?: unknown[] }): ClassDecorator;\n"
                "export declare function Injectable(options?: { providedIn?: 'root' | null }): ClassDecorator;\n"
                "export declare function inject<T>(token: abstract new (...args: never[]) => T): T;\n")
            self.files["tsconfig.json"] = json.dumps({"compilerOptions": options}, indent=2) + "\n"
            if self.multi:
                projects = {}
                for module in self.m.modules:
                    self.files[f"src/{module.name}/tsconfig.lib.json"] = json.dumps({"extends": "../../tsconfig.json", "include": ["./**/*.ts", "../globals.d.ts"], "exclude": ["./**/*.spec.ts"]}) + "\n"
                    self.files[f"src/{module.name}/tsconfig.spec.json"] = json.dumps({"extends": "../../tsconfig.json", "include": ["./**/*.spec.ts", "../globals.d.ts"]}) + "\n"
                    projects[module.name] = {"root": f"src/{module.name}", "projectType": "library",
                                             "architect": {"build": {"options": {"tsConfig": f"src/{module.name}/tsconfig.lib.json"}},
                                                           "test": {"options": {"tsConfig": f"src/{module.name}/tsconfig.spec.json"}}}}
                self.files["angular.json"] = json.dumps({"version": 1, "projects": projects}, indent=2) + "\n"
            else:
                self.files["tsconfig.app.json"] = json.dumps({"extends": "./tsconfig.json", "include": include, "exclude": ["src/**/*.spec.ts"]}) + "\n"
                self.files["tsconfig.spec.json"] = json.dumps({"extends": "./tsconfig.json", "include": ["src/**/*.spec.ts", "src/globals.d.ts"]}) + "\n"
                self.files["angular.json"] = json.dumps({"version": 1, "projects": {"corpus": {"root": "", "architect": {
                    "build": {"options": {"tsConfig": "tsconfig.app.json"}}, "test": {"options": {"tsConfig": "tsconfig.spec.json"}}}}}}, indent=2) + "\n"
            self.target = "angular.json"
        elif self.multi:
            for module in self.m.modules:
                refs = [{"path": f"../{x.name}"} for x in self.m.modules[: module.index]]
                self.files[f"src/{module.name}/tsconfig.json"] = json.dumps({"extends": "../../tsconfig.base.json", "compilerOptions": {"composite": True, "noEmit": False, "emitDeclarationOnly": True, "outDir": f"../../out/{module.name}"},
                                                                          "include": ["./**/*." + ("ts" if self.typed else "js"), "../globals.d.ts"], "references": refs}) + "\n"
            self.files["tsconfig.base.json"] = json.dumps({"compilerOptions": options}, indent=2) + "\n"
            self.files["tsconfig.json"] = json.dumps({"files": [], "references": [{"path": f"./src/{m.name}"} for m in self.m.modules]}, indent=2) + "\n"
            self.target = "tsconfig.json"
        else:
            self.files["tsconfig.json"] = json.dumps({"compilerOptions": options, "include": include}, indent=2) + "\n"
            self.target = "tsconfig.json"


WRITERS = {"csharp": CSharp, "java": Java, "typescript": TypeScript, "javascript": TypeScript, "angular": TypeScript}


# ---- answer key ----------------------------------------------------------------------------------------------------------

def answer_key(model: Model, w: Writer) -> dict:
    methods, types, edges = {}, {}, {k: set() for k in ["Calls", "Overrides", "Implements", "Supertype", "TestedBy", "Injects", "Renders", "TemplateCalls"]}
    callers: dict[str, set] = {}
    all_types = [t for m in model.modules for t in model.all_types(m)]
    for t in all_types:
        if not t.program:
            types[w.type_fq(t)] = t.kind
        for s in ([t.base] if t.base else []) + t.ifaces:
            edges["Supertype"].add((w.type_fq(t), w.type_fq(s)))
        for s, _ in t.injects:
            edges["Injects"].add((w.type_fq(t), w.type_fq(s)))
        for c in t.renders:
            edges["Renders"].add((w.type_fq(t), w.type_fq(c)))
        template = f"{w.type_fq(t)}.<template>/0" if t.role == "component" else None
        for m in t.template_calls:
            edges["TemplateCalls"].add((template, w.key(m)))
            callers.setdefault(w.key(m), set()).add(template)
        if t.program and model.lang in ("typescript", "javascript", "angular"):
            main = t.methods[0]
            fq = f"{w.file_of(t)}:{t.module.name + '.' if w.layout == 'single' else ''}main/0"
            methods[fq] = dict(entry=True, test=False, dead=False, ctor=False)
            for kind, target, extra in main.calls:
                for callee in call_targets(kind, target, extra, w):
                    edges["Calls"].add((fq, callee))
                    callers.setdefault(callee, set()).add(fq)
            continue
        for m in t.methods:
            if t.test and m.ctor and model.lang in ("typescript", "javascript", "angular"):
                continue  # a describe block has no constructor
            k = w.key(m) if not (t.program and model.lang in ("csharp",)) else f"{w.type_fq(t)}.Main/0"
            methods[k] = dict(entry=m.entry, test=m.test, dead=m.dead, ctor=m.ctor, lifecycle=m.lifecycle, abstract=m.abstract)
            if m.overrides:
                edges["Overrides"].add((k, w.key(m.overrides)))
            for i in m.implements:
                edges["Implements"].add((k, w.key(i)))
            for kind, target, extra in m.calls:
                for callee in call_targets(kind, target, extra, w):
                    if callee == k:
                        continue  # extractors record no self-calls
                    edges["Calls"].add((k, callee))
                    callers.setdefault(callee, set()).add(k)
                    if m.test:
                        edges["TestedBy"].add((callee, k))
    # Interfaces a derived type picks up through its base: members of interfaces implemented on the way.
    return dict(
        language=model.lang, profile=model.profile, target=w.target,
        methods=methods, types=types,
        edges={k: sorted(map(list, v)) for k, v in edges.items()},
        callers={k: sorted(v) for k, v in callers.items()},
        stats=dict(modules=len(model.modules), types=len(types), methods=len(methods), calls=len(edges["Calls"]),
                   files=len(w.files), lines=sum(v.count("\n") for v in w.files.values())),
        entry_is_framework=model.lang in ("csharp", "java"),
    )


def call_targets(kind, target: Method, extra, w: Writer) -> list[str]:
    if kind in ("new", "lambda"):
        return [w.key(target.owner.ctor()), w.key(target)]
    if kind == "iface":
        return [w.key(extra.ctor()), w.key(target)]
    return [w.key(target)]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lang", choices=LANGS, required=True)
    ap.add_argument("--profile", choices=sorted(PROFILES), required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--seed", type=int, default=1)
    a = ap.parse_args()
    if (a.lang, a.profile) in UNSUPPORTED:
        ap.error(f"{a.lang} cannot express the {a.profile} construction")
    model = Model(a.lang, a.profile, a.seed)
    writer = WRITERS[a.lang](model)
    writer.write(a.out)
    os.makedirs(a.out, exist_ok=True)
    for rel, text in writer.files.items():
        path = os.path.join(a.out, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    key = answer_key(model, writer)
    with open(os.path.join(a.out, "expected.json"), "w", encoding="utf-8") as f:
        json.dump(key, f, indent=1)
    s = key["stats"]
    print(f"{a.lang}/{a.profile}: {s['modules']} modules, {s['types']} types, {s['methods']} methods, {s['calls']} calls, {s['files']} files, {s['lines']} lines → {a.out}")


if __name__ == "__main__":
    main()
