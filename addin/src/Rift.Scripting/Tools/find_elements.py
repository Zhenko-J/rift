# rift-tool: read
# Typed element filter. inputs: category, view (limit to what the view shows; optional), type (type name or id;
# optional), rules [{parameter, operator, value}] on instance parameters (equals, not_equals, contains, begins_with,
# greater, less, has_value, has_no_value; numbers in mm for lengths), limit=500
cat = category(arg("category", required=True))
v = view(arg("view")) if arg("view") else None
col = (FilteredElementCollector(doc, v.Id) if v is not None else FilteredElementCollector(doc)).OfCategoryId(cat.Id).WhereElementIsNotElementType()
type_ref = arg("type")
rules = arg("rules") or []
limit = int(arg("limit", 500))


def number(p):
    v = p.AsDouble() if p.StorageType == StorageType.Double else p.AsInteger()
    return v * MM if p.StorageType == StorageType.Double and is_length(p) else v


def match(el):
    if type_ref is not None:
        t = doc.GetElement(el.GetTypeId())
        if t is None or not (str(iid(t.Id)) == str(type_ref) or name_of(t) == str(type_ref)):
            return False
    for r in rules:
        p = el.LookupParameter(r.get("parameter", ""))
        op = r.get("operator", "equals")
        if op == "has_value":
            if p is None or not p.HasValue:
                return False
            continue
        if op == "has_no_value":
            if p is not None and p.HasValue:
                return False
            continue
        if p is None:
            return False
        want = r.get("value")
        if p.StorageType in (StorageType.Double, StorageType.Integer) and isinstance(want, (int, float)) and not isinstance(want, bool):
            got = number(p)
            ok = {"equals": abs(got - want) < 1e-6, "not_equals": abs(got - want) >= 1e-6, "greater": got > want, "less": got < want}.get(op)
        else:
            got = (p.AsString() if p.StorageType == StorageType.String else p.AsValueString()) or ""
            w = str(want)
            ok = {"equals": got == w, "not_equals": got != w, "contains": w in got, "begins_with": got.startswith(w)}.get(op)
        if ok is None:
            raise ValueError("operator '%s' is not available here" % op)
        if not ok:
            return False
    return True


out = []
total = 0
for el in col:
    if match(el):
        total += 1
        if len(out) < limit:
            out.append(summary(el))
result = {"category": cat.Name, "view": v.Name if v is not None else None, "total": total, "elements": out, "truncated": total > limit}
