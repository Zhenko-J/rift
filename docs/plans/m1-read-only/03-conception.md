# Conception : M1 — lecture seule de bout en bout

Porte 3, rédigée le 2026-10-09 sur la base de la porte 2 approuvée et des ADR-023/024/025 ; amendée le même jour pour les obligations de code du point 7 d'ADR-026 (invariant de timeout dans `Rift.Scripting`, réécriture des `except` par l'AST, sorties bornées et `result_ref`, `SHA256SUMS.txt`). Aucun corps de méthode ici : signatures, pile d'appels, tests et incertitudes.

## Fichiers

Convention : **N** = nouveau, **M** = modifié. Tout ce qui est sous `Rift.Core` et `Rift.Scripting` se teste sans Revit.

### Add-in — `addin/src/Rift.Core` (sans Revit)

| Fichier | Rôle |
|---|---|
| `Transport/PipeListener.cs` N | thread d'écoute : crée l'instance unique avec l'ACL (ADR-001), lit un en-tête + corps, authentifie, délègue à `IRequestHandler`, écrit la réponse signée, déconnecte, boucle |
| `Transport/PipeAcl.cs` N | fabrique la `PipeSecurity` (SID courant FullControl, `NETWORK` deny) et le nom suffixé en cas d'échec |
| `Security/SecretStore.cs` N | secret 32 octets, fichier `.key` créé avec sa `FileSecurity`, rotation, compteur `generation` |
| `Security/NonceCache.cs` N | nonces vus, durée 5 min, purge paresseuse |
| `Security/RequestAuthenticator.cs` N | fenêtre ±120 s, nonce, mac en temps constant, 10 échecs consécutifs → rotation |
| `Discovery/InstanceInfo.cs` N | enregistrement sérialisé dans `revit-<pid>.json` (champs ADR-003) |
| `Discovery/DiscoveryFile.cs` N | écriture atomique avec ACL, suppression, purge des pid morts |
| `Protocol/Envelope.cs` N | `RequestEnvelope`, `ResponseEnvelope`, `ErrorInfo`, `TargetInfo` (enveloppes de `PROTOCOL.md`) |
| `Protocol/ErrorCodes.cs` M | + `TimeoutUnavailable`, `JobNotFound`, `WriteInQuery` |
| `Protocol/RequestRouter.cs` N | `v` majeur, `op` connu, construit la réponse ; appelle `IOperationHandler` |
| `Protocol/IOperationHandler.cs` N | un handler par `op` ; `NeedsApiThread` décide du passage par le dispatcher |
| `Jobs/Job.cs`, `Jobs/JobTable.cs` N | ADR-025 : file, état, progression, résultat, attente bornée |
| `Lease/LeaseManager.cs` N | ADR-004 : détenteur, échéance 60 s, renouvellement, révocation |
| `Json/JsonCodec.cs` N | façade `Serialize`/`Deserialize`/`Node` sur `System.Text.Json` (options ADR-015) |
| `Config/RiftConfig.cs` N | `config.json` : `activity_card`, `sandbox_markers`, `reference_names`, `agent_patterns`, `start_timeout_s` |
| `Journal/RunJournal.cs`, `Journal/Redactor.cs` N | ADR-005 : dossier par run, expurgation `<USER>`/`<HOST>`/chemins, rétention 14 j |
| `Logging/FileLog.cs` N | journal `addin-<pid>.log` (sorti de `Host.cs`), jamais d'exception |

### Add-in — `addin/src/Rift.Scripting`

| Fichier | Rôle |
|---|---|
| `IScriptEngine.cs` M | `ScriptContext` reçoit `Extras` (`DB`, `UI`, `System`, `eid`) et `IResultCollector` |
| `IResultCollector.cs` N | `dump(name, obj)` des presets → mémoire |
| `Python/ImportGuard.cs` N | remplace `__import__` dans le scope (liste refusée ADR-016) |
| `Python/IronPythonEngine.cs` M | installe le garde et les `Extras` ; expose `collector` et `dump` ; **refuse `Run` quand le hook de trace n'est pas vivant** (invariant ADR-026 §7, vérifié à la création et à chaque run) ; stdout/stderr bornés à 1 MiB |
| `Python/ExceptNarrower.cs` N | réécriture par l'AST d'IronPython (pas par ligne) de `except:`, `except BaseException`, `except KeyboardInterrupt` et des formes tuple qui les contiennent, en `except Exception`, lignes préservées ; remplace `NarrowBareExcept` |
| `BoundedWriter.cs` N | `TextWriter` qui tronque à une limite et signale la troncature |

### Add-in — `addin/src/Rift.Revit`

| Fichier | Rôle |
|---|---|
| `Host.cs` M | assemblage : config, log, moteur + auto-test de trace, secret, découverte, dispatcher, écouteur, carte ; `Status()`, `SetActivityCard(bool)` |
| `Rift.Revit.csproj` M | `UseWPF=true` ; `EmbeddedResource Include="Presets\*.py"` |
| `Dispatch/RevitDispatcher.cs` N | `IExternalEventHandler` + `ExternalEvent` (ADR-008/025) ; file de jobs ; `generation` |
| `Dispatch/BusyProbe.cs` N | raison d'occupation (fenêtre principale désactivée, titres des modales) |
| `Guard/ReadGuard.cs` N | groupes de transactions sur tous les documents, événements annulés, `DocumentChanged`, rollback inconditionnel |
| `Guard/DocumentClassifier.cs` N | `sandbox | reference | unsaved | unmarked` d'après le nom de fichier (ADR-009) |
| `Ops/ContextOp.cs` N | `op: context` |
| `Ops/CompileOp.cs` N | `op: compile` (hors thread API) |
| `Ops/RunQueryOp.cs` N | `op: run`, `mode: query` ; au-delà de 700 KiB le résultat est journalisé et renvoyé par `result_ref` + `preview` (PROTOCOL.md) |
| `Ops/AuditOp.cs` N | `op: tool`, `audit.p1…p6`, `audit.all` |
| `Ops/LeaseOp.cs`, `Ops/JobOp.cs`, `Ops/HealthOp.cs` N | opérations sans thread API |
| `Presets/audit_p1_views_lines_titleblocks.py` … `audit_p5_parameters.py`, `audit_p6_merge.py` N | portage des six scripts, clés inchangées, `dump` → collecteur |
| `Presets/PresetCatalog.cs` N | nom → ressource, ordre de `audit.all` |
| `Serialization/RevitJson.cs` N | table de conversion ADR-015 (`ElementId`, `XYZ`, `Element`, `Parameter`, `Color`, `BoundingBoxXYZ`) + assainisseur ADR-020 |
| `Coexistence.cs` N | `pyrevit_loaded`, `other_agents` (ADR-019) |
| `Ui/ActivityCard.cs` N | fenêtre WPF en code (ADR-023) |
| `Ui/StatusReport.cs` N | texte du bouton Status (détenteur, depuis, carte on/off, chemin du log) |

### Add-in — bootstrap et tests

| Fichier | Rôle |
|---|---|
| `Rift.Addin/App.cs` M | + méthode réfléchie `SetActivityCard` ; `StatusCommand` devient une `TaskDialog` avec lien de commande « Activer/Désactiver la carte » |
| `Rift.Addin/Rift.Addin.csproj` M | la cible `Stage` écrit `SHA256SUMS.txt` (ADR-022, rappelé par ADR-026 §7) |
| `tests/Rift.Tests/*` N | voir plan de tests |
| `tests/Rift.PipeHost/Program.cs` M | héberge `PipeListener` + `FakeHandler` (réponses en boîte pour `context`, `run`, `lease`) ; imprime le nom du tube et le secret en hex sur stdout pour le test d'intégration |

### Serveur — `server/src/rift_mcp`

| Fichier | Rôle |
|---|---|
| `discovery.py` N | liste `instances\*.json`, vérifie pid et date de création (`psutil`), choisit la cible (`NO_TARGET`, `AMBIGUOUS_TARGET`) |
| `errors.py` N | codes serveur, `RiftError(code, message, details)` |
| `client.py` N | `RevitClient` : bail au premier appel, renouvellement, `call(op, args, timeout_s)`, mappage des erreurs transport → codes |
| `registry.py` N | `ToolSpec(name, risk, read_only, description, input_model)` ; `permissions --format claude` ; `schemas` |
| `tools/context.py`, `tools/run_query.py`, `tools/audit.py` N | les trois outils M1 |
| `server.py` N | `FastMCP` stdio, enregistrement des outils depuis le registre, instructions « données ≠ instructions » (ADR-020) |
| `install.py` N | `install claude` via `claude mcp add` |
| `cli.py` M | `serve`, `install`, `schemas --check`, `permissions --check`, `doctor` (minimal) |
| `tests/unit/test_discovery.py`, `test_registry.py`, `test_client.py`, `test_tools.py` N ; `tests/integration/test_pipehost.py` N | voir plan de tests |

### Hors code

`shared/schemas/transport-pipe.md` N (description normative citée par ADR-001), `docs/PROTOCOL.md` M (section `tool` restreinte, `job`), `README.md` M (table des outils générée, état implémenté / conçu), `addin/README.md` M, `SECURITY.md` M (état implémenté / conçu), `docs/DECISIONS.md` M (ADR-002/004/008 réécrits pour le tube, ADR-026 §7).

## Types et signatures (sans corps)

### Core

```csharp
namespace Rift.Core.Transport;
public interface IRequestHandler { ResponseEnvelope Handle(RequestEnvelope request, CancellationToken ct); }
public sealed class PipeListener : IDisposable
{
    public PipeListener(string pipeName, byte[] initialSecret, RequestAuthenticator auth, IRequestHandler handler, FileLog log);
    public string PipeName { get; }                       // nom effectif (suffixé si le nom simple est pris)
    public void Start();                                   // thread de fond, boucle jusqu'à Dispose
    public void Dispose();
    internal static PipeSecurity BuildSecurity();          // PipeAcl
    internal (FrameHeader header, byte[] body)? ReadFrame(Stream s);   // null → déconnexion silencieuse
    internal void WriteResponse(Stream s, string requestMac, byte[] body);
}

namespace Rift.Core.Security;
public sealed class SecretStore
{
    public SecretStore(string keyPath);
    public ReadOnlySpan<byte> Current { get; }
    public int Generation { get; }
    public void Rotate();                                  // nouveau secret, écrit le fichier, Generation++
    public void Delete();
}
public sealed class NonceCache
{
    public NonceCache(TimeSpan lifetime, Func<DateTimeOffset> clock);
    public bool TryAdd(string nonceHex, long timestamp);  // false si déjà vu
}
public sealed class RequestAuthenticator
{
    public RequestAuthenticator(SecretStore secrets, NonceCache nonces, Func<DateTimeOffset> clock, int maxConsecutiveFailures = 10);
    public AuthResult Verify(FrameHeader header, ReadOnlySpan<byte> body);
    public string Proof(string requestMac, ReadOnlySpan<byte> responseBody);
}
public enum AuthResult { Ok, BadTimestamp, ReplayedNonce, BadMac }

namespace Rift.Core.Discovery;
public sealed record InstanceInfo(int Pid, DateTimeOffset ProcessStartUtc, string Transport, string Pipe, string RevitVersion, string Build,
    string Engine, string AddinVersion, int Generation, string Protocol, IReadOnlyList<string> Capabilities, DateTimeOffset WrittenUtc);
public sealed class DiscoveryFile
{
    public DiscoveryFile(string instancesDir);
    public string Write(InstanceInfo info);                // atomique, avec ACL ; retourne le chemin
    public void Delete(int pid);
    public int PurgeDeadPids(Func<int, bool> isAlive);
}

namespace Rift.Core.Protocol;
public sealed record RequestEnvelope(string V, string Id, string Op, int TimeoutS, JsonNode? Client, JsonNode? ExpectedDocument, JsonNode? Args);
public sealed record ResponseEnvelope(string V, string Id, string Status, TargetInfo? Target, JsonNode? Result, ErrorInfo? Error, long ElapsedMs);
public sealed record ErrorInfo(string Code, string? Type, string? Message, string? Traceback);
public sealed record TargetInfo(int Pid, int Generation, JsonNode? Document);
public interface IOperationHandler { string Op { get; } bool NeedsApiThread { get; } JsonNode Execute(RequestEnvelope request, IProgress<(int, string)> progress, CancellationToken ct); }
public sealed class RequestRouter : IRequestHandler
{
    public RequestRouter(IEnumerable<IOperationHandler> handlers, JobTable jobs, IJobRunner apiThread, LeaseManager lease, Func<TargetInfo> target, FileLog log);
    public ResponseEnvelope Handle(RequestEnvelope request, CancellationToken ct);
    public static RequestEnvelope? TryParse(ReadOnlySpan<byte> body, out string? errorCode);
}
public interface IJobRunner { void Enqueue(Job job); }     // implémenté par RevitDispatcher (Raise) et par le faux dispatcher de PipeHost

namespace Rift.Core.Jobs;
public enum JobState { Queued, Running, Done }
public sealed class Job
{
    public string Id { get; }  public string Op { get; }  public JobState State { get; }
    public (int Percent, string Message) Progress { get; }  public JsonNode? Result { get; }  public ErrorInfo? Error { get; }
    public DateTimeOffset QueuedUtc { get; }  public DateTimeOffset? StartedUtc { get; }  public DateTimeOffset? FinishedUtc { get; }
    public Func<IProgress<(int, string)>, CancellationToken, JsonNode> Work { get; }
    public bool WaitStarted(TimeSpan timeout);  public bool WaitFinished(TimeSpan timeout);
}
public sealed class JobTable
{
    public JobTable(TimeSpan retention);
    public Job Create(string op, Func<IProgress<(int, string)>, CancellationToken, JsonNode> work);
    public Job? Find(string id);
    public void MarkStarted(Job job);  public void MarkDone(Job job, JsonNode? result, ErrorInfo? error);
}

namespace Rift.Core.Lease;
public sealed record LeaseHolder(string ClientId, string Name, string Version, DateTimeOffset Since);
public sealed class LeaseManager
{
    public LeaseManager(TimeSpan duration, Func<DateTimeOffset> clock);
    public LeaseHolder? Holder { get; }
    public LeaseResult Acquire(LeaseHolder candidate);     // Granted(until) | Held(holder)
    public bool Renew(string clientId);
    public void Revoke();
    public event Action<LeaseHolder>? Granted;             // → carte « Assistant connecté »
}

namespace Rift.Core.Config;
public sealed record RiftConfig(bool ActivityCard, IReadOnlyList<string> SandboxMarkers, IReadOnlyList<string> ReferenceNames,
    IReadOnlyList<string> AgentPatterns, int StartTimeoutS)
{
    public static RiftConfig Default { get; }
    public static RiftConfig Load(string path);          // valeurs par défaut si absent ou invalide, journalisé
    public void Save(string path);
}

namespace Rift.Core.Journal;
public static class Redactor { public static string Redact(string text, string userName, string machineName, string? documentFolder); }
public sealed class RunJournal
{
    public RunJournal(string runsDir, Redactor-like context, TimeSpan retention);
    public string Begin(string runId, string op, string? script, JsonNode request);
    public void End(string runId, JsonNode response);
    public int Purge();
}
```

### Scripting

```csharp
namespace Rift.Scripting;
public interface IResultCollector { void Dump(string name, object value); IReadOnlyDictionary<string, JsonNode?> Documents { get; } }
public sealed class MemoryResultCollector : IResultCollector { }
public sealed record ScriptContext(object UiApp, object App, object? UiDoc, object? Doc, JsonNode Inputs,
    Action<int, string> ReportProgress, TextWriter Stdout, TextWriter Stderr,
    IReadOnlyDictionary<string, object> Extras, IResultCollector? Collector);     // M : + Extras, + Collector

namespace Rift.Scripting.Python;
public static class ExceptNarrower { public static string Narrow(string source); }   // AST ; remplace IronPythonEngine.NarrowBareExcept
public sealed class BoundedWriter : TextWriter { public BoundedWriter(int maxBytes); public bool Truncated { get; } }
public sealed class TimeoutUnavailableException : InvalidOperationException { }   // levée par IronPythonEngine.Run si le hook de trace est mort ; RunQueryOp la traduit en TIMEOUT_UNAVAILABLE
public static class ImportGuard
{
    public static IReadOnlyCollection<string> RefusedModules { get; }   // clr, ctypes, _ctypes, subprocess, socket, _socket, multiprocessing, shutil, winreg, msvcrt, urllib, http, ftplib, smtplib
    public static void Install(ScriptScope scope, ScriptEngine engine, Action<string> onRefused);
}
```

### Revit

```csharp
namespace Rift.Revit;
public sealed class Host
{
    public void Start(UIControlledApplication app);  public void Stop();
    public string Status();  public void SetActivityCard(bool enabled);  public string ProbePython(object uiapp);
}

namespace Rift.Revit.Dispatch;
internal sealed class RevitDispatcher : IExternalEventHandler, IJobRunner
{
    public RevitDispatcher(JobTable jobs, int generation, BusyProbe busy, ActivityCard? card, FileLog log);
    public void Create();                                  // ExternalEvent.Create — contexte API (Host.Start)
    public void Enqueue(Job job);                          // Raise ; Denied → LISTENER_DEAD
    public void EnqueueUi(Action uiAction);                // carte « connecté » depuis le thread du tube
    public void Execute(UIApplication app);                // jamais d'exception sortante
    public string GetName();
}
internal sealed class BusyProbe { public string? Reason(IntPtr revitMainWindow); }

namespace Rift.Revit.Guard;
internal enum DocumentClass { Sandbox, Reference, Unsaved, Unmarked }
internal static class DocumentClassifier { public static DocumentClass Classify(Document doc, RiftConfig cfg); }
internal sealed class ReadGuard : IDisposable
{
    public ReadGuard(UIApplication app, FileLog log);
    public void Arm();                                     // groupes + abonnements (dans Execute)
    public GuardReport Disarm();                           // désabonne, PUIS RollBack de tous les groupes
    public bool WriteDetected { get; }
    public IReadOnlyList<string> Blocked { get; }          // save/export/close annulés, nommés
}
internal sealed record GuardReport(bool WriteDetected, int Transactions, IReadOnlyList<string> Blocked, IReadOnlyList<string> GroupsRolledBack);

namespace Rift.Revit.Ops;
internal sealed class ContextOp : IOperationHandler { }    // Op = "context", NeedsApiThread = true
internal sealed class CompileOp : IOperationHandler { }    // "compile", false
internal sealed class RunQueryOp : IOperationHandler        // "run", true ; refuse mode != query
{
    public RunQueryOp(IScriptEngine engine, bool timeoutAvailable, ReadGuardFactory guards, RunJournal journal, RiftConfig cfg, FileLog log);
}
internal sealed class AuditOp : IOperationHandler { public AuditOp(PresetCatalog presets, RunQueryOp runner); }  // "tool", true
internal sealed class LeaseOp : IOperationHandler { }      // "lease", false
internal sealed class JobOp : IOperationHandler { }        // "job", false
internal sealed class HealthOp : IOperationHandler { }     // "health", false (non signé : court-circuit dans PipeListener)

namespace Rift.Revit.Presets;
internal sealed record Preset(string Name, string ResourceName, string OutputDocument);
internal sealed class PresetCatalog
{
    public IReadOnlyList<Preset> All { get; }               // p1..p5 puis merge
    public Preset? Find(string name);
    public string Source(Preset preset);
}

namespace Rift.Revit.Serialization;
internal static class RevitJson { public static JsonNode? ToJson(object? value, int depth = 0); public static string Sanitize(string s); }

namespace Rift.Revit.Ui;
internal sealed class ActivityCard : IDisposable
{
    public ActivityCard(IntPtr revitMainWindow, FileLog log);   // thread UI
    public bool Enabled { get; set; }
    public void ShowConnected(string clientName);
    public void ShowResult(string toolName, string outcome, string? errorCode, string? message);
    public void Hide();
}
```

### Serveur (Python)

```python
# discovery.py
@dataclass(frozen=True) class Instance: pid: int; process_start_utc: datetime; pipe: str; revit_version: str; build: str; engine: str; addin_version: str; generation: int; protocol: str; capabilities: tuple[str, ...]; key_path: Path
def list_instances(root: Path | None = None) -> list[Instance]: ...
def verify_alive(inst: Instance) -> bool: ...            # psutil : pid + create_time ±2 s
def choose_target(instances: list[Instance], pid: int | None) -> Instance: ...   # NO_TARGET / AMBIGUOUS_TARGET
def read_secret(inst: Instance) -> bytes: ...

# errors.py
class RiftError(Exception): code: str; message: str; details: dict | None
SERVER_CODES = ("NO_TARGET", "AMBIGUOUS_TARGET", "TARGET_CHANGED", "TARGET_LOST", "TARGET_BUSY", "TARGET_INTERRUPTED", "READ_ONLY_MODE", "LINT_ERROR")

# client.py
class RevitClient:
    def __init__(self, instance: Instance, secret: bytes, client_name: str, client_version: str, *, call_fn=pipe.call) -> None: ...
    async def ensure_lease(self) -> dict: ...
    async def call(self, op: str, args: dict | None = None, *, timeout_s: int = 60, expected_document: dict | None = None) -> dict: ...
    async def wait_job(self, job_id: str, timeout_s: int) -> dict: ...
    @property def holder(self) -> dict | None: ...

# registry.py
Risk = Literal["auto", "ask", "never_auto"]
@dataclass(frozen=True) class ToolSpec: name: str; risk: Risk; read_only: bool; description: str; input_model: type[BaseModel]; remote_op: str
TOOLS: tuple[ToolSpec, ...]
def permissions(fmt: Literal["claude"]) -> str: ...
def schemas() -> dict: ...
FIXED_READ_OPS = frozenset({"context", "tool:audit.p1", ..., "tool:audit.all"})   # ADR-012 : seul périmètre autorisé pour risk="auto"

# tools/context.py
class GetContextInput(BaseModel): pid: int | None = None
async def get_context(client: RevitClient, args: GetContextInput) -> dict: ...
# tools/run_query.py
class RunQueryInput(BaseModel): script: str; title: str = "query"; inputs: dict = {}; timeout_s: int = 60; expected_document: str | None = None
async def run_query(client: RevitClient, args: RunQueryInput) -> dict: ...     # compile puis run
# tools/audit.py
class AuditTemplateInput(BaseModel): preset: Literal["all", "p1", "p2", "p3", "p4", "p5", "p6"] = "all"; write_to: str | None = None; timeout_s: int = 300
async def audit_template(client: RevitClient, args: AuditTemplateInput) -> dict: ...

# server.py
def build_server(client_factory: Callable[[], RevitClient]) -> FastMCP: ...
def main_serve() -> int: ...
# install.py
def install_claude(scope: str = "user", *, run=subprocess.run) -> InstallReport: ...   # InstallReport(command, ok, stdout, hint)
```

## Pile d'appels

**Démarrage** : `App.OnStartup` → `Host.Start` → `RiftConfig.Load` → `FileLog` → `IronPythonEngine` + `CountTraceEvents` (0 → `timeoutAvailable=false`) → `SecretStore` → `DiscoveryFile.PurgeDeadPids`/`Write` → `ActivityCard` (thread UI, caché) → `RevitDispatcher.Create` → `RequestRouter(handlers…)` → `PipeListener.Start`.

**`get_context`** : `server.get_context` → `RevitClient.ensure_lease` → `pipe.call(lease)` → `PipeListener` (auth) → `RequestRouter.Handle` → `LeaseOp` → `LeaseManager.Acquire` → `Granted` → `RevitDispatcher.EnqueueUi(card.ShowConnected)` ; puis `pipe.call(context)` → `RequestRouter.Handle` → `JobTable.Create` → `RevitDispatcher.Enqueue` (`Raise`) → `Job.WaitStarted(30 s)` (échec → `BusyProbe.Reason` → `REVIT_BUSY`) → `Execute` → `ContextOp.Execute` → `DocumentClassifier`, `Coexistence`, `RevitJson` → `JobTable.MarkDone` → `card.ShowResult` → `Job.WaitFinished(timeout_s)` → `ResponseEnvelope` → `pipe` (proof) → `RevitClient` (vérifie la preuve, renouvelle le bail) → résultat typé.

**`run_query`** : `server.run_query` → `pipe.call(compile)` → `CompileOp` (thread du tube, `engine.Compile`) ; erreur → `SCRIPT_ERROR {line}` sans exécution → sinon `pipe.call(run)` → job → `Execute` → `RunQueryOp.Execute` : `timeoutAvailable` ? sinon `TIMEOUT_UNAVAILABLE` → `RunJournal.Begin` → `ReadGuard.Arm` → `ImportGuard.Install` → `engine.Run` → `ReadGuard.Disarm` (rollback) → `outcome` (`WriteDetected` → `rolled_back/WRITE_IN_QUERY`) → `RevitJson` → `RunJournal.End` → `card.ShowResult`. Dépassement de `timeout_s` côté tube → `status: accepted {job_id}` → `RevitClient.wait_job` → `JobOp`.

**`audit_template all`** : idem `run` avec `AuditOp.Execute` → pour chaque `Preset` : `report_progress`, `engine.Run(source, ctx avec MemoryResultCollector)` ; le merge lit `collector.Documents` au lieu de fichiers → `{documents: {"01_…": …, "00_TOUT": …}}` → serveur : `write_to` → fichiers.

**Refus** : trame sans `mac` valide → `RequestAuthenticator.Verify ≠ Ok` → `UNAUTHORIZED`, déconnexion, compteur d'échecs ; deuxième client → `LeaseOp` → `Held` → `LEASE_HELD {holder}` → `card.ShowResult("lease", "refused", "LEASE_HELD")`.

## Plan de tests

Chaque ligne : **assertion** — test — pourquoi elle échoue sur le code M0.

### xUnit (`Rift.Tests`, sans Revit)

1. **Un second créateur du même nom de tube est refusé et le premier continue de servir** — `PipeListenerTests.Single_instance_name_is_held` — aucun écouteur n'existe.
2. **Une trame non signée reçoit `UNAUTHORIZED` et la connexion est fermée ; une trame `health` reçoit `{v, pid}` sans preuve** — `PipeListenerTests.Unsigned_request_is_refused_health_is_answered` — idem.
3. **Un nonce rejoué dans la fenêtre est refusé, un `ts` hors ±120 s est refusé, un `mac` juste avec le corps modifié d'un octet est refusé** — `RequestAuthenticatorTests` (horloge injectée) — pas d'authentificateur.
4. **Dix échecs consécutifs font tourner le secret et incrémentent `generation` ; un succès remet le compteur à zéro** — `RequestAuthenticatorTests.Ten_failures_rotate` — idem.
5. **`DiscoveryFile.Write` est atomique (jamais de fichier partiel observable) et `PurgeDeadPids` supprime les pid morts seulement** — `DiscoveryFileTests` — pas de découverte.
6. **`JobTable` : `WaitStarted` rend `false` après le délai si personne ne démarre ; un job fini est retrouvable jusqu'à la rétention puis `null`** — `JobTableTests` — pas de table.
7. **`LeaseManager` : deuxième candidat → `Held` avec le premier détenteur ; renouvellement prolonge ; après expiration un nouveau candidat obtient le bail ; `Granted` est levé une fois** — `LeaseManagerTests` — pas de bail.
8. **`RequestRouter` : `v: "2.0"` → `PROTOCOL_MISMATCH` ; `op` inconnu → erreur ; `NeedsApiThread=false` n'appelle pas `IJobRunner` ; dépassement de `timeout_s` → `accepted` + `job_id` retrouvable** — `RequestRouterTests` (faux `IJobRunner` synchrone/lent) — pas de routeur.
9. **`ImportGuard` : `import clr` lève `ImportError` dans le script et est signalé ; `import json` passe** — `ImportGuardTests` — M0 prouve que `import clr` **réussit** (P-D4).
10. **`MemoryResultCollector` : `dump('01_x', {...})` depuis Python aboutit dans `Documents["01_x"]` en JSON ; un deuxième `dump` du même nom remplace** — `ResultCollectorTests` — pas de collecteur.
11. **Les six presets compilent sous IronPython 3.4 et n'importent que `json`, `datetime`** — `PresetCatalogTests.All_presets_compile` — les ressources n'existent pas.
12. **`RiftConfig.Load` sur fichier absent ou invalide rend `Default` et ne lève pas** — `ConfigTests` — pas de config.
13. **`Redactor` remplace le nom d'utilisateur, le nom de machine et le dossier du document, et laisse le reste intact** — `RedactorTests` — pas d'expurgation.
14. **`ErrorCodes` contient les trois nouveaux codes et `PROTOCOL.md` les cite** — `ProtocolDocTests` (lit le fichier) — codes absents.
15. **`Rift.Revit` n'est référencé ni par Core ni par Scripting ; aucun assembly ne référence `pyRevit`** — `ArchitectureTests` existant, étendu à `IResultCollector` — passe déjà pour les anciens types ; la nouvelle ligne échoue tant que le type n'existe pas.
15a. **`Run` lève `TimeoutUnavailableException` quand le hook de trace ne délivre aucun événement (engine construit avec un faux `SetTrace` neutralisé) et n'exécute rien** — `IronPythonEngineTests.Run_is_refused_without_a_live_trace_hook` — M0 exécute quand même (le garde n'existe qu'au niveau du prototype).
15b. **`except KeyboardInterrupt:`, `except (ValueError, BaseException):` et `except BaseException as e:` sont réécrits ; `except (ValueError, TypeError):` ne l'est pas ; les numéros de ligne sont préservés ; la deadline coupe une boucle sous chacune des formes** — `ExceptNarrowerTests` — `NarrowBareExcept` ne traite ni `KeyboardInterrupt` ni les tuples.
15c. **Un script qui écrit 2 MiB sur stdout rend 1 MiB et `stdout_truncated: true` ; un `result` de 800 KiB revient en `result_ref` + `preview`, pas en ligne** — `BoundedWriterTests`, `RunQueryOpTests` (côté Core, via le routeur et un faux moteur) — sorties non bornées aujourd'hui.
15d. **`addin/dist/2026/rift/SHA256SUMS.txt` existe après `Stage` et couvre chaque fichier du dossier** — étape CI `verify staged hashes` (PowerShell) — `Stage` ne produit pas le fichier.

### pytest (`server/tests`)

16. **`list_instances` ignore un JSON malformé, `verify_alive` rend `false` pour un pid réutilisé avec une autre date de création, `choose_target` lève `AMBIGUOUS_TARGET` à deux instances sans `pid`** — `test_discovery.py` (psutil bouchonné) — module absent.
17. **Le registre refuse `risk="auto"` à tout outil dont `remote_op` n'est pas dans `FIXED_READ_OPS` ; `run_query` est `ask` et jamais `readOnlyHint` ; `permissions --format claude` liste exactement les outils `auto`** — `test_registry.py` — registre absent, `permissions --check` est vacuous aujourd'hui.
18. **`RevitClient.call` prend le bail au premier appel, transforme `TARGET_BUSY`/`TARGET_CHANGED` du transport en `RiftError`, vérifie la preuve, et bascule sur `wait_job` quand la réponse est `accepted`** — `test_client.py` (`call_fn` bouchonné) — client absent.
19. **`run_query` n'envoie pas `run` quand `compile` échoue et renvoie la ligne ; `audit_template(write_to=…)` écrit `00_TOUT.json` + `0N_*.json` sous `<write_to>/<titre>/` et rien sans `write_to`** — `test_tools.py` — outils absents.
20. **`install_claude` construit `claude mcp add --scope user rift -- rift-mcp serve`, ne touche aucun fichier, et rend un `hint` lisible quand `claude` est introuvable** — `test_install.py` (`run` bouchonné) — `install` affiche « not implemented ».

### Intégration CI (`tests/integration/test_pipehost.py`, Windows)

21. **Le vrai client CPython contre le vrai `PipeListener` (PipeHost) : `health` → pid ; `lease` → accordé ; `context` → réponse en boîte avec preuve vérifiée ; `lease` d'un second `client_id` → `LEASE_HELD` ; trame avec mauvais mac → `UNAUTHORIZED` ; corps de 1 048 577 octets → fermeture** — PipeHost est un bouchon qui ne sert rien.

### Recette dans Revit (manuelle, consignée dans `00-status.md`, copie `SANDBOX`)

22. **Carte** : visible à la connexion ; `LEASE_HELD`, `UNAUTHORIZED` (amendé le 2026-10-13 : carte affichée, au plus une toutes les 2 s, conformément à ADR-023 ; rien n'est exécuté), `WRITE_IN_QUERY`, `REVIT_BUSY` affichent le code reçu par l'assistant ; disparaît seule après succès ; ne prend jamais le focus ; n'apparaît pas au-dessus d'une boîte modale.
23. **Critère 3** : après un `run_query` qui tente `Transaction.Commit`, pile Annuler vide, `IsModified` inchangé, taille et date du fichier inchangées.
24. **Critère 1** : `audit_template all` puis `write_to`, `p8` puis `p9` contre l'extraction de l'ancienne chaîne sur le même document → comparatif vide, statut `=` sur chaque ligne.
25. **Critère 5** : durée de `audit_template all` ≤ somme des six appels de l'ancienne chaîne, même poste, même document.

## Décisions les moins sûres

1. **WPF depuis le contexte isolé, construit en code.** Incertitude : `PresentationFramework` se charge dans le contexte par défaut, mais un `Window` créé depuis un assembly du contexte `rift` peut tomber sur des résolutions de ressources par nom (thèmes, `ResourceDictionary` implicites). Vérification : tranche 1 (balle traçante) crée et affiche la carte dans Revit avant tout autre travail UI ; repli : `System.Windows.Forms` (également dans `WindowsDesktop.App`) ou une barre dans le ruban.
2. **`ExternalEvent.Raise` depuis le thread du tube et `Execute` dans le contexte isolé.** Jamais exercé (point ouvert d'ADR-021). Vérification : tranche 1 ; repli : `Idling` avec file, moins réactif.
3. **Le garde lecture par `TransactionGroup` sur chaque document ouvert modifiable** : un document peut refuser `Start()` (lié, détaché, workshared verrouillé) → `guard="watch"` (ADR-007) et la détection repose sur `DocumentChanged` seul. Vérification : scénario 7 du benchmark avec deux documents ; test manuel avec un document lié.
4. **Portage Python 2 → 3 des presets sans altérer les valeurs** : `str(x.Value)` sur un `ElementId` 64 bits, `"%d"` sur des `long`, `dict.items()` paresseux, `sorted` avec `None`. Vérification : critère 1 (comparatif vide) ; tant qu'il n'est pas vide, aucune autre tranche n'avance.
5. **`anyio.to_thread` autour du client synchrone** : un appel bloqué (Revit figé, pas de réponse) tient le thread jusqu'au timeout socket inexistant sur un tube. Vérification : `test_client` avec `call_fn` qui dort ; mitigation : `timeout_s + 5 s` avec `CancelIoEx` via ctypes ou fermeture du handle depuis la boucle.
6. **`claude mcp add` comme unique voie d'installation** : la CLI peut être absente (Claude Desktop seul). Vérification : `test_install` ; le message de repli donne la commande et le bloc JSON à coller, sans l'écrire.
7. **Auto-test de trace à 0 → `TIMEOUT_UNAVAILABLE` pour tout `run`** : trop strict si le hook ne fire qu'après `SetTrace` (variante P-D4 « late compile »). Vérification : le test compte les deux variantes ; `run` passe en compilation tardive si seule la seconde fonctionne. Depuis ADR-026 §7 l'invariant vit dans `Rift.Scripting` (le moteur refuse lui-même), l'opération ne fait que traduire l'exception en code.
8. **Réécriture des `except` par l'AST d'IronPython** : l'API `IronPython.Compiler` n'est pas publique au même titre que `Microsoft.Scripting.Hosting` ; si elle est trop instable, repli sur un tokenizer maison limité aux lignes `except` (même couverture, mêmes tests 15b).
