# rift-tool: write
# Set type parameter values (every instance of the type changes). inputs: items [{type_id, parameter, value}], or
# items [{id, parameter, value}] with an instance id whose type is changed.
done = []
with tx("set_type_parameters"):
    for item in arg("items", required=True):
        if item.get("type_id") is not None:
            t = element(item["type_id"], "type")
        else:
            el = element(item.get("id"))
            t = doc.GetElement(el.GetTypeId())
            if t is None:
                raise ValueError("element %s has no type" % item.get("id"))
        p = find_param(t, item.get("parameter"))
        before = param_value(p).get("display")
        set_param(p, item.get("value"))
        done.append({"type_id": iid(t.Id), "type": name_of(t), "parameter": p.Definition.Name, "before": before, "after": param_value(p).get("display")})
result = {"set": done}
