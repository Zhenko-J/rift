# rift-tool: read
# Levels with elevation (mm, from the project base) and whether they have a plan view.
plans = set()
for v in collect(ViewPlan):
    try:
        if not v.IsTemplate and v.GenLevel is not None:
            plans.add(iid(v.GenLevel.Id))
    except Exception:
        pass
result = sorted(
    [{"id": iid(l.Id), "name": l.Name, "elevation_mm": round(l.Elevation * MM, 1), "has_plan": iid(l.Id) in plans} for l in collect(Level)],
    key=lambda x: x["elevation_mm"])
