# rift-tool: read
# Graphics and assets of a material. inputs: material
m = find_one(collect(Material), arg("material", required=True), "material")


def pattern(pid):
    e = doc.GetElement(pid) if pid is not None and iid(pid) > 0 else None
    return name_of(e) if e is not None else None


d = {
    "id": iid(m.Id), "name": m.Name, "class": m.MaterialClass, "category": m.MaterialCategory,
    "color": rgb(m.Color), "transparency": m.Transparency, "shininess": m.Shininess, "smoothness": m.Smoothness,
    "surface_foreground": {"pattern": pattern(m.SurfaceForegroundPatternId), "color": rgb(m.SurfaceForegroundPatternColor)},
    "surface_background": {"pattern": pattern(m.SurfaceBackgroundPatternId), "color": rgb(m.SurfaceBackgroundPatternColor)},
    "cut_foreground": {"pattern": pattern(m.CutForegroundPatternId), "color": rgb(m.CutForegroundPatternColor)},
    "cut_background": {"pattern": pattern(m.CutBackgroundPatternId), "color": rgb(m.CutBackgroundPatternColor)},
}
for key, aid in (("appearance", m.AppearanceAssetId), ("structural", m.StructuralAssetId), ("thermal", m.ThermalAssetId)):
    e = doc.GetElement(aid) if aid is not None and iid(aid) > 0 else None
    d[key + "_asset"] = name_of(e) if e is not None else None
d["identity"] = [p for p in parameters(m) if p["name"] in ("Description", "Manufacturer", "Model", "Comments", "Keynote", "Mark", "Cost", "URL")]
result = d
