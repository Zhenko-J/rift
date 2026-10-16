# rift-tool: write
# Rename a type. inputs: type (id or name), family (to disambiguate a name), new_name
ref = arg("type", required=True)
if arg("family"):
    f = find_one(collect(Family), arg("family"), "family")
    t = find_one([doc.GetElement(s) for s in f.GetFamilySymbolIds()], ref, "type")
elif is_id(ref):
    t = element(ref, "type")
else:
    t = find_one(FilteredElementCollector(doc).WhereElementIsElementType(), ref, "type")
old = name_of(t)
with tx("rename_family_type"):
    t.Name = arg("new_name", required=True)
result = {"id": iid(t.Id), "from": old, "to": name_of(t)}
