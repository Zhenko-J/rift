# rift-tool: write
# Set instance parameter values. inputs: items [{id, parameter, value}] — text as is, numbers in mm for lengths,
# "12 mm"-style strings through Revit's value parser, element ids for element parameters, true/false for yes/no.
done = []
with tx("set_element_parameters"):
    for item in arg("items", required=True):
        el = element(item.get("id"))
        p = find_param(el, item.get("parameter"))
        before = param_value(p).get("display")
        set_param(p, item.get("value"))
        done.append({"id": iid(el.Id), "parameter": p.Definition.Name, "before": before, "after": param_value(p).get("display")})
result = {"set": done}
