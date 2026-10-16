# rift-tool: write
# Purge unused elements (Revit 2024+ Document.GetUnusedElements). A dry run (the default of every write) lists what would
# go in the receipt; keep it to delete. inputs: categories [..] (optional: only these), limit_listed=200
try:
    unused = doc.GetUnusedElements(System.Collections.Generic.HashSet[ElementId]())
except Exception as e:
    raise ValueError("this Revit version cannot list unused elements: %s" % e)
wanted = set(iid(category(c).Id) for c in (arg("categories") or []))
targets = []
for i in unused:
    el = doc.GetElement(i)
    if el is None:
        continue
    if wanted:
        c = el.Category
        if c is None or iid(c.Id) not in wanted:
            continue
    targets.append(el)
listed = [summary(e) for e in targets[: int(arg("limit_listed", 200))]]
deleted = 0
with tx("purge_unused"):
    for el in targets:
        try:
            if el.IsValidObject:
                deleted += doc.Delete(el.Id).Count
        except Exception:
            pass
result = {"candidates": len(targets), "deleted_with_dependents": deleted, "listed": listed}
