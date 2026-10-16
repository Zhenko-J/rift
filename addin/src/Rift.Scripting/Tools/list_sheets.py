# rift-tool: read
# Sheets with number, name, title block and the views and schedules placed on them.
out = []
for sh in collect(ViewSheet):
    if sh.IsTemplate:
        continue
    d = {"id": iid(sh.Id), "number": sh.SheetNumber, "name": sh.Name, "placeholder": sh.IsPlaceholder}
    tb = FilteredElementCollector(doc, sh.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement()
    if tb is not None:
        d["titleblock"] = {"id": iid(tb.Id), "type": name_of(doc.GetElement(tb.GetTypeId()))}
    placed = []
    for vid in sh.GetAllPlacedViews():
        v = doc.GetElement(vid)
        placed.append({"id": iid(vid), "name": v.Name, "view_type": str(v.ViewType)})
    d["views"] = sorted(placed, key=lambda x: str(x["name"]))
    d["schedules"] = sorted([doc.GetElement(si.ScheduleId).Name for si in FilteredElementCollector(doc, sh.Id).OfClass(ScheduleSheetInstance) if not si.IsTitleblockRevisionSchedule], key=str)
    out.append(d)
result = sorted(out, key=lambda x: str(x["number"]))
