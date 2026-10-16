# rift-tool: write
# Create a sheet. inputs: number, name, titleblock (type name or id; default: the most used title block type)
number = str(arg("number", required=True))
if any(s.SheetNumber == number for s in collect(ViewSheet)):
    raise ValueError("sheet number %s already exists" % number)
types = list(FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType())
if arg("titleblock"):
    tb = find_one(types, arg("titleblock"), "title block type")
    tb_id = tb.Id
elif types:
    counts = {}
    for i in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType():
        counts[iid(i.GetTypeId())] = counts.get(iid(i.GetTypeId()), 0) + 1
    best = max(types, key=lambda t: counts.get(iid(t.Id), 0))
    tb_id = best.Id
else:
    tb_id = ElementId.InvalidElementId
with tx("create_sheet"):
    sh = ViewSheet.Create(doc, tb_id)
    sh.SheetNumber = number
    sh.Name = arg("name", required=True)
result = {"id": iid(sh.Id), "number": sh.SheetNumber, "name": sh.Name, "titleblock_type": iid(tb_id)}
