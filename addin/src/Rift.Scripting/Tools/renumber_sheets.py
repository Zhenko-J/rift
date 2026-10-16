# rift-tool: write
# Renumber sheets. inputs: mapping [{from, to}] — numbers may swap: every sheet first gets a temporary number.
mapping = arg("mapping", required=True)
by_number = dict((s.SheetNumber, s) for s in collect(ViewSheet) if not s.IsTemplate)
targets = [str(m["to"]) for m in mapping]
if len(set(targets)) != len(targets):
    raise ValueError("two sheets cannot get the same number")
moving = set(str(m["from"]) for m in mapping)
for t in targets:
    if t in by_number and t not in moving:
        raise ValueError("sheet number %s is already used by a sheet that is not renumbered" % t)
for m in mapping:
    if str(m["from"]) not in by_number:
        raise ValueError("sheet %s not found" % m["from"])
with tx("renumber_sheets"):
    for i, m in enumerate(mapping):
        # only characters Revit accepts in a sheet number (no {}[]|;<>?`~)
        by_number[str(m["from"])].SheetNumber = "rift-tmp-%d-%s" % (i, m["from"])
    for m in mapping:
        by_number[str(m["from"])].SheetNumber = str(m["to"])
result = {"renumbered": [{"from": str(m["from"]), "to": str(m["to"])} for m in mapping]}
