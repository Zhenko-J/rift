# J0 prototypes (.NET add-in)

Feasibility checks that settle the open points of ADR-001, 016, 021 and 022 before milestone M1. Results are consigned under **Evidence** in `docs/DECISIONS.md`.

| Id | Runs | Proves |
|---|---|---|
| P-D1 | console (`PipeProbe serve`) | named pipe created with an explicit `PipeSecurity` (allow current user, deny NETWORK); effective ACL printed |
| P-D2 | console (`PipeProbe serve`) | a second server of the same name is refused (`maxNumberOfServerInstances = 1`); `Disconnect` + `WaitForConnection` reuse the instance |
| P-D3 | `pd3_pipe_client.py` against `PipeProbe serve` | the real CPython client: `open(r'\\.\pipe\…','r+b')`, `GetNamedPipeServerProcessId`, `health` frame, signed request, proof verification, 1 MiB body |
| P-D4 | inside Revit (self-test command, M1) | IronPython 3.4 hosted in the isolated context with pyRevit loaded |
| P-D5 | console (later) | Roslyn scripting figures (v1.x decision) |
| P-D6 | inside Revit (ribbon → Status; today the `RIFT` panel of the `NORN` tab) | isolated `AssemblyLoadContext`, type identity of the Revit API, Status button |
| P-D7 | inside Revit | unsigned-publisher warning asked once per (path, AddInId) |
| P-D8 | console (`PipeProbe httplistener`) | `HttpListener` on `127.0.0.1`/`localhost` without an http.sys reservation (expected: access denied → fallback is `TcpListener`) |

```powershell
dotnet run --project prototypes/PipeProbe -c Release -- serve rift-probe <secret-hex>   # P-D1, P-D2
uv run --project server python prototypes/pd3_pipe_client.py rift-probe <secret-hex>     # P-D3
dotnet run --project prototypes/PipeProbe -c Release -- httplistener                       # P-D8
```
