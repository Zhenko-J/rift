# rift-tool: read
# Parameter and selection filters, their categories, and the views and templates that use them.
usage = {}
for v in collect(View):
    try:
        for fid in v.GetFilters():
            usage.setdefault(iid(fid), []).append(("template:" if v.IsTemplate else "") + v.Name)
    except Exception:
        pass
out = []
for f in collect(FilterElement):
    d = {"id": iid(f.Id), "name": name_of(f), "kind": "selection" if isinstance(f, SelectionFilterElement) else "parameter"}
    if isinstance(f, ParameterFilterElement):
        try:
            d["categories"] = sorted([Category.GetCategory(doc, c).Name for c in f.GetCategories() if Category.GetCategory(doc, c) is not None], key=str)
        except Exception:
            d["categories"] = []
    d["used_by"] = sorted(usage.get(d["id"], []), key=str)
    out.append(d)
result = sorted(out, key=lambda x: str(x["name"]))
