# rift-tool: write
# Duplicate a type (family type or system type such as a wall type). inputs: type (id or name), family (to
# disambiguate a name), new_name
ref = arg("type", required=True)
if arg("family"):
    f = find_one(collect(Family), arg("family"), "family")
    t = find_one([doc.GetElement(s) for s in f.GetFamilySymbolIds()], ref, "type")
elif is_id(ref):
    t = element(ref, "type")
else:
    t = find_one(FilteredElementCollector(doc).WhereElementIsElementType(), ref, "type")
new_name = arg("new_name", required=True)
with tx("duplicate_family_type"):
    copy = t.Duplicate(new_name)
result = {"source": name_of(t), "id": iid(copy.Id), "name": name_of(copy)}
