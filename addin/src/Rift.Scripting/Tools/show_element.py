# rift-tool: read
# Selects elements and zooms the active view on them (the model does not change). inputs: element_ids
if uidoc is None:
    raise ValueError("no document window is active")
els = [element(r) for r in arg("element_ids", required=True)]
ids = id_list([e.Id for e in els])
uidoc.Selection.SetElementIds(ids)
active = doc.ActiveView
method = "show_elements"
try:
    uidoc.ShowElements(ids)
except Exception as first:
    # ShowElements may refuse while the read guard holds its transaction group: zoom the open view instead.
    boxes = [b for b in [e.get_BoundingBox(active) for e in els] if b is not None]
    if not boxes:
        raise ValueError("the elements have no extent in the active view (%s)" % (first,))
    low = XYZ(min(b.Min.X for b in boxes), min(b.Min.Y for b in boxes), min(b.Min.Z for b in boxes))
    high = XYZ(max(b.Max.X for b in boxes), max(b.Max.Y for b in boxes), max(b.Max.Z for b in boxes))
    open_views = [u for u in uidoc.GetOpenUIViews() if iid(u.ViewId) == iid(active.Id)]
    if not open_views:
        raise ValueError("the active view is not open in a window")
    open_views[0].ZoomAndCenterRectangle(low, high)
    method = "zoom"
result = {"selected": len(els), "view": name_of(active), "method": method}
