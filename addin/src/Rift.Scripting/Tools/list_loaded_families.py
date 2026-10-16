# rift-tool: read
# Loaded families by category, with their types count and instance count. inputs: category (optional)
cat = category(arg("category")) if arg("category") else None
counts = {}
for fi in collect(FamilyInstance):
    try:
        fid = iid(fi.Symbol.Family.Id)
        counts[fid] = counts.get(fid, 0) + 1
    except Exception:
        pass
out = []
for f in collect(Family):
    fc = f.FamilyCategory
    if cat is not None and (fc is None or iid(fc.Id) != iid(cat.Id)):
        continue
    out.append({
        "id": iid(f.Id), "name": f.Name, "category": fc.Name if fc is not None else None,
        "in_place": f.IsInPlace, "types": f.GetFamilySymbolIds().Count, "instances": counts.get(iid(f.Id), 0),
    })
result = sorted(out, key=lambda x: (str(x["category"]), str(x["name"])))
