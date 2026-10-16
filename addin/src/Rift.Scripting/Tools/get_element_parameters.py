# rift-tool: read
# Instance (and type) parameters of elements. inputs: element_ids [..], include_type=true, names [..] (optional filter)
names = set(arg("names") or [])
out = []
for i in arg("element_ids", required=True):
    el = element(i)
    inst = parameters(el)
    d = {"id": iid(el.Id), "name": name_of(el), "instance": [p for p in inst if not names or p["name"] in names]}
    if arg("include_type", True):
        t = doc.GetElement(el.GetTypeId())
        if t is not None:
            d["type"] = [p for p in parameters(t) if not names or p["name"] in names]
            d["type_id"] = iid(t.Id)
    out.append(d)
result = out
