# rift-tool: write
# Graphic overrides of a filter in a view or template. inputs: filter, view, color (#RRGGBB|[r,g,b]), line_weight 1-16,
# halftone, transparency 0-100, solid_fill (true: solid surface pattern in the colour)
f = find_one(collect(FilterElement), arg("filter", required=True), "filter")
ref = arg("view", required=True)
try:
    target = view(ref)
except ValueError:
    target = template(ref)
if f.Id not in list(target.GetFilters()):
    raise ValueError("filter '%s' is not applied to '%s' (apply_filter_to_view first)" % (name_of(f), target.Name))
o = OverrideGraphicSettings()
c = color(arg("color"))
if c is not None:
    o.SetProjectionLineColor(c)
    o.SetCutLineColor(c)
    if arg("solid_fill", False):
        solid = [p for p in collect(FillPatternElement) if p.GetFillPattern().IsSolidFill]
        if solid:
            o.SetSurfaceForegroundPatternId(solid[0].Id)
            o.SetSurfaceForegroundPatternColor(c)
            o.SetCutForegroundPatternId(solid[0].Id)
            o.SetCutForegroundPatternColor(c)
lw = arg("line_weight")
if lw is not None:
    o.SetProjectionLineWeight(int(lw))
if arg("halftone") is not None:
    o.SetHalftone(bool(arg("halftone")))
if arg("transparency") is not None:
    o.SetSurfaceTransparency(int(arg("transparency")))
with tx("set_filter_overrides"):
    target.SetFilterOverrides(f.Id, o)
result = {"filter": name_of(f), "view": target.Name}
