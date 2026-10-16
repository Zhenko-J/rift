# rift-tool: write
# Create a schedule of a category with fields. inputs: category, name, fields [names of schedulable fields]
cat = category(arg("category", required=True))
name = arg("name", required=True)
if any(s.Name == name for s in collect(ViewSchedule)):
    raise ValueError("a schedule named '%s' already exists" % name)
with tx("create_schedule"):
    vs = ViewSchedule.CreateSchedule(doc, cat.Id)
    vs.Name = name
    defn = vs.Definition
    available = {}
    for sf in defn.GetSchedulableFields():
        available.setdefault(sf.GetName(doc), sf)
    missing = []
    for fname in arg("fields", []) or []:
        sf = available.get(fname)
        if sf is None:
            missing.append(fname)
            continue
        defn.AddField(sf)
    if missing:
        raise ValueError("fields not schedulable on %s: %s" % (cat.Name, ", ".join(missing)))
result = {"id": iid(vs.Id), "name": name, "category": cat.Name, "fields": [defn.GetField(i).GetName() for i in range(defn.GetFieldCount())]}
