# rift-tool: write
# Bind a shared parameter of the current shared parameter file to categories.
# inputs: name, categories [..], binding "instance"|"type", group (a GroupTypeId name such as "Data", "IdentityData",
# "Text"; default Data)
app = doc.Application
df = app.OpenSharedParameterFile() if app.SharedParametersFilename else None
if df is None:
    raise ValueError("no shared parameter file is set in Revit (Manage > Shared Parameters)")
name = arg("name", required=True)
found = None
for g in df.Groups:
    for d in g.Definitions:
        if d.Name == name:
            found = d
if found is None:
    raise ValueError("'%s' is not in the shared parameter file" % name)
kind = arg("binding", "instance")
# an existing binding keeps its categories: ReInsert replaces the whole set, which would unbind the parameter from them
spe = SharedParameterElement.Lookup(doc, found.GUID)
existing = doc.ParameterBindings.get_Item(spe.GetDefinition()) if spe is not None else None
if existing is not None and (kind == "type") != isinstance(existing, TypeBinding):
    raise ValueError("'%s' is already bound as %s; changing the binding kind is not done by this tool" % (name, "type" if isinstance(existing, TypeBinding) else "instance"))
cats = app.Create.NewCategorySet()
kept = []
if existing is not None:
    for cat in existing.Categories:
        cats.Insert(cat)
        kept.append(cat.Name)
added = []
for c in arg("categories", required=True):
    cat = category(c)
    if not cat.AllowsBoundParameters:
        raise ValueError("category '%s' does not accept bound parameters" % cat.Name)
    if cats.Insert(cat):
        added.append(cat.Name)
binding = app.Create.NewTypeBinding(cats) if kind == "type" else app.Create.NewInstanceBinding(cats)
group = getattr(GroupTypeId, arg("group", "Data"), None)
if group is None:
    raise ValueError("group '%s' unknown (a GroupTypeId member such as Data, IdentityData, Text)" % arg("group"))
def schedule_fields():
    out = {}
    for s in collect(ViewSchedule):
        if s.IsTemplate or s.Definition is None:
            continue
        out[s.Name] = s.Definition.GetFieldCount()
    return out
if existing is not None and not added:
    result = {"name": name, "guid": str(found.GUID), "binding": kind, "categories": sorted(kept), "added": [], "already_bound": kept}
else:
    before = schedule_fields() if existing is not None else {}
    with tx("bind_shared_parameter"):
        if existing is None:
            ok = doc.ParameterBindings.Insert(found, binding, group)
        else:
            # Revit 2026: ReInsert drops schedule fields that use the parameter (seen in acceptance, 2026-10-16)
            ok = doc.ParameterBindings.ReInsert(spe.GetDefinition(), binding, spe.GetDefinition().GetGroupTypeId())
            doc.Regenerate()
            after = schedule_fields()
            lost = sorted(n for n, c in before.items() if after.get(n, 0) < c)
            if lost:
                raise ValueError("rebinding '%s' would remove columns from schedules %s; add the categories in Revit (Manage > Project Parameters) instead" % (name, ", ".join(lost)))
    if not ok:
        raise ValueError("Revit refused the binding")
    result = {"name": name, "guid": str(found.GUID), "binding": kind, "categories": sorted(c.Name for c in cats), "added": added, "already_bound": kept}
