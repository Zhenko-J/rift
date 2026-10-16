# rift-tool: write
# Delete elements (and what Revit deletes with them, which the result lists). inputs: element_ids [..]
asked = [element(i) for i in arg("element_ids", required=True)]
names = dict((iid(e.Id), summary(e)) for e in asked)
with tx("delete_elements"):
    deleted = doc.Delete(id_list([e.Id for e in asked]))
dependents = [iid(i) for i in deleted if iid(i) not in names]
result = {"deleted": list(names.values()), "also_deleted": len(dependents), "also_deleted_ids": dependents[:200]}
