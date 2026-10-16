# rift-tool: read
# Types of a family, with their instance count. inputs: family
f = find_one(collect(Family), arg("family", required=True), "family")
counts = {}
for fi in collect(FamilyInstance):
    try:
        counts[iid(fi.Symbol.Id)] = counts.get(iid(fi.Symbol.Id), 0) + 1
    except Exception:
        pass
out = []
for sid in f.GetFamilySymbolIds():
    s = doc.GetElement(sid)
    out.append({"id": iid(sid), "name": name_of(s), "active": s.IsActive, "instances": counts.get(iid(sid), 0)})
result = {"family": f.Name, "types": sorted(out, key=lambda x: str(x["name"]))}
