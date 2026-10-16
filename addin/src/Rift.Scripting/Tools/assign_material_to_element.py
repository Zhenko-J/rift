# rift-tool: write
# Assign a material to elements through a material parameter. inputs: element_ids [..], material, parameter (default:
# the element's "Material" parameter, or its type's when the instance has none)
m = find_one(collect(Material), arg("material", required=True), "material")
pname = arg("parameter")
done, refused = [], []
with tx("assign_material_to_element"):
    for i in arg("element_ids", required=True):
        el = element(i)
        targets = [el, doc.GetElement(el.GetTypeId())]
        p = None
        for t in targets:
            if t is None:
                continue
            p = t.LookupParameter(pname) if pname else (t.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM) or t.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM))
            if p is not None and not p.IsReadOnly and p.StorageType == StorageType.ElementId:
                break
            p = None
        if p is None:
            refused.append({"id": iid(el.Id), "reason": "no writable material parameter"})
            continue
        p.Set(m.Id)
        if t.Id == el.Id:
            done.append({"id": iid(el.Id), "on": "instance"})
        else:
            # the type's parameter changes every instance of that type, not only this element
            done.append({"id": iid(el.Id), "on": "type", "type_id": iid(t.Id), "type": t.Name})
on_types = sorted(set(d["type"] for d in done if d["on"] == "type"))
result = {"material": m.Name, "assigned": done, "refused": refused, "types_changed": on_types}
