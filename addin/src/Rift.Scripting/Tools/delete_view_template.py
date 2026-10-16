# rift-tool: write
# Delete a view template; views that used it lose it. inputs: template
t = template(arg("template", required=True))
using = [v.Name for v in views(False) if iid(v.ViewTemplateId) == iid(t.Id)]
name = t.Name
with tx("delete_view_template"):
    deleted = doc.Delete(t.Id)
result = {"deleted": name, "views_that_used_it": sorted(using, key=str), "elements_deleted": deleted.Count}
