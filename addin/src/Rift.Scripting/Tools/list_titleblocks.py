# rift-tool: read
# Title block families and types, with the sheet size in mm and how many sheets use each type.
use = {}
for tb in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType():
    use[iid(tb.GetTypeId())] = use.get(iid(tb.GetTypeId()), 0) + 1
out = []
for t in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType():
    d = {"id": iid(t.Id), "family": getattr(t, "FamilyName", None), "type": name_of(t), "sheets": use.get(iid(t.Id), 0)}
    for key, bip in (("width_mm", BuiltInParameter.SHEET_WIDTH), ("height_mm", BuiltInParameter.SHEET_HEIGHT)):
        p = t.get_Parameter(bip)
        if p is not None:
            d[key] = round(p.AsDouble() * MM, 1)
    out.append(d)
result = sorted(out, key=lambda x: (str(x["family"]), str(x["type"])))
