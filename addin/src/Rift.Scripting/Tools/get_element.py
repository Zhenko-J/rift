# rift-tool: read
# Elements by id: category, class, type, level, location (mm), bounding box (mm). inputs: element_ids [..]
out = []
for i in arg("element_ids", required=True):
    el = element(i)
    d = summary(el)
    t = doc.GetElement(el.GetTypeId())
    if t is not None:
        d["type"] = name_of(t)
        d["family"] = getattr(t, "FamilyName", None)
    try:
        lvl = doc.GetElement(el.LevelId)
        d["level"] = lvl.Name if lvl is not None else None
    except Exception:
        pass
    loc = el.Location
    if isinstance(loc, LocationPoint):
        d["location_mm"] = [round(loc.Point.X * MM, 1), round(loc.Point.Y * MM, 1), round(loc.Point.Z * MM, 1)]
    elif isinstance(loc, LocationCurve):
        a, b = loc.Curve.GetEndPoint(0), loc.Curve.GetEndPoint(1)
        d["curve_mm"] = [[round(a.X * MM, 1), round(a.Y * MM, 1), round(a.Z * MM, 1)], [round(b.X * MM, 1), round(b.Y * MM, 1), round(b.Z * MM, 1)]]
        d["length_mm"] = round(loc.Curve.Length * MM, 1)
    bb = el.get_BoundingBox(None)
    if bb is not None:
        d["bbox_mm"] = {"min": [round(c * MM, 1) for c in (bb.Min.X, bb.Min.Y, bb.Min.Z)], "max": [round(c * MM, 1) for c in (bb.Max.X, bb.Max.Y, bb.Max.Z)]}
    out.append(d)
result = out
