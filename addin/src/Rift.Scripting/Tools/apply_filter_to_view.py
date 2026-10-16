# rift-tool: write
# Add a filter to a view or a view template. inputs: filter, view (or template), visible=true
f = find_one(collect(FilterElement), arg("filter", required=True), "filter")
ref = arg("view", required=True)
try:
    target = view(ref)
except ValueError:
    target = template(ref)
with tx("apply_filter_to_view"):
    if f.Id not in list(target.GetFilters()):
        target.AddFilter(f.Id)
    target.SetFilterVisibility(f.Id, bool(arg("visible", True)))
result = {"filter": name_of(f), "view": target.Name, "template": target.IsTemplate, "visible": bool(arg("visible", True))}
