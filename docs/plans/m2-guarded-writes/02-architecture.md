# Architecture : M2 — écritures gardées

Rédigée le 2026-10-13 (autopilote, à valider). Elle s'appuie sur le code M1 tel qu'il est au commit `9d10ca4`.

## Code existant lu

| Fichier | Aujourd'hui (M1) | Ce que M2 en fait |
|---|---|---|
| `Rift.Scripting/Runs/RunQueryOp.cs` | `op: run` en `mode: query` : invariant de timeout, compile, journal, garde lecture, rollback, résultat | devient le seul handler de `run` pour les deux modes : le chemin `modify` partage compile, journal, sorties bornées, `result_ref` ; il diffère par le garde (écriture), la politique, la décision finale et le reçu |
| `Rift.Scripting/Runs/IRunHost.cs` (`IRunGuard`, `GuardReport`, `RunTarget`, `IRunHost`) | garde lecture abstrait, faké dans les tests | + `IWriteGuard` (groupe cible nommé, décision Garder/Annuler, reçu), `ChangeReceipt`, `IAskDialog` ; `IRunHost.CreateWriteGuard(title)` et `IRunHost.Ask` |
| `Rift.Revit/Guard/ReadGuard.cs` | groupes toujours annulés, événements de blocage, `FailuresProcessing` | inchangé ; `WriteGuard` en est le pendant écriture (même squelette d'événements) |
| `Rift.Core/Policy/DocumentClassifier.cs` | classe de document par nom de fichier | lu par la politique d'écriture (`WritePolicy`, fonction pure) |
| `Rift.Core/Jobs/Job.cs`, `JobTable.cs`, `Protocol/Ops/JobOp.cs` | table de jobs, `op: job` lit l'état | `Job` porte un `CancellationTokenSource` ; `op: job` avec `cancel: true` annule (en file → `CANCELLED` sans exécution ; en cours → jeton annulé, le script s'arrête à la prochaine trace) |
| `Rift.Core/Journal/RunJournal.cs` | un dossier par run, index, purge | + historique par document (`history/<clé>.jsonl`) : reçus de `run_modify`, raison attachée par `record_change` |
| `Rift.Scripting/Runs/AuditOp.cs` | tout `op: tool` (presets) | devient un handler d'outil derrière `ToolOp`, qui route `audit.*` et `history.*` |
| `Rift.Revit/Ops/ContextOp.cs` | `policy {read_only: true, milestone: M1, run_modes: [query]}` | `read_only: false`, `M2`, `[query, modify]`, classe du document et politique qui s'appliquerait |
| `server/src/rift_mcp/tools/run_query.py` | compile puis run en query | `run_modify.py` : lint (ADR-017) puis compile puis run en modify ; `history.py`, `jobs.py` |
| `server/src/rift_mcp/registry.py` | `CURRENT_WAVE = "M1"` | `M2` ; `run_modify`, `record_change`, `get_change_records`, `cancel_job` passent `implemented` ; prompt `change` implémenté |

## Ce que ça touche

**Add-in** — `Rift.Scripting` (sans Revit) : `WritePolicy`, `ChangeReceipt`, `IWriteGuard`, `IAskDialog`, le chemin `modify` de `RunQueryOp`, `ToolOp`, `HistoryOp`. `Rift.Core` : `Job.Cancel`, `JobOp` (cancel), `RunJournal` (historique), `ErrorCodes` (rien de nouveau : `CANCELLED`, `SANDBOX_REQUIRED`, `EXPECTED_DOCUMENT_REQUIRED` existent). `Rift.Revit` : `WriteGuard`, `AskDialog`, `RevitRunHost` (garde d'écriture, boîte), `ContextOp`, `Host` (câblage, `history/`).

**Serveur** — `lint.py` (parso), `tools/run_modify.py`, `tools/history.py`, `tools/jobs.py`, `server.py` (quatre outils, prompt `change`), `registry.py`.

**Hors code** — `docs/PROTOCOL.md` (`run` en `mode: modify`, `job` avec `cancel`, `tool: history.*`), ADR-031 (décisions M2 non couvertes), README/pamphlet régénérés, SECURITY.md (état), CHANGELOG.

**Aucune page Notion, aucun fichier de référence** : M2 n'écrit que dans des copies SANDBOX (ou, sur décision explicite de l'utilisateur dans Revit, un document non enregistré ou de référence).

## Flux de bout en bout (`run_modify`)

1. **Serveur.** `run_modify(script, title, expected_document, dry_run=True, policy="auto", allow_reference=False, lint=True, timeout_s)` : `expected_document` obligatoire (sinon `EXPECTED_DOCUMENT_REQUIRED` sans rien envoyer) ; lint parso (erreurs → `LINT_ERROR` avec les constats, rien n'est envoyé ; avertissements renvoyés avec le résultat) ; `op: compile` ; `op: run {mode: modify, dry_run, policy, allow_reference, title, inputs, timeout_s}` avec `expected_document`.
2. **Add-in, avant tout.** Invariant de timeout ; document actif présent et conforme à `expected_document` (`NO_DOCUMENT`, `DOCUMENT_MISMATCH`) ; **politique** (ADR-009, fonction pure) : `sandbox` → politique demandée (`auto` par défaut) ; `unsaved` → `ask` forcé ; `reference` → `ask` forcé si `allow_reference`, sinon `SANDBOX_REQUIRED` ; `unmarked` → `SANDBOX_REQUIRED`. Tout refus est `refused`, avant compilation.
3. **Run.** Compile ; journal ; `WriteGuard.Arm(title)` : `TransactionGroup` **nommé `Agent: <titre>`** sur le document cible, groupes « lecture » (toujours annulés) sur les autres documents ouverts, événements : `DocumentChanged` (collecte ajoutés/modifiés/supprimés par catégorie sur la cible, compte les transactions ; écriture ailleurs notée `other_documents`), pré-événements save/save-as/close/sync/export annulés et nommés, `FailuresProcessing` (ADR-010 : avertissements supprimés, erreurs → rollback silencieux, `revit_failure`), `DialogBoxShowing` (ADR-009 : boîtes du script écrasées `Cancel(2)`, journalisées ; la boîte `rift.ask` est la nôtre et passe). Le moteur exécute avec le jeton d'annulation du job.
4. **Décision.** Après le script : transaction laissée ouverte → annulée, `open_transaction_left` ; erreur / timeout / annulation / échec Revit → **RollBack** (`rolled_back`, raison) ; sinon `dry_run` → **RollBack** (`rolled_back`, `dry_run`) ; sinon `auto` → **Assimilate** (`committed`, une entrée Annuler) ; sinon `ask` → sélection des éléments touchés, `TaskDialog rift.ask` Garder / Annuler (Annuler par défaut) → Assimilate ou RollBack (`user_discarded`). Les groupes des autres documents sont toujours annulés.
5. **Reçu.** `changes {added, modified, deleted, transactions, by_category {catégorie: {added, modified, deleted}}, samples [{id, category, name}] (≤ 20)}`, `history_id` (= `run_id`), `other_documents`, `dialogs`, `failures`, `policy_applied`, `dry_run`, `blocked`, `result`, `stdout`… Une ligne est ajoutée à `history/<clé du document>.jsonl` (clé = SHA-256 du chemin, titre expurgé) pour tout run en `modify`, dry-run inclus.
6. **Suite.** `record_change(reason, history_id?)` complète la dernière ligne (ou celle donnée) ; `get_change_records(limit)` les lit pour le document actif ; `cancel_job(job_id)` annule un job en cours (`CANCELLED`, tout annulé) ou en file.

Threads inchangés : tube, API Revit (run, boîte Garder/Annuler, carte), serveur CPython. La boîte `ask` est modale **dans le job** : la carte ne s'affiche pas par-dessus (ADR-023) ; le tube répond `accepted` et le client suit le job.

## Données et paramètres Revit

Lus : `Document.PathName` (classe), `DocumentChangedEventArgs.GetAddedElementIds/GetModifiedElementIds/GetDeletedElementIds` (reçu), `Element.Category.Name`, `Element.Name` (échantillons, assainis ADR-020). Écrits : uniquement ce que le script de l'assistant écrit, sous le groupe `Agent: <titre>` ; la sélection (`Selection.SetElementIds`) pendant la boîte `ask` puis remise à vide. Rien n'est écrit dans le modèle par RIFT lui-même.
