# rift-tool: write
# Apply a view template to views (by id or name). inputs: template, views [..]
t = template(arg("template", required=True))
targets = [view(r) for r in arg("views", required=True)]
done, refused = [], []
with tx("apply_view_template"):
    for v in targets:
        try:
            if not v.IsValidViewTemplate(t.Id):
                refused.append({"view": v.Name, "reason": "template not valid for this view type"})
                continue
            v.ViewTemplateId = t.Id
            done.append(v.Name)
        except Exception as e:
            refused.append({"view": v.Name, "reason": str(e)})
result = {"template": t.Name, "applied": done, "refused": refused}
