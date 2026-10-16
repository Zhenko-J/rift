# rift-tool: write
# Duplicate a view template under a new name. inputs: template, new_name
t = template(arg("template", required=True))
new_name = arg("new_name", required=True)
if any(v.Name == new_name for v in views(True)):
    raise ValueError("a view template named '%s' already exists" % new_name)
with tx("duplicate_view_template"):
    ids = ElementTransformUtils.CopyElements(doc, id_list([t.Id]), doc, Transform.Identity, CopyPasteOptions())
    copy = doc.GetElement(list(ids)[0])
    copy.Name = new_name
result = {"source": t.Name, "id": iid(copy.Id), "name": copy.Name}
