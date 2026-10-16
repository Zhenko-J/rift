# Conception : M2 — écritures gardées

Rédigée le 2026-10-13 (autopilote, à valider). Signatures, pile d'appels, tests et incertitudes ; pas de corps de méthode.

## Fichiers

**N** = nouveau, **M** = modifié. Tout ce qui est sous `Rift.Core` et `Rift.Scripting` se teste sans Revit.

### `addin/src/Rift.Scripting`

| Fichier | Rôle |
|---|---|
| `Runs/WritePolicy.cs` N | `Decide(documentClass, requested, allowReference) → PolicyDecision(Applied: "auto"\|"ask", RefusalCode?)` — fonction pure d'ADR-009 |
| `Runs/ChangeReceipt.cs` N | `ChangeReceipt(Added, Modified, Deleted, Transactions, ByCategory, Samples)`, `ToJson()` ; `ChangeSample(Id, Category, Name)` |
| `Runs/IRunHost.cs` M | `IWriteGuard : IRunGuard { void Arm(string title); ChangeReceipt Changes { get; } bool FailureRolledBack { get; } GuardReport Disarm(leftovers, bool keep); }` ; `IAskDialog { AskResult Ask(string title, ChangeReceipt changes); }` ; `IRunHost.CreateWriteGuard()`, `IRunHost.Ask` (null = pas de boîte possible → `ask` refusé `BAD_REQUEST`) |
| `Runs/RunQueryOp.cs` M | `Execute` accepte `mode: modify` ; `RunModify(...)` : politique, compile, journal, garde écriture, décision, reçu, historique ; `Finish` partagé ; `CANCELLED` quand le jeton est annulé |
| `Runs/RunArgs.cs` M | `Mode` accepte `query` et `modify` ; `DryRun` (défaut `true`), `Policy` (`auto`\|`ask`, défaut `auto`), `AllowReference` (défaut `false`) |
| `Runs/ToolOp.cs` N | `op: tool` : route `args.name` par préfixe (`audit.` → `AuditOp`, `history.` → `HistoryOp`) ; `BAD_REQUEST` listant les noms sinon |
| `Runs/AuditOp.cs` M | devient `IToolHandler` (`Prefix = "audit."`), sans autre changement |
| `Runs/HistoryOp.cs` N | `history.record {history_id?, reason}` et `history.list {limit}` sur `RunJournal.History` du document actif |

### `addin/src/Rift.Core`

| Fichier | Rôle |
|---|---|
| `Jobs/Job.cs` M | `CancellationTokenSource` ; `Token` ; `Cancel()` ; `CancelRequested` |
| `Jobs/JobTable.cs` M | `TryCancel(job) → queued (terminé CANCELLED) \| running (jeton annulé) \| done` |
| `Protocol/Ops/JobOp.cs` M | `args.cancel: true` → `TryCancel`, réponse `{job_id, state, cancel_requested}` |
| `Journal/RunJournal.cs` M | `AppendHistory(key, JsonObject line)`, `ReadHistory(key, limit)`, `AttachReason(key, historyId?, reason)` ; dossier `history/` à côté de `runs/` ; expurgé |

### `addin/src/Rift.Revit`

| Fichier | Rôle |
|---|---|
| `Guard/WriteGuard.cs` N | `IWriteGuard` : groupe nommé sur la cible, groupes lecture ailleurs, collecte des ids par catégorie, blocages, `FailuresProcessing` (ADR-010), `DialogBoxShowing` (ADR-009) ; `Disarm(keep)` : `Assimilate()` ou `RollBack()` sur la cible, `RollBack()` ailleurs |
| `Guard/AskDialog.cs` N | `IAskDialog` : sélection des éléments touchés, `TaskDialog` `rift.ask` Garder/Annuler (Annuler par défaut), sélection remise à vide |
| `Ops/RevitRunHost.cs` M | `CreateWriteGuard()`, `Ask` |
| `Ops/ContextOp.cs` M | `policy {read_only: false, milestone: "M2", run_modes: [query, modify], document_class, write_policy}` |
| `Host.cs` M | `ToolOp(AuditOp, HistoryOp)` ; `RunJournal` avec `history/` |

### `server/src/rift_mcp`

| Fichier | Rôle |
|---|---|
| `lint.py` N | `lint(script) → LintReport(errors, warnings)` : parso 3.6 (syntaxe), `async`/`await`, annotations de variable, `OpenAndActivateDocument`, `clr`, processus/sockets/ctypes, `.IntegerValue`, `ElementId(<int>)`, boîtes modales, `doc.Save/Close/Sync`, `IFailuresPreprocessor` |
| `tools/run_modify.py` N | `RunModifyInput` ; `run_modify(client, args)` : `EXPECTED_DOCUMENT_REQUIRED`, lint → `LINT_ERROR`, compile, run `modify` ; renvoie `run`, `lint {warnings}` |
| `tools/history.py` N | `record_change`, `get_change_records` → `op: tool history.*` |
| `tools/jobs.py` N | `cancel_job(job_id)` → `op: job {cancel: true}` |
| `server.py` M | les quatre outils, prompt `change` |
| `registry.py` M | `CURRENT_WAVE = "M2"`, statuts |

## Pile d'appels

**`run_modify`** : `server.run_modify` → `lint.lint` → `pipe.call(compile)` → `pipe.call(run {mode: modify})` → `RunQueryOp.Execute` → `RunArgs.Mode/DryRun/Policy` → `CheckExpectedDocument` (obligatoire) → `WritePolicy.Decide(target.DocumentClass, …)` (refus avant compilation) → `engine.Compile` → `RunJournal.Begin` → `host.CreateWriteGuard()` → `guard.Arm(title)` → `engine.Run(…, job.Token)` → `guard.Changes` (instantané avant toute annulation) → décision (`keep`) : erreur/timeout/annulation/`FailureRolledBack` → false ; `dry_run` → false ; `auto` → true ; `ask` → `host.Ask.Ask(title, changes)` → `guard.Disarm(leftovers, keep)` → `Finish` (outcome `committed` \| `rolled_back` + `reason`, `changes`, `history_id`) → `RunJournal.AppendHistory` → `RunJournal.End` → carte.

**`cancel_job`** : `server.cancel_job` → `pipe.call(job {job_id, cancel: true})` → `JobOp` → `JobTable.TryCancel` → en cours : `job.Cancel()` → `TraceDeadline` voit le jeton à la prochaine trace → `KeyboardInterrupt` → `run.TimedOut` + jeton annulé → outcome `cancelled`, `RollBack`.

## Tests

### xUnit (sans Revit)
1. `WritePolicyTests` : les dix combinaisons (classe × politique × `allow_reference`) d'ADR-009.
2. `RunModifyOpTests` (vrai moteur, faux garde d'écriture, faux hôte) : (a) `expected_document` absent → `EXPECTED_DOCUMENT_REQUIRED`, rien n'est compilé ; (b) document `unmarked` → `SANDBOX_REQUIRED` refusé avant le garde ; (c) dry-run (défaut) : script qui « modifie » le faux document → `rolled_back / dry_run`, reçu renseigné, garde `keep = false` ; (d) `dry_run: false`, `auto` → `committed`, `keep = true`, `history_id = run_id`, une ligne d'historique écrite ; (e) `ask` avec faux dialogue « Garder » → `committed` ; « Annuler » → `rolled_back / user_discarded` ; (f) `unsaved` + `auto` demandé → `policy_applied: ask` ; (g) erreur de script → `SCRIPT_ERROR`, `keep = false`, `outcome: rolled_back` dans les détails ; (h) timeout → `TIMEOUT`, rollback ; (i) échec Revit simulé (`FailureRolledBack`) → `rolled_back / revit_failure` même sans dry-run ; (j) `ask` sans boîte possible (`host.Ask` null) → `BAD_REQUEST` ; (k) transaction laissée ouverte (leftover) → `rolled_back / open_transaction_left`.
3. `ToolOpTests` : routage `audit.*` / `history.*`, nom inconnu listant tout.
4. `HistoryTests` : `AppendHistory` + `ReadHistory` (ordre, limite, expurgation), `AttachReason` sur la dernière ligne et sur une ligne donnée, `history.record` sans historique → `BAD_REQUEST`.
5. `JobCancelTests` : annulation en file → `CANCELLED` sans exécution ; en cours → jeton annulé, le script en boucle s'arrête, outcome `cancelled` ; terminé → sans effet.
6. `RunQueryOpTests` inchangés (le mode query ne bouge pas).

### pytest
7. `test_lint.py` : chaque règle sur un script minimal ; un script propre ne renvoie rien ; f-string acceptée.
8. `test_run_modify.py` : `EXPECTED_DOCUMENT_REQUIRED` sans envoi ; `LINT_ERROR` sans envoi ; `lint: false` journalisé dans les args ; dry-run par défaut ; `committed` relayé avec `changes` ; `SANDBOX_REQUIRED` relayé comme refus ; `accepted` → `job`.
9. `test_history.py`, `test_jobs.py` : enveloppes envoyées, réponses relayées.
10. Intégration PipeHost : `run_modify` de bout en bout avec un faux garde d'écriture (dry-run puis `committed`), `cancel_job` sur un script en boucle.

### Recette dans Revit (copie SANDBOX, consignée dans `00-status.md`)
11. Critères 1-5 de la porte 1 ; scénarios 4, 5, 6, 7 du benchmark ; carte sur `committed`, `rolled_back`, `SANDBOX_REQUIRED`.

## Incertitudes

1. **`TransactionGroup.Assimilate()`** fusionne bien toutes les transactions du script en une entrée Annuler nommée comme le groupe — vérifié dans la documentation de l'API, à confirmer en recette (critère 1).
2. **`DialogBoxShowing` et `OverrideResult`** : l'API ne dit pas quels boutons la boîte porte ; `Cancel(2)` est tenté en premier (ADR-009) et le résultat réel n'est connu qu'en recette. Défaut autopilote : une seule tentative, journalisée.
3. **`DocumentChanged` pendant `Assimilate`/`RollBack`** : désabonnement **avant** (comme le garde lecture), sinon le rollback compterait comme écriture.
4. **Jeton d'annulation et trace** : `TraceDeadline` ne regarde le jeton que tous les 64 événements : un script bloqué dans un appel .NET long ne s'annule qu'au retour. Documenté.
5. **Clé d'historique** d'un document non enregistré : SHA-256 du titre (défaut autopilote), ce qui mélange deux documents homonymes non enregistrés — acceptable pour un historique de séance.
