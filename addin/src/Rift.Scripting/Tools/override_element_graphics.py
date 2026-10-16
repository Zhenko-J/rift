# rift-tool: write
# Override element graphics in a view. Either inputs.element_ids + color/halftone/line_weight, or inputs.category +
# inputs.parameter to colour every element of the category in the view by the value of that parameter (one colour per
# distinct value, palette below). inputs.view defaults to the active view.
v = view(arg("view"))
PALETTE = [(230, 25, 75), (60, 180, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180), (70, 240, 240), (240, 50, 230),
           (210, 245, 60), (250, 190, 212), (0, 128, 128), (220, 190, 255), (170, 110, 40), (128, 0, 0), (128, 128, 0)]
solid = [p for p in collect(FillPatternElement) if p.GetFillPattern().IsSolidFill]


def settings(c):
    o = OverrideGraphicSettings()
    if c is not None:
        o.SetProjectionLineColor(c)
        if solid:
            o.SetSurfaceForegroundPatternId(solid[0].Id)
            o.SetSurfaceForegroundPatternColor(c)
    if arg("halftone") is not None:
        o.SetHalftone(bool(arg("halftone")))
    if arg("line_weight") is not None:
        o.SetProjectionLineWeight(int(arg("line_weight")))
    return o


legend = {}
count = 0
with tx("override_element_graphics"):
    if arg("parameter"):
        cat = category(arg("category", required=True))
        els = FilteredElementCollector(doc, v.Id).OfCategoryId(cat.Id).WhereElementIsNotElementType()
        for el in els:
            p = el.LookupParameter(arg("parameter"))
            key = (p.AsValueString() or p.AsString() or "") if p is not None else "(none)"
            if key not in legend:
                rgbv = PALETTE[len(legend) % len(PALETTE)]
                legend[key] = list(rgbv)
            v.SetElementOverrides(el.Id, settings(Color(*legend[key])))
            count += 1
    else:
        o = settings(color(arg("color")))
        for i in arg("element_ids", required=True):
            v.SetElementOverrides(to_id(i), o)
            count += 1
result = {"view": v.Name, "elements": count, "legend": legend}
