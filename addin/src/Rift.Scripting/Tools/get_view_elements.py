# rift-tool: read
# Elements visible in a view, counted by category, with a sample. inputs: view (default active), category (optional), limit=200
v = view(arg("view"))
col = FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType()
if arg("category"):
    col = col.OfCategoryId(category(arg("category")).Id)
limit = int(arg("limit", 200))
by_cat = {}
sample = []
for el in col:
    c = cat_name(el) or "(no category)"
    by_cat[c] = by_cat.get(c, 0) + 1
    if len(sample) < limit:
        sample.append(summary(el))
result = {"view": v.Name, "by_category": dict(sorted(by_cat.items())), "total": sum(by_cat.values()), "elements": sample}
