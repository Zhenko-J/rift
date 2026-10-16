# rift-tool: read
# Model warnings grouped by description, with counts and a few element ids each. inputs: sample=5
groups = {}
for w in doc.GetWarnings():
    key = w.GetDescriptionText()
    g = groups.setdefault(key, {"description": key, "severity": str(w.GetSeverity()), "count": 0, "elements": []})
    g["count"] += 1
    if len(g["elements"]) < int(arg("sample", 5)):
        g["elements"].append([iid(i) for i in w.GetFailingElements()])
result = {"total": sum(g["count"] for g in groups.values()), "groups": sorted(groups.values(), key=lambda g: -g["count"])}
