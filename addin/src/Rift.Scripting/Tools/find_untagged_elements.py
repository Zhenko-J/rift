# rift-tool: read
# Elements of a category visible in a view that no tag in that view points to. inputs: view (default active), category,
# limit=500
v = view(arg("view"))
cat = category(arg("category", required=True))
tagged = set()
for tag in FilteredElementCollector(doc, v.Id).OfClass(IndependentTag):
    try:
        for i in tag.GetTaggedLocalElementIds():
            tagged.add(iid(i))
    except Exception:
        try:
            tagged.add(iid(tag.TaggedLocalElementId))
        except Exception:
            pass
for tag in FilteredElementCollector(doc, v.Id).OfClass(SpatialElementTag):
    try:
        if isinstance(tag, Architecture.RoomTag):
            tagged.add(iid(tag.TaggedLocalRoomId))
        elif isinstance(tag, AreaTag):
            tagged.add(iid(tag.Area.Id))
    except Exception:
        pass
limit = int(arg("limit", 500))
out = []
total = 0
for el in FilteredElementCollector(doc, v.Id).OfCategoryId(cat.Id).WhereElementIsNotElementType():
    if iid(el.Id) in tagged:
        continue
    total += 1
    if len(out) < limit:
        out.append(summary(el))
result = {"view": v.Name, "category": cat.Name, "untagged": total, "elements": out, "truncated": total > limit}
