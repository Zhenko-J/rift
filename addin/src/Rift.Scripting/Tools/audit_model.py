# rift-tool: read
# Model health report: warnings, unused view templates and filters, views not on sheets, unplaced rooms, in-place
# families, imported CAD, purgeable elements (Revit 2024+), and names that break simple conventions.
all_views = list(collect(View))
templates = [v for v in all_views if v.IsTemplate]
used_templates = set(iid(v.ViewTemplateId) for v in all_views if not v.IsTemplate)
used_filters = set()
for v in all_views:
    try:
        for f in v.GetFilters():
            used_filters.add(iid(f))
    except Exception:
        pass
placed = set(iid(vp.ViewId) for vp in collect(Viewport)) | set(iid(s.ScheduleId) for s in collect(ScheduleSheetInstance))
not_on_sheet = [v.Name for v in all_views if not v.IsTemplate and not isinstance(v, ViewSheet) and v.CanBePrinted
                and v.ViewType not in (ViewType.ProjectBrowser, ViewType.SystemBrowser, ViewType.Internal, ViewType.Undefined)
                and iid(v.Id) not in placed]
rooms = list(FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
report = {
    "warnings": len(doc.GetWarnings()),
    "unused_view_templates": sorted([t.Name for t in templates if iid(t.Id) not in used_templates], key=str),
    "unused_filters": sorted([name_of(f) for f in collect(FilterElement) if iid(f.Id) not in used_filters], key=str),
    "views_not_on_sheets": len(not_on_sheet),
    "views_not_on_sheets_sample": sorted(not_on_sheet, key=str)[:50],
    "unplaced_rooms": len([r for r in rooms if r.Location is None]),
    "in_place_families": sorted([f.Name for f in collect(Family) if f.IsInPlace], key=str),
    "imported_cad": len([i for i in collect(ImportInstance) if not i.IsLinked]),
}
try:
    report["purgeable"] = doc.GetUnusedElements(System.Collections.Generic.HashSet[ElementId]()).Count
except Exception:
    report["purgeable"] = None
dup = {}
for v in templates:
    key = re.sub(r"[\s_\-]+", " ", v.Name.strip().lower())
    dup.setdefault(key, []).append(v.Name)
report["near_duplicate_template_names"] = [names for names in dup.values() if len(names) > 1]
report["names_with_double_spaces"] = sorted([e.Name for e in list(templates) + list(collect(Material)) if "  " in e.Name], key=str)[:50]
result = report
