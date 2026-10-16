# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
# RIFT typed tools (M3, ADR-032): prelude prepended to every tool script. Lengths are millimetres, categories are their
# displayed names (or OST_ names), elements are referred to by id or by name. A tool reads its arguments from `inputs`,
# assigns its answer to `result`, and raises ValueError with a clear message when an argument does not resolve.
import json, re, math
import System
from Autodesk.Revit.DB import *
from System import Int64
from System.Collections.Generic import List

MM = 304.8


def iid(x):
    try:
        return int(x.Value)
    except Exception:
        return int(x.IntegerValue)


def to_id(v):
    try:
        return ElementId(Int64(int(v)))
    except Exception:
        raise ValueError("not an element id: %r" % (v,))


def id_list(values):
    ids = List[ElementId]()
    for v in values:
        ids.Add(v if isinstance(v, ElementId) else to_id(v))
    return ids


def name_of(el):
    try:
        return Element.Name.GetValue(el)
    except Exception:
        try:
            return el.Name
        except Exception:
            return None


def arg(key, default=None, required=False):
    v = inputs.get(key, default) if inputs is not None else default
    if required and (v is None or v == "" or v == []):
        raise ValueError("argument '%s' is required" % key)
    return v


def collect(cls):
    return FilteredElementCollector(doc).OfClass(cls)


def is_id(ref):
    return isinstance(ref, int) or (isinstance(ref, str) and ref.strip().lstrip("-").isdigit())


def find_one(items, ref, what):
    """An element of `items` by id or exact name (then case-insensitive name); ValueError when none or several."""
    if ref is None or ref == "":
        raise ValueError("%s is required" % what)
    items = list(items)
    if is_id(ref):
        want = int(ref)
        for e in items:
            if iid(e.Id) == want:
                return e
    text = str(ref)
    exact = [e for e in items if name_of(e) == text]
    if len(exact) == 1:
        return exact[0]
    if len(exact) > 1:
        raise ValueError("%s '%s' is ambiguous (%d elements share the name; pass the id)" % (what, text, len(exact)))
    loose = [e for e in items if (name_of(e) or "").lower() == text.lower()]
    if len(loose) == 1:
        return loose[0]
    raise ValueError("%s '%s' not found" % (what, text))


def element(ref, what="element"):
    el = doc.GetElement(to_id(ref)) if is_id(ref) else None
    if el is None:
        raise ValueError("%s %s not found" % (what, ref))
    return el


def category(ref):
    if ref is None or ref == "":
        raise ValueError("category is required")
    text = str(ref)
    if text.startswith("OST_"):
        try:
            c = Category.GetCategory(doc, getattr(BuiltInCategory, text))
            if c is not None:
                return c
        except Exception:
            pass
    for c in doc.Settings.Categories:
        if c.Name == text or c.Name.lower() == text.lower():
            return c
    raise ValueError("category '%s' not found" % text)


def views(templates=False):
    return [v for v in collect(View) if v.IsTemplate == templates]


def view(ref=None):
    if ref is None or ref == "":
        v = doc.ActiveView
        if v is None:
            raise ValueError("no active view; pass a view")
        return v
    return find_one(views(False), ref, "view")


def template(ref):
    return find_one(views(True), ref, "view template")


def cat_name(el):
    try:
        return el.Category.Name if el.Category is not None else None
    except Exception:
        return None


def summary(el):
    d = {"id": iid(el.Id), "name": name_of(el), "category": cat_name(el), "class": el.GetType().Name}
    try:
        t = el.GetTypeId()
        if t is not None and iid(t) > 0:
            d["type_id"] = iid(t)
    except Exception:
        pass
    return d


def is_length(p):
    try:
        return p.Definition.GetDataType() == SpecTypeId.Length
    except Exception:
        return False


def param_value(p):
    d = {"name": p.Definition.Name, "read_only": p.IsReadOnly, "storage": str(p.StorageType)}
    try:
        bip = p.Definition.BuiltInParameter
        if bip != BuiltInParameter.INVALID:
            d["bip"] = str(bip)
    except Exception:
        pass
    try:
        if p.IsShared:
            d["guid"] = str(p.GUID)
    except Exception:
        pass
    st = p.StorageType
    if st == StorageType.String:
        d["value"] = p.AsString()
    elif st == StorageType.Integer:
        d["value"] = p.AsInteger()
    elif st == StorageType.Double:
        v = p.AsDouble()
        d["value"] = round(v * MM, 3) if is_length(p) else v
        if is_length(p):
            d["unit"] = "mm"
    elif st == StorageType.ElementId:
        e = p.AsElementId()
        d["value"] = iid(e) if e is not None else None
        ref = doc.GetElement(e) if e is not None and iid(e) > 0 else None
        if ref is not None:
            d["value_name"] = name_of(ref)
    try:
        d["display"] = p.AsValueString()
    except Exception:
        pass
    return d


def parameters(el):
    out = []
    for p in el.Parameters:
        try:
            out.append(param_value(p))
        except Exception:
            pass
    return sorted(out, key=lambda x: str(x["name"]))


def find_param(el, name):
    if name is None or name == "":
        raise ValueError("parameter name is required")
    text = str(name)
    if text.startswith("BIP:") or (text.isupper() and "_" in text):
        try:
            p = el.get_Parameter(getattr(BuiltInParameter, text.replace("BIP:", "")))
            if p is not None:
                return p
        except Exception:
            pass
    p = el.LookupParameter(text)
    if p is None:
        raise ValueError("parameter '%s' not found on element %d" % (text, iid(el.Id)))
    return p


def set_param(p, value):
    """Sets a parameter: text as is (or as a value string for numbers with units), numbers in mm for lengths, ids for elements."""
    if p.IsReadOnly:
        raise ValueError("parameter '%s' is read-only" % p.Definition.Name)
    st = p.StorageType
    if st == StorageType.String:
        ok = p.Set("" if value is None else str(value))
    elif st == StorageType.Integer:
        if isinstance(value, bool):
            ok = p.Set(1 if value else 0)
        elif isinstance(value, str) and not is_id(value):
            ok = p.SetValueString(value)
        else:
            ok = p.Set(int(value))
    elif st == StorageType.Double:
        if isinstance(value, str):
            ok = p.SetValueString(value)
        elif is_length(p):
            ok = p.Set(float(value) / MM)
        else:
            try:
                ok = p.Set(UnitUtils.ConvertToInternalUnits(float(value), p.GetUnitTypeId()))
            except Exception:
                ok = p.Set(float(value))
    elif st == StorageType.ElementId:
        ok = p.Set(ElementId.InvalidElementId if value is None else to_id(value))
    else:
        raise ValueError("parameter '%s' cannot be set" % p.Definition.Name)
    if not ok:
        raise ValueError("Revit refused the value %r for '%s'" % (value, p.Definition.Name))


def rgb(c):
    try:
        if c is not None and c.IsValid:
            return [c.Red, c.Green, c.Blue]
    except Exception:
        pass
    return None


def color(value):
    if value is None:
        return None
    if isinstance(value, str):
        h = value.lstrip("#")
        if len(h) != 6:
            raise ValueError("colour must be #RRGGBB or [r, g, b]")
        return Color(int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))
    r, g, b = value
    return Color(int(r), int(g), int(b))


class tx(object):
    """with tx('what'): … — one transaction named after the tool; the guard folds it into the run's undo entry."""

    def __init__(self, what):
        self.t = Transaction(doc, "rift: " + what)

    def __enter__(self):
        self.t.Start()
        return self.t

    def __exit__(self, kind, value, tb):
        if kind is None:
            self.t.Commit()
        elif self.t.HasStarted() and not self.t.HasEnded():
            self.t.RollBack()
        return False

# ---- tool body ---------------------------------------------------------------------------------------------------
