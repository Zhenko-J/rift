# rift-tool: write
# Add fields to a schedule. inputs: schedule, fields [names], hidden=false
vs = find_one([s for s in collect(ViewSchedule) if not s.IsTemplate], arg("schedule", required=True), "schedule")
defn = vs.Definition
present = set(defn.GetField(i).GetName() for i in range(defn.GetFieldCount()))
available = {}
for sf in defn.GetSchedulableFields():
    available.setdefault(sf.GetName(doc), sf)
added, skipped = [], []
with tx("add_schedule_field"):
    for fname in arg("fields", required=True):
        if fname in present:
            skipped.append(fname)
            continue
        sf = available.get(fname)
        if sf is None:
            raise ValueError("field '%s' is not schedulable on this schedule" % fname)
        f = defn.AddField(sf)
        f.IsHidden = bool(arg("hidden", False))
        added.append(fname)
result = {"schedule": vs.Name, "added": added, "already_present": skipped}
