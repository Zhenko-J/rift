# rift-tool: read
# Parameters of the title block placed on a sheet (instance and type). inputs: sheet (number, name or id)
sheets = [s for s in collect(ViewSheet) if not s.IsTemplate]
ref = arg("sheet", required=True)
match = [s for s in sheets if s.SheetNumber == str(ref)]
sh = match[0] if len(match) == 1 else find_one(sheets, ref, "sheet")
tb = FilteredElementCollector(doc, sh.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement()
if tb is None:
    raise ValueError("sheet %s has no title block" % sh.SheetNumber)
result = {
    "sheet": {"id": iid(sh.Id), "number": sh.SheetNumber, "name": sh.Name},
    "titleblock": summary(tb),
    "instance": parameters(tb),
    "type": parameters(doc.GetElement(tb.GetTypeId())),
}
