# RIFT pipe transport — normative description

Cited by ADR-001 and ADR-002. Shared by the add-in (`Rift.Core/Transport`, `Rift.Core/Security`), the server (`rift_mcp/transport`) and the development host `Rift.PipeHost`. `docs/PROTOCOL.md` is the readable summary of the whole protocol; this file fixes the bytes on the pipe. Any change here is a protocol version change.

## Names and files

| Item | Value |
|---|---|
| pipe | `\\.\pipe\rift-revit-<pid>`; when that name is taken, `rift-revit-<pid>-<8 random hex>`; the name actually used is the `pipe` field of the discovery file |
| discovery file | `%LOCALAPPDATA%\rift\instances\revit-<pid>.json` (shareable metadata, ADR-003) |
| key file | `%LOCALAPPDATA%\rift\instances\revit-<pid>.key`, exactly 32 bytes, never logged, never sent |
| data folder override | environment variable `RIFT_HOME` (server side, tests) |

Discovery file fields: `pid`, `process_start_utc` (ISO 8601, UTC), `transport` (`"pipe"`), `pipe`, `revit_version`, `build`, `engine`, `addin_version`, `generation` (secret generation, starts at 1), `protocol` (`"2.0"`), `capabilities` (operations served), `written_utc`. The add-in writes both files atomically (temp file + replace), creates them with an ACL for the current user only, rewrites the discovery file at every secret rotation, purges files whose pid is dead at start-up and deletes its own at shutdown.

## Pipe instance

- Created with `NamedPipeServerStreamAcl.Create`: `PipeDirection.InOut`, `maxNumberOfServerInstances = 1`, `PipeTransmissionMode.Byte`, `PipeOptions.Asynchronous`.
- `PipeSecurity`: allow the current user's SID (`FullControl`), deny the `NETWORK` logon SID (`\\host\pipe\…` over SMB is refused by the kernel).
- One connection serves one request: read one frame, authenticate, execute or refuse, write one frame, `WaitForPipeDrain`, `Disconnect`, wait again on the same instance. The name never disappears between requests.
- A client that finds the instance busy (`ERROR_PIPE_BUSY`, surfaced as `EINVAL` by the C runtime when the pipe is opened like a file) waits with `WaitNamedPipe` for up to 2 s and then reports `TARGET_BUSY`.
- Before any frame a client checks `GetNamedPipeServerProcessId` against the discovery file's `pid` (`TARGET_CHANGED` otherwise).

## Frames

ASCII header line terminated by `\n`, at most 256 bytes, then exactly `len` bytes of body (UTF-8 JSON, at most 1 048 576 bytes, checked before any allocation).

```
request  : RIFT/1 ts=<unix seconds> nonce=<32 hex> mac=<64 hex> len=<bytes>\n<body>
response : RIFT/1 proof=<64 hex> len=<bytes>\n<body>
health   : RIFT/1 health len=0\n
         → RIFT/1 proof=- len=<bytes>\n{"v":"2.0","pid":<pid>}
```

- Header fields appear in that order, separated by single spaces; hex is lower case; a header that does not parse, a magic other than `RIFT/1`, a `len` above the limit or a body shorter than `len` closes the connection **without a response**.
- `health` is the only unsigned frame and the only one whose response has `proof=-`.
- The body of every other request is the request envelope of `docs/PROTOCOL.md`; the body of every response is the response envelope. A response body that would exceed the limit is replaced by an envelope with code `PAYLOAD_TOO_LARGE`.

## Signature

```
mac   = HMAC-SHA256(secret, "RIFT/1|" + ts + "|" + nonce + "|" + sha256_hex(body))
proof = HMAC-SHA256(secret, mac + "|" + sha256_hex(response_body))
```

- `secret`: the 32 bytes of the key file. `sha256_hex`: lower-case hex of SHA-256 over the exact bytes sent.
- Server-side checks, in order: `ts` within ±120 s of the add-in's clock (`BadTimestamp`), `mac` equal in constant time (`BadMac`), `nonce` unseen within the last 5 minutes (`ReplayedNonce`). The nonce is recorded only after a valid `mac`.
- A refused frame is answered with status `error`, code `UNAUTHORIZED`, message = the reason above, signed with the current secret; nothing is executed. Ten consecutive refusals (a success resets the count) rotate the secret and increment `generation`.
- The client verifies `proof` before reading the body (`TARGET_CHANGED` when it fails); after `UNAUTHORIZED` or a failed proof it re-reads the discovery and key files once and retries with a fresh nonce if the secret changed.
- Test vectors: `shared/schemas/test-vectors/hmac.json` (`tools/gen_hmac_vectors.py`), checked by `HmacSignerTests` and `test_signing.py`.
