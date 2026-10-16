# rift-tool: read
# The rows of a schedule as displayed (text of each cell). inputs: schedule, limit=500
vs = find_one([s for s in collect(ViewSchedule) if not s.IsTemplate], arg("schedule", required=True), "schedule")
limit = int(arg("limit", 500))
body = vs.GetTableData().GetSectionData(SectionType.Body)
rows = []
for r in range(body.NumberOfRows):
    if len(rows) >= limit:
        break
    rows.append([vs.GetCellText(SectionType.Body, r, c) for c in range(body.NumberOfColumns)])
header = rows[0] if rows else []
result = {"schedule": vs.Name, "columns": header, "rows": rows[1:], "total_rows": body.NumberOfRows, "truncated": body.NumberOfRows > limit}
