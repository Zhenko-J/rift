# Conception : chantier UI — bandeau, fenêtres et carte d'activité (M5)

Porte 3, rédigée le 2026-10-13 sur la base des portes 1 et 2 approuvées et d'ADR-029. Aucun corps de méthode ici : fichiers, signatures, pile d'appels, tests et incertitudes. Valeurs visuelles : porte 1 § Langage visuel, complétées par Z_D01 `design.md` là où la porte 1 se tait (tailles d'affichage 208/148/96/64/44 px, numéral ≥ 96 px sur une fenêtre, pastilles 22/34/56 px, gouttière 14 px, rayon des contrôles 14 px, marge de carte 34 px ou 20 px compacte, cible de clic ≥ 44 px, mesure ≤ 720 px).

## Fichiers

Convention : **N** = nouveau, **M** = modifié, **D** = déplacé. Tout ce qui est sous `Rift.Core` et `Rift.Ui` se teste sans Revit.

**Proposition de découpage (à valider, point 1 des décisions les moins sûres)** : un projet **`Rift.Ui`** (`net8.0-windows`, `UseWPF`, référence `Rift.Core`, **jamais l'API Revit**) porte le thème, le kit, les icônes, la carte et les fenêtres ; `Rift.Revit` ne garde que ce qui touche l'API (ruban, colle dans `Host`). Sans ce projet, rien de l'interface ne se teste en xUnit (`Rift.Tests` ne peut pas référencer `Rift.Revit`, qui dépend de l'API Revit). Il précise la porte 2 (qui disait `Rift.Revit/Ui`) sans en changer une décision ; ADR-021 (disposition de la solution) reçoit une ligne à l'approbation.

### `addin/src/Rift.Core` (sans Revit, sans WPF)

| Fichier | Rôle |
|---|---|
| `Control/PauseGate.cs` N | état `Running`/`Paused`, `Pause(by)`/`Resume()`/`Toggle(by)`, révocation du bail, annulation des jobs non démarrés, événement `Changed` |
| `Activity/RequestRecord.cs` N | une ligne d'activité : clé, heure, libellé, statut, code, message, durée, `run_id`, `ChangeSummary?` |
| `Activity/ChangeSummary.cs` N | reçu de M2 réduit à ce que History montre : comptes par catégorie + raison ; `From(JsonNode? result)` |
| `Activity/ActivityLog.cs` N | tampon circulaire de séance (500), mise à jour par clé (un job `accepted` puis fini = une ligne), compteurs, `Version` |
| `Activity/Outcome.cs` N | table unique statut/code → état de pastille + aparté (`succeeded`, `still running`, `refused — …`) partagée par la carte et History |
| `Jobs/Job.cs` M | événement `Finished` levé par `MarkDone` et par `TryCancelBeforeStart` |
| `Jobs/JobTable.cs` M | `CancelNotStarted(ErrorInfo)` → nombre de jobs annulés |
| `Protocol/RequestRouter.cs` M | porte de pause avant la recherche du handler et avant le bail ; `Completed` passe à `Action<RequestRecord>` ; pas d'événement pour `op: job` ; un job `accepted` émet un second enregistrement à `Job.Finished` |
| `Config/RiftConfig.cs` M | champ `Developer` (`developer`, défaut `false`) ; `WithActivityCard(bool)` |

### `addin/src/Rift.Ui` (nouveau projet, WPF en code, sans Revit)

| Fichier | Rôle |
|---|---|
| `Rift.Ui.csproj` N | `UseWPF`, `AllowUnsafeBlocks` (P/Invoke de la carte), référence `Rift.Core` ; `EmbeddedResource` lié : `docs/pamphlet/tools.json` |
| `Theme.cs` N | **seul** fichier avec des couleurs, rayons, tailles, police, ombre, durées ; pinceaux gelés ; `ReduceMotion` |
| `Kit/Tracked.cs` N | `FrameworkElement` qui dessine un texte avec interlettrage (`FormattedText` par glyphe) : WPF n'a pas de `letter-spacing` |
| `Kit/Z.cs` N | fabrique des primitives : `Card`, `Numeral`, `MicroLabel`, `Aside`, `Pastille`, `PastilleGrid`, `InsetTile`, `GhostButton`, `Switch`, `Rule` |
| `Kit/ZTable.cs` N | `ListView` + `GridView` construits en code, virtualisés, stylés par `Theme` (History, Tools) |
| `Kit/ZAudit.cs` N | parcourt l'arbre visuel : nombre d'éléments peints en accent, blanc pur hors texte sur accent, `Border` sans rayon ; sert aux tests et au mode `developer` |
| `Icons.cs` N | tracés Lucide (avis ISC en tête) et `Render(name, px, scale)` → `BitmapSource` |
| `PastilleState.cs` N | `Done`, `Running`, `Failed`, `Paused` |
| `ActivityCard.cs` D+M | déplacée de `Rift.Revit/Ui` ; mécanique ADR-023 inchangée ; contenu `CardContent` ; Z_D01 |
| `CardContent.cs` N | ce que la carte affiche (pastille, libellé, aparté, code, message) ; fabriques `From(RequestRecord)`, `Connected`, `Paused`, `Resumed` |
| `IUiHost.cs` N | ce que les fenêtres demandent à l'hôte : instantanés, pause, carte, chemin des journaux |
| `Windows/RiftWindow.cs` N | base : propriétaire Win32, non topmost, `ShowOrActivate`, minuterie 1 s visible seulement, titre `Brand.Product · <nom>` |
| `Windows/StatusWindow.cs` N | numéral `CALLS SERVED`, tuiles client / serveur / tube / journal, `activity card`, `pause`, `copy log path` |
| `Windows/HistoryWindow.cs` N | numéral `THIS SESSION`, grille de pastilles, tableau |
| `Windows/ToolsWindow.cs` N | numéral = outils, pastilles `implemented`/`designed`, filtre par risque |
| `Windows/UiWindows.cs` N | une instance par fenêtre, ouverture/activation, fermeture à l'arrêt |
| `ViewModels/StatusSnapshot.cs` N | enregistrement immuable lu par Status |
| `ViewModels/HistoryView.cs` N | lignes et pastilles à partir d'un `ActivitySnapshot` |
| `ViewModels/ToolCatalog.cs` N | lecture de `tools.json`, compteurs, filtre |

### `addin/src/Rift.Revit`

| Fichier | Rôle |
|---|---|
| `Rift.Revit.csproj` M | référence `Rift.Ui` ; `UseWPF`/`AllowUnsafeBlocks` retirés s'ils ne servent plus qu'à la carte déplacée |
| `Ui/Ribbon.cs` N | onglet `NORN`, panneau `RIFT`, boutons, permutation on/off, `developer` |
| `Ui/ActivityCard.cs` D | → `Rift.Ui` |
| `Ui/StatusReport.cs` D | → `Rift.Ui/ViewModels/StatusSnapshot.cs` (le texte disparaît) |
| `Host.cs` M | `Start(app, bootstrapPath)`, `Invoke(command, arg)`, implémente `IUiHost`, crée `PauseGate`, `ActivityLog`, `Ribbon`, `UiWindows` |

### `addin/src/Rift.Addin` (bootstrap)

| Fichier | Rôle |
|---|---|
| `App.cs` M | charge le contexte, `Start(app, path)`, un seul `MethodInfo` (`Invoke`) ; `BuildRibbon` supprimé |
| `RelayCommand.cs` N | base abstraite `IExternalCommand` : relaie `Command` à `App.Invoke`, ne lève jamais vers Revit |
| `Commands.cs` N | `ServerToggleCommand`, `StatusCommand`, `HistoryCommand`, `CardCommand`, `ToolsCommand`, `ProbePythonCommand` (une ligne chacune) ; `InboxCommand` en M4 |
| `StatusCommand.cs`, `ProbePythonCommand.cs` D | absorbés par `Commands.cs` |

### Tests, serveur, documentation

| Fichier | Rôle |
|---|---|
| `addin/tests/Rift.Tests/Rift.Tests.csproj` M | référence `Rift.Ui`, `UseWPF` |
| `addin/tests/Rift.Tests/Sta.cs` N | exécute un test sur un thread STA avec un `Dispatcher` (xUnit n'en fournit pas) |
| `addin/tests/Rift.Tests/{PauseGateTests,ActivityLogTests,OutcomeTests,ChangeSummaryTests,ThemeTests,KitTests,IconsTests,WindowsTests,ActivityCardTests,ToolCatalogTests}.cs` N | voir plan de tests |
| `addin/tests/Rift.Tests/{RequestRouterTests,JobTableTests,ArchitectureTests,PolicyAndJournalTests}.cs` M | pause, `Completed`, `CancelNotStarted`, règles de sources, `developer` |
| `addin/tests/Rift.PipeHost/Program.cs` M | variable `RIFT_PIPEHOST_PAUSED=1` : démarre en pause |
| `server/src/rift_mcp/client.py` M | `PAUSED` : oublie le bail, pas de nouvel essai |
| `server/tests/unit/test_client.py`, `server/tests/integration/test_pipehost.py` M | voir plan de tests |
| `docs/PROTOCOL.md` M | sémantique de `PAUSED` (avant le bail, `result.paused_since_utc`, aucun handler) |
| `docs/DECISIONS.md` M | ADR-021 : ligne `Rift.Ui` (si le point 1 est approuvé) |
| `THIRD_PARTY.md` M | Lucide, ISC, liste des icônes copiées |
| `SECURITY.md`, `README.md`, `CHANGELOG.md` M | pause côté add-in ; captures ; entrée |

## Types et signatures (sans corps)

### Core

```csharp
namespace Rift.Core.Control;

public enum ServerState { Running, Paused }

public sealed record PauseInfo(ServerState State, DateTimeOffset? SinceUtc, string? By);

/// Thread-safe. Pause() is synchronous: once it returns, every request reaching the router is refused.
public sealed class PauseGate
{
    public PauseGate(LeaseManager lease, JobTable jobs, Func<DateTimeOffset>? clock = null);
    public PauseInfo State { get; }
    public bool IsPaused { get; }
    /// Raised on the caller's thread after the state changed; never under the lock.
    public event Action<PauseInfo>? Changed;
    /// Revokes the lease, cancels queued jobs with PAUSED; returns the number of jobs cancelled. Idempotent.
    public int Pause(string by);
    public void Resume();
    /// Returns the new state.
    public PauseInfo Toggle(string by);
}
```

```csharp
namespace Rift.Core.Activity;

public sealed record CategoryChange(string Category, int Added, int Modified, int Deleted);

public sealed record ChangeSummary(IReadOnlyList<CategoryChange> Categories, string? Reason)
{
    /// Reads result.changes (format fixed by M2); null when absent or malformed. Category names go through the sanitiser (ADR-020), at most 50 categories.
    public static ChangeSummary? From(JsonNode? result);
    public int Total { get; }
}

/// Key = job id when the request became a job, otherwise a per-log sequence number.
public sealed record RequestRecord(string Key, DateTimeOffset Utc, string Label, string Status, string? Code, string? Message,
    long ElapsedMs, string? RunId, ChangeSummary? Changes);

public sealed record ActivitySnapshot(IReadOnlyList<RequestRecord> Records, long Total, int Succeeded, int Failed, int Running, long Version);

public sealed class ActivityLog
{
    public const int DefaultCapacity = 500;
    public ActivityLog(int capacity = DefaultCapacity);
    /// Inserts, or replaces the record with the same key in place (accepted → finished). Total counts distinct keys since start.
    public void Upsert(RequestRecord record);
    public string NextKey();
    public long Version { get; }
    public ActivitySnapshot Snapshot();
}

public enum OutcomeKind { Done, Running, Failed, Paused }

public static class Outcome
{
    /// ok → Done "succeeded"; accepted|running → Running "still running"; refused PAUSED → Paused "paused by you";
    /// refused|error → Failed "refused — <message>" / "failed — <message>". Single table used by card and History.
    public static (OutcomeKind Kind, string Aside) Of(string label, string status, string? code, string? message);
}
```

```csharp
namespace Rift.Core.Jobs;
public sealed class Job      { /* existing */ public event Action<Job>? Finished; }
public sealed class JobTable { /* existing */ public int CancelNotStarted(ErrorInfo error); }

namespace Rift.Core.Protocol;
public sealed class RequestRouter
{
    public RequestRouter(IEnumerable<IOperationHandler> handlers, JobTable jobs, IJobRunner apiThread, Func<TargetInfo> target, FileLog log,
        TimeSpan? startTimeout = null, TimeSpan? defaultTimeout = null, LeaseManager? lease = null,
        PauseGate? pause = null, ActivityLog? activity = null);
    /// Raised once per request (none for op "job"), and once more when an accepted job finishes (same Key).
    public event Action<RequestRecord>? Completed;
}

namespace Rift.Core.Config;
public sealed record RiftConfig(bool ActivityCard, IReadOnlyList<string> SandboxMarkers, IReadOnlyList<string> ReferenceNames,
    IReadOnlyList<string> AgentPatterns, int StartTimeoutS, bool Developer)
{
    public RiftConfig WithActivityCard(bool on);
}
```

### Ui

```csharp
namespace Rift.Ui;

public static class Theme
{
    public static class Palette   // porte 1 values, verbatim
    {
        public static readonly Color Ground, Card, Inset, Ink, SubInk, Quiet, Hairline, Stroke, Accent, OnAccent, Success, Warning, Failure;
    }
    public static Brush Brush(Color c);                 // cached, frozen
    public static readonly FontFamily Mono;             // "Consolas, Courier New"
    public static class Size  { public const double Numeral = 96, NumeralCompact = 64, Body = 15, BodySmall = 13, Label = 11, Pastille = 22, PastilleGrid = 34, Gutter = 14, HitMin = 44, Measure = 720; }
    public static class Radius { public const double Window = 28, Panel = 20, Control = 14; }
    public static class Pad    { public const double Card = 34, Compact = 20; }
    public const double LabelTrackingEm = 0.2, NumeralTrackingEm = -0.05;
    public static Effect Shadow();                      // one warm shadow: rgba(25,25,24,.16), blur 34, offset 14
    public static readonly TimeSpan Appear;             // 220 ms, 4 px rise
    public static bool ReduceMotion { get; }            // !SystemParameters.ClientAreaAnimation
}

public enum PastilleState { Done, Running, Failed, Paused }

public static class Z
{
    public static Border Card(UIElement child, double radius = Theme.Radius.Panel, bool compact = false);
    public static FrameworkElement Numeral(string digits, double size = Theme.Size.Numeral);
    public static FrameworkElement MicroLabel(string text);          // upper-cased here, tracked 0.2 em, Quiet ink
    public static TextBlock Aside(string text);                       // lower case enforced by the caller; no trailing period (asserted in tests)
    public static Shape Pastille(PastilleState state, double size = Theme.Size.Pastille);
    public static Panel PastilleGrid(IReadOnlyList<PastilleState> states, int perRow = 7);
    public static Border InsetTile(string label, string value, string? aside = null);
    public static Button GhostButton(string verb, Action onClick);   // 2 px stroke, round, ≥ 44 px high
    public static ToggleButton Switch(bool on, Action<bool> changed);
    public static Rectangle Rule();
}

public sealed class Tracked : FrameworkElement
{
    public Tracked(string text, double size, double trackingEm, FontWeight weight, Brush ink);
}

public static class ZAudit
{
    public static int CountAccent(DependencyObject root);
    public static IReadOnlyList<string> Violations(DependencyObject root);   // pure white outside OnAccent text, Border without radius, non-Mono font
}

public static class Icons
{
    public const string Power = "power", Pause = "pause", Gauge = "gauge", History = "history", Card = "panel-bottom", Book = "book-open", Inbox = "inbox", Flask = "flask-conical";
    public static BitmapSource Render(string name, int px, double scale);   // stroke 1.75 on a 24 box, round caps/joins, Ink, never filled
}

public sealed record CardContent(PastilleState State, string Label, string Aside, string? Code, string? Message)
{
    public static CardContent From(RequestRecord r);
    public static CardContent Connected(string client);
    public static CardContent Paused(PauseInfo p);
    public static CardContent Resumed();
}

internal sealed partial class ActivityCard : IDisposable
{
    public ActivityCard(IntPtr revitMainWindow, FileLog log);
    public bool Enabled { get; set; }
    public void Show(CardContent content);
    public void Hide();
}

public interface IUiHost
{
    StatusSnapshot Status();
    ActivitySnapshot Activity();
    PauseInfo TogglePause(string by);
    void SetActivityCard(bool on);       // persists config.json, hides the card when off
    string LogFolder { get; }
}

public abstract class RiftWindow : Window
{
    protected RiftWindow(IntPtr owner, string name, IUiHost host);
    protected IUiHost Host { get; }
    protected abstract UIElement Build();
    protected abstract void Refresh();   // timer tick while visible; skipped when the snapshot version did not change
    public void ShowOrActivate();
}
public sealed class StatusWindow  : RiftWindow { public StatusWindow(IntPtr owner, IUiHost host); }
public sealed class HistoryWindow : RiftWindow { public HistoryWindow(IntPtr owner, IUiHost host); }
public sealed class ToolsWindow   : RiftWindow { public ToolsWindow(IntPtr owner, IUiHost host, ToolCatalog catalog); }

public sealed class UiWindows : IDisposable
{
    public UiWindows(IntPtr owner, IUiHost host, Func<ToolCatalog> catalog);
    public void Open(string name);       // "status" | "history" | "tools"
}

public sealed record StatusSnapshot(string Version, string? PipeName, int Served, LeaseHolder? Holder, DateTimeOffset? LeaseUntil,
    int Generation, string? LastRefusal, PauseInfo Pause, bool CardAvailable, bool CardEnabled, string LogFolder, string Engine);

public sealed record HistoryRow(string Time, string Tool, string Outcome, string? Code, string Duration, PastilleState State, string? Changes);
public static class HistoryView
{
    public static IReadOnlyList<HistoryRow> Rows(ActivitySnapshot s, TimeZoneInfo zone);   // newest first
    public static IReadOnlyList<PastilleState> Pastilles(ActivitySnapshot s);              // oldest first, last row ragged
}

public sealed record ToolEntry(string Name, string Wave, string Toolset, string Risk, string Status, string Description, IReadOnlyList<string> Origin);
public sealed class ToolCatalog
{
    public static ToolCatalog LoadEmbedded();
    public static ToolCatalog Load(Stream json);
    public string Version { get; }
    public string CurrentWave { get; }
    public IReadOnlyList<ToolEntry> Tools { get; }
    public int Implemented { get; }
    public IEnumerable<ToolEntry> Filter(string? risk);
}
```

### Revit et bootstrap

```csharp
namespace Rift.Revit.Ui;
internal sealed class Ribbon
{
    /// Creates or reuses NORN (ArgumentException = already there), adds the RIFT panel:
    /// [on/off large] | [status, history, card stacked] | [tools large] | [probe python small, developer only].
    public static Ribbon Build(UIControlledApplication app, string bootstrapPath, bool developer, double scale, FileLog log);
    public void SetPaused(bool paused);        // API/UI thread: ItemText, LargeImage, Image, ToolTip
    public void SetCard(bool on);              // ItemText "card on" / "card off"
}

namespace Rift.Revit;
public sealed class Host : IDisposable, IUiHost
{
    public void Start(UIControlledApplication app, string bootstrapPath);
    public void Stop();
    /// "server.toggle" | "status" | "history" | "card.toggle" | "tools" | "probe.python"; arg = UIApplication. Never throws; returns an error text or null.
    public string? Invoke(string command, object? arg);
}

namespace Rift.Addin;
public sealed class App : IExternalApplication { internal static string? Invoke(string command, object? arg); }
public abstract class RelayCommand : IExternalCommand
{
    protected abstract string Command { get; }
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements);   // TaskDialog only if Invoke returned an error text
}
public sealed class ServerToggleCommand : RelayCommand { protected override string Command => "server.toggle"; }
// StatusCommand, HistoryCommand, CardCommand, ToolsCommand, ProbePythonCommand: same shape
```

### Serveur

```python
# client.py — _check(): code == "PAUSED" → self._lease_until = None; raise RiftError("PAUSED", message, details)
#   (no retry, no automatic re-lease: the next tool call leases again and gets PAUSED again while paused)
```

## Pile d'appels

**Démarrage** : `App.OnStartup` → `Host.Start(app, bootstrapPath)` → (inchangé jusqu'au routeur) → `PauseGate(lease, jobs)` → `ActivityLog` → `RequestRouter(…, pause, activity)` → `router.Completed += OnCompleted` → `PipeListener.Start` → `Ribbon.Build(app, path, config.Developer, scale)` → `Icons.Render` × 2 tailles → `ActivityCard` (Rift.Ui) → `UiWindows`. `pause.Changed += p => EnqueueUi(_ => { ribbon.SetPaused(p.State == Paused); card.Show(CardContent.Paused(p) | Resumed()); })`.

**Clic on/off** : Revit → `ServerToggleCommand.Execute` → `App.Invoke("server.toggle", uiapp)` → `Host.Invoke` → `PauseGate.Toggle("you")` → `LeaseManager.Revoke` + `JobTable.CancelNotStarted(PAUSED)` (chaque job annulé lève `Finished` → `Completed` → History) → `Changed` → `EnqueueUi(ribbon.SetPaused, card.Show)` (exécuté dès que Revit rend la main, juste après la commande).

**Requête en pause** : `PipeListener` (auth OK) → `RequestRouter.Handle` → `pause.IsPaused` → `Refuse(PAUSED, "paused by the user in Revit", {paused_since_utc})` → `Completed(record)` → `ActivityLog.Upsert` + `EnqueueUi(card.Show)`. Aucun handler, aucun bail, aucun job.

**Requête normale** : … → handler ou job → réponse ; `ok|error|refused` → `Completed(record{Key = NextKey()})` ; `accepted` → `Completed(record{Key = job.Id, Status = accepted})` et `job.Finished += j => Completed(record{Key = j.Id, final})`. `op: job` → réponse au client, aucun `Completed`. `OnCompleted` : `ActivityLog.Upsert(r)` ; `EnqueueUi(card.Show(CardContent.From(r)))` ; `LEASE_HELD` → `_lastRefusal`.

**Refus d'authentification** : `PipeListener.Unauthorized` → `Host.OnUnauthorized` → `ActivityLog.Upsert(record{Label = "request", refused, UNAUTHORIZED})` + carte (limitée à une toutes les 2 s, inchangé).

**Fenêtres** : `StatusCommand` → `Host.Invoke("status")` → `UiWindows.Open("status")` → `StatusWindow.ShowOrActivate` → minuterie → `IUiHost.Status()` → redessin si changé. Bouton `pause` → `IUiHost.TogglePause("you")` (même chemin que le ruban). Interrupteur → `IUiHost.SetActivityCard(on)` → `RiftConfig.WithActivityCard(on).Save` + `card.Enabled` + `EnqueueUi(ribbon.SetCard)`. `copy log path` → `Clipboard.SetText(LogFolder)` (un nouvel essai après 100 ms si le presse-papiers est occupé, sinon aparté `clipboard busy — path shown above`).

**Arrêt** : `Host.Stop` → `UiWindows.Dispose` (ferme les fenêtres) → existant.

## Plan de tests

Chaque ligne : **assertion** — test — pourquoi elle échoue sur le code actuel. Les tests WPF tournent sur un thread STA (`Sta.Run`).

### xUnit — Core

1. **`Pause()` : `IsPaused`, le détenteur du bail devient `null`, un job en file non démarré finit `PAUSED` (refusé) et n'est jamais exécuté, un job démarré n'est pas touché ; `Resume()` rend `Running` ; deux `Pause()` = un seul `Changed`** — `PauseGateTests` — `PauseGate` n'existe pas.
2. **En pause, toute requête authentifiée (`lease`, `context`, `run`, op inconnue) reçoit `refused/PAUSED` avec `paused_since_utc`, aucun handler n'est appelé (compteur 0), aucun job créé, le bail reste vide** — `RequestRouterTests.Paused_refuses_everything_before_lease` — le routeur n'a pas de porte de pause.
3. **Après `Resume()`, la première requête d'un client reprend le bail et s'exécute** — `RequestRouterTests.Resume_admits_again` — idem.
4. **`CancelNotStarted` annule les jobs `queued` seulement et lève `Finished` pour chacun** — `JobTableTests` — méthode et événement absents.
5. **`Completed` : une requête simple → un enregistrement ; un job `accepted` → deux enregistrements de même `Key` (accepted puis final) même sans `op: job` du client ; `op: job` → aucun** — `RequestRouterTests.Completed_records` — `Completed` porte quatre chaînes et se déclenche aussi pour `job`.
6. **`ActivityLog` : 501 insertions → 500 lignes, `Total` = 501, la plus ancienne est sortie ; `Upsert` d'une clé existante remplace en place sans changer `Total` ; `Version` croît à chaque écriture ; un instantané ne bouge pas après une écriture ultérieure ; 8 threads × 1 000 insertions sans exception** — `ActivityLogTests` — n'existe pas.
7. **`Outcome.Of` couvre la table : ok, accepted, running, refused `PAUSED` → `Paused`, refused `LEASE_HELD` → `Failed` + `refused — held by …`, error `SCRIPT_ERROR` → `Failed` ; aucun aparté ne finit par un point ni ne contient de majuscule hors code** — `OutcomeTests` — n'existe pas (la carte a son propre `switch`).
8. **`ChangeSummary.From` : `changes` absent → `null` ; format attendu → comptes et raison ; caractères de contrôle retirés (ADR-020) ; plus de 50 catégories → 50** — `ChangeSummaryTests` — n'existe pas.
9. **`config.json` : `developer` absent → `false`, `true` lu ; `WithActivityCard(false).Save` puis `Load` → `false`, autres champs inchangés** — `PolicyAndJournalTests` (config) — champ absent.

### xUnit — Ui (STA)

10. **Chaque couleur de `Theme.Palette` vaut la valeur de la porte 1** (table de 13 lignes) — `ThemeTests` — `Theme` n'existe pas.
11. **`Tracked` : la largeur mesurée de `CALLS SERVED` à 0,2 em dépasse celle à 0 em de `11 × 0,2 × (n − 1)` px à ±1 px ; à −0,05 em elle est plus courte** — `KitTests.Tracking` — WPF n'a pas d'interlettrage.
12. **`PastilleGrid` de 15 états → lignes de 7, 7, 1 ; `Pastille(Paused)` sans remplissage, contour 2 px ; `Pastille(Running)` peinte en accent ; `GhostButton` ≥ 44 px de haut** — `KitTests` — n'existe pas.
13. **Chaque fenêtre (Status, History, Tools), construite avec un faux `IUiHost` dans chacun de ses états (vide, en pause, client connecté, job en cours, échecs), a `ZAudit.CountAccent ≤ 1`, aucune `ZAudit.Violations`, un numéral ≥ 96 px, `Topmost == false`, un propriétaire** — `WindowsTests` — aucune fenêtre n'existe.
14. **History : 3 succès, 1 `LEASE_HELD`, 1 job en cours → numéral `5`, pastilles pleine ×3, rouge ×1, orange ×1, ligne `LEASE_HELD` avec le même code et message que la réponse** (critère 2) — `WindowsTests.History_matches_records` — idem.
15. **Carte : après `EnsureHandle`, style étendu contient `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` ; `ShowActivated == false`, `Focusable == false`, `Topmost == false` ; `Show(CardContent.From(running))` → une seule pastille accent ; `Enabled = false` → `Show` ne rend pas la fenêtre visible ; aucun numéral** — `ActivityCardTests` — la carte actuelle n'expose pas `CardContent` et peint un liseré vert/rouge.
16. **`Icons.Render` : pour chaque icône, 16 et 32 px à l'échelle 1 → bitmaps 16×16 et 32×32 ; à l'échelle 1,5 → 24×24 et 48×48 ; au moins un pixel d'encre, aucun pixel orange** — `IconsTests` — n'existe pas.
17. **`ToolCatalog.LoadEmbedded` : même nombre d'outils et même `current_wave` que `docs/pamphlet/tools.json` lu sur disque ; `Filter("auto")` ne rend que des `auto`** — `ToolCatalogTests` — n'existe pas.

### xUnit — architecture (lecture des sources et des références)

18. **Aucun `Color.FromRgb`, `Color.FromArgb`, `Colors.`, littéral `#RRGGBB`, `new FontFamily`, `CornerRadius(` numérique hors `Theme.cs` dans `Rift.Ui` et `Rift.Revit`** — `ArchitectureTests.Visual_literals_live_in_theme` — `ActivityCard.cs` en contient aujourd'hui.
19. **Aucun littéral `"RIFT"` ni `"NORN"` hors `Brand.cs` dans `addin/src`** (critère 5) — `ArchitectureTests.Product_name_has_one_source` — à vérifier ; échoue si une chaîne d'interface s'y glisse.
20. **Aucun `Topmost = true` dans `addin/src` ; `Rift.Ui` ne référence pas l'API Revit ; `Rift.Addin` ne référence que `RevitAPI*` et le framework** — `ArchitectureTests` — `Rift.Ui` n'existe pas.

### pytest

21. **Réponse `refused/PAUSED` → `RiftError("PAUSED")`, `client.leased` devient faux, aucun second envoi** — `test_client.py::test_paused_forgets_lease_and_does_not_retry` — `_check` ne traite pas `PAUSED`.
22. **Contre PipeHost démarré avec `RIFT_PIPEHOST_PAUSED=1` : `get_context` → `PAUSED`, le faux handler n'a rien exécuté** — `test_pipehost.py::test_paused_host_refuses` — PipeHost ne connaît pas la variable.

### Recette dans Revit (manuelle, consignée dans `00-status.md`, copie `SANDBOX`)

23. **Critère 1** : clic `server on` → `paused` ; 10 appels `get_context` lancés dans la seconde qui suit → 10 `PAUSED` ; aucune ligne `execute op=` au journal de l'add-in pendant la pause ; second clic → l'appel suivant passe.
24. **Critère 2** : scénarios 3, 8, 9, 10, 11, 12 du benchmark rejoués ; chaque appel figure dans History avec le code reçu par l'assistant.
25. **Critère 3** : grille Z_D01 en cinq points remplie pour la carte (4 états), Status, History, Tools, avec capture ; icônes nettes à 100 % et 150 % d'affichage.
26. **Critère 4** : saisie continue dans une zone de texte de Revit pendant une rafale de 20 `get_context` → aucun caractère perdu ; une boîte modale ouverte → la carte n'apparaît qu'à sa fermeture.
27. **Critère 5** : `Brand.Product` changé en `RIFTX` dans une construction locale → onglet, panneau, titres et carte affichent `RIFTX`, test 19 vert ; construction jetée.
28. **Onglet partagé** : un second plugin de la suite NORN (ou, à défaut, un add-in de test qui crée `NORN` avant RIFT) → un seul onglet, deux panneaux.

## Décisions les moins sûres

1. **Nouveau projet `Rift.Ui`.** Incertitude : un assembly de plus dans le contexte isolé et dans `deps.json` ; WPF depuis un deuxième assembly du contexte `rift` n'a jamais été chargé dans Revit. Bénéfice : tout l'affichage se teste sans Revit. Vérification : tranche 1 (balle traçante) affiche la carte depuis `Rift.Ui`. Repli : fusion dans `Rift.Revit` et tests d'interface limités aux modèles de vue placés dans `Rift.Core`.
2. **Interlettrage dessiné à la main (`Tracked`).** WPF n'a pas de `letter-spacing` ; `FormattedText` glyphe par glyphe perd le crénage (sans effet en Consolas, police à chasse fixe) et l'accessibilité du texte (exposer `AutomationProperties.Name`). Vérification : test 11 et capture ; repli : espaces fines insérées (approximation fixe).
3. **Icônes rendues par `RenderTargetBitmap` dans le ruban.** Le ruban de Revit (AdWindows) peut redimensionner une image dont le DPI ne vaut pas 96 et la rendre floue. Vérification : recette 25 à 100 % et 150 % ; repli (ADR-029 §4) : PNG au build, DPI 96, une taille par échelle.
4. **Mise à jour du ruban par `EnqueueUi` plutôt que directement dans la commande.** Uniforme (ruban et fenêtre Status passent par le même chemin), mais le bouton change d'aspect un tour de boucle après le clic ; si Revit enchaîne sur une boîte modale, le libellé attend. Le critère 1 n'en dépend pas (la porte bascule de façon synchrone). Vérification : recette 23 ; repli : appel direct quand `Invoke` vient d'une commande (contexte API valide).
5. **`PAUSED` en pastille vide, pas rouge.** La porte 1 dit « rouge = échoué ou refusé » et « vide = en pause » ; une demande refusée **parce que** l'utilisateur a mis en pause relève des deux. Choix : vide, parce que c'est l'état voulu par l'utilisateur, pas une panne. À confirmer à cette porte.
6. **`Job.Finished` levé sur le thread de l'API** depuis `MarkDone` : l'abonné fait un `Upsert` (verrou court) et un `EnqueueUi` depuis l'intérieur d'`Execute` (nouveau `Raise` pendant l'exécution du gestionnaire). Revit l'accepte d'après la documentation, jamais exercé ici. Vérification : test 5 sur le faux dispatcher, puis recette 24 avec un `run_query` long.
7. **Ombre sur une fenêtre `AllowsTransparency`** : rendu logiciel, la fenêtre doit grandir de la marge de l'ombre (34 + 14 px) et le placement de la carte doit la retrancher. Vérification : capture de la recette 25 et absence de saccade en rafale (recette 26) ; repli : ombre réduite à 12 px sur la carte seulement.
8. **Place dans l'onglet partagé** : un grand bouton, une pile de trois, un grand bouton, plus « Probe Python » en mode `developer`. Si d'autres plugins NORN remplissent l'onglet, Revit replie le panneau. Vérification : recette 28 ; repli : `PulldownButton` pour Activity.
9. **`RiftConfig.Save` réécrit le fichier entier** et perd les clés inconnues ou ajoutées à la main (rien n'en dépend aujourd'hui). Vérification : test 9 ; repli : fusion `JsonObject` lue puis réécrite.
