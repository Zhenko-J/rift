# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
# RIFT audit preset p5 (ADR-024): project parameter bindings and the inventory of shared parameters.
# Port to IronPython 3.4 of the maintainer's extraction script p5. Parity rule: same keys, same value
# computations, same sort orders. Changes from the original: no file system (dump() hands the JSON text to
# the add-in's collector), every sort key goes through str(), bare except clauses are narrowed by the engine;
# the optional block that regenerated a shared-parameter .txt file is not ported (it writes a file and changes
# the application's SharedParametersFilename).
# Produces the document 05_parametres:
#   - parametres_de_projet: the bindings (Manage > Project Parameters), one entry per parameter: shared, guid
#     (when shared), binding instance|type, data_type (localised label), data_type_id (stable), group, categories
#   - inventaire_parametres_partages: every SharedParameterElement (unique GUIDs) = project parameters plus the
#     shared parameters embedded in loaded families
import json, datetime
from Autodesk.Revit.DB import *

def dump(name, obj):
    rift_collector.Put(name, json.dumps(obj, ensure_ascii=False))
    return name

def iid(x):
    try: return int(x.Value)
    except: return int(x.IntegerValue)

def spec_label(dfn):
    try:
        ft = dfn.GetDataType()
        try: return LabelUtils.GetLabelForSpec(ft)
        except: return ft.TypeId
    except:
        try: return str(dfn.ParameterType)
        except: return None

def spec_id(dfn):      # stable key (autodesk.spec...), the label is localised
    try: return dfn.GetDataType().TypeId
    except: return None

def grp_id(dfn):
    try: return dfn.GetGroupTypeId().TypeId
    except: return None

def grp_label(dfn):
    try:
        return LabelUtils.GetLabelForGroup(dfn.GetGroupTypeId())
    except:
        try: return str(dfn.ParameterGroup)
        except: return None

report_progress(10, "shared parameters")
# map: ElementId of the definition -> guid (shared parameters present in the file)
shared_by_defid = {}
shared_seen = {}
for spe in FilteredElementCollector(doc).OfClass(SharedParameterElement):
    try:
        d = spe.GetDefinition()
        g = str(spe.GuidValue)
        shared_by_defid[iid(d.Id)] = g
        if g not in shared_seen:
            shared_seen[g] = {"name": d.Name, "guid": g,
                              "data_type": spec_label(d),
                              "data_type_id": spec_id(d),
                              "visible": getattr(d, "Visible", None)}
    except:
        pass

report_progress(50, "project parameter bindings")
bound = []
it = doc.ParameterBindings.ForwardIterator()
it.Reset()
while it.MoveNext():
    try:
        dfn = it.Key
        b = it.Current
        cats = sorted([c.Name for c in b.Categories])
        try:
            is_shared = bool(dfn.IsShared)
        except:
            is_shared = iid(dfn.Id) in shared_by_defid
        bound.append({
            "name": dfn.Name,
            "shared": is_shared,
            "guid": shared_by_defid.get(iid(dfn.Id)),
            "binding": "instance" if isinstance(b, InstanceBinding) else "type",
            "data_type": spec_label(dfn),
            "data_type_id": spec_id(dfn),
            "group": grp_label(dfn),
            "group_id": grp_id(dfn),
            "categories": cats,
        })
    except:
        pass
bound = sorted(bound, key=lambda x: str(x["name"]).lower())
allsp = sorted(shared_seen.values(), key=lambda x: str(x["name"]).lower())

data = {
    "_document": doc.Title,
    "_generated": str(datetime.datetime.now()),
    "_resume": {
        "parametres_de_projet_total": len(bound),
        "dont_partages": len([x for x in bound if x["shared"]]),
        "dont_projet_pur": len([x for x in bound if not x["shared"]]),
        "shared_parameter_elements_uniques": len(allsp),
    },
    "parametres_de_projet": bound,
    "inventaire_parametres_partages": allsp,
}
dump("05_parametres", data)
report_progress(100, "done")
print(json.dumps(data["_resume"], indent=2))
