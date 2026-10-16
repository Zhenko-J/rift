# rift-tool: write
# Place a view (or a schedule) on a sheet. inputs: view, sheet (number, name or id), x_mm, y_mm (centre; default: the
# centre of the sheet's title block)
sheets = [s for s in collect(ViewSheet) if not s.IsTemplate]
ref = arg("sheet", required=True)
match = [s for s in sheets if s.SheetNumber == str(ref)]
sh = match[0] if len(match) == 1 else find_one(sheets, ref, "sheet")
candidates = [v for v in collect(View) if not v.IsTemplate and not isinstance(v, ViewSheet)]
v = find_one(candidates, arg("view", required=True), "view")
if arg("x_mm") is not None and arg("y_mm") is not None:
    point = XYZ(float(arg("x_mm")) / MM, float(arg("y_mm")) / MM, 0)
else:
    box = sh.Outline
    point = XYZ((box.Min.U + box.Max.U) / 2.0, (box.Min.V + box.Max.V) / 2.0, 0)
with tx("place_view_on_sheet"):
    if isinstance(v, ViewSchedule):
        placed = ScheduleSheetInstance.Create(doc, sh.Id, v.Id, point)
    else:
        if not Viewport.CanAddViewToSheet(doc, sh.Id, v.Id):
            raise ValueError("view '%s' cannot be placed on sheet %s (already on a sheet, or not placeable)" % (v.Name, sh.SheetNumber))
        placed = Viewport.Create(doc, sh.Id, v.Id, point)
    if placed is None:
        # CanAddViewToSheet does not look at the content: Revit places nothing for an empty view
        raise ValueError("Revit did not place view '%s' on sheet %s (an empty view cannot be placed on a sheet)" % (v.Name, sh.SheetNumber))
result = {"sheet": sh.SheetNumber, "view": v.Name, "id": iid(placed.Id), "x_mm": round(point.X * MM, 1), "y_mm": round(point.Y * MM, 1)}
