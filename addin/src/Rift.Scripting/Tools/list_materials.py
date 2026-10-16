# rift-tool: read
# Materials: class, shading colour, transparency, appearance asset. inputs: contains (optional name filter)
needle = (arg("contains") or "").lower()
out = []
for m in collect(Material):
    if needle and needle not in m.Name.lower():
        continue
    d = {"id": iid(m.Id), "name": m.Name, "class": m.MaterialClass, "color": rgb(m.Color), "transparency": m.Transparency}
    try:
        a = doc.GetElement(m.AppearanceAssetId)
        d["appearance_asset"] = name_of(a) if a is not None else None
    except Exception:
        pass
    out.append(d)
result = sorted(out, key=lambda x: str(x["name"]))
