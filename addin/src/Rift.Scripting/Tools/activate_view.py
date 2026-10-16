# rift-tool: read
# Asks Revit to make a view active; Revit switches once the call has returned (the model does not change). inputs: view
if uidoc is None:
    raise ValueError("no document window is active")
v = view(arg("view", required=True))
if v.ViewType in (ViewType.Internal, ViewType.Undefined, ViewType.ProjectBrowser, ViewType.SystemBrowser):
    raise ValueError("view '%s' cannot be shown in a window" % name_of(v))
uidoc.RequestViewChange(v)
result = {"view": name_of(v), "id": iid(v.Id), "type": str(v.ViewType), "requested": True,
          "note": "Revit switches to the view after this call returns"}
