# rift-tool: write
# Remove element graphic overrides in a view: inputs.element_ids, or every element of the view when absent.
v = view(arg("view"))
ids = arg("element_ids")
if not ids:
    ids = [iid(e.Id) for e in FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType()]
blank = OverrideGraphicSettings()
with tx("clear_element_overrides"):
    for i in ids:
        v.SetElementOverrides(to_id(i), blank)
result = {"view": v.Name, "elements": len(ids)}
