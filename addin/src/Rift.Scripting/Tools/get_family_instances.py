# rift-tool: read
# Instances of a family or of one of its types. inputs: family, type (optional), limit=500
f = find_one(collect(Family), arg("family", required=True), "family")
symbols = [doc.GetElement(s) for s in f.GetFamilySymbolIds()]
wanted = set(iid(s.Id) for s in symbols)
if arg("type"):
    wanted = set([iid(find_one(symbols, arg("type"), "type").Id)])
limit = int(arg("limit", 500))
out = []
total = 0
for fi in collect(FamilyInstance):
    try:
        if iid(fi.Symbol.Id) not in wanted:
            continue
    except Exception:
        continue
    total += 1
    if len(out) < limit:
        d = summary(fi)
        d["type"] = name_of(fi.Symbol)
        try:
            lvl = doc.GetElement(fi.LevelId)
            d["level"] = lvl.Name if lvl is not None else None
        except Exception:
            pass
        out.append(d)
result = {"family": f.Name, "total": total, "instances": out, "truncated": total > limit}
