# Tranches : M2 — écritures gardées

Rédigé le 2026-10-13 (autopilote, à valider). Chaque tranche est verticale, exécutable et se termine par un commit avec ses tests ; la recette dans Revit se fait sur une copie `SANDBOX`.

## Tranche 1 — une modification gardée, à blanc par défaut

**Ce qui devient vrai.** Critères 2 et 3 (partie refus) : `run_modify` sur une copie SANDBOX s'exécute sous un groupe nommé, revient avec son reçu de changement et ne laisse rien (dry-run) ou une seule entrée Annuler (`dry_run: false`, `auto`) ; hors SANDBOX la demande est refusée avant d'avoir rien compilé ; le serveur refuse sans envoyer un script que le lint rejette.

**Contenu.** `WritePolicy`, `ChangeReceipt`, `IWriteGuard`/`IRunHost` étendus, chemin `modify` de `RunQueryOp`, `RunArgs` ; `WriteGuard` (groupes, collecte, blocages, échecs ADR-010), `RevitRunHost`, `ContextOp` ; `RunJournal.AppendHistory` ; serveur `lint.py`, `tools/run_modify.py`, `server.py`, `registry.py` (`M2`) ; PipeHost : faux garde d'écriture.

**Preuve.** Tests 1, 2 (a-d, g-i, k), 7, 8, 10 (dry-run, committed). Dans Revit : critères 1, 2, 3 (refus), 4 (erreur, timeout, échec Revit).

## Tranche 2 — Garder / Annuler dans Revit

**Ce qui devient vrai.** Critère 3 (partie `ask`) : sur un document non enregistré ou de référence autorisé, ou sur demande explicite `policy: ask`, Revit montre les éléments touchés et demande Garder / Annuler, Annuler par défaut ; les boîtes de dialogue ouvertes par le script sont écrasées et journalisées.

**Contenu.** `AskDialog`, `IAskDialog`, `DialogBoxShowing` dans `WriteGuard`, `RevitRunHost.Ask`.

**Preuve.** Tests 2 (e, f, j). Dans Revit : critère 3 complet, scénario 5 du benchmark, carte sur `user_discarded`.

## Tranche 3 — on sait ce que l'assistant a fait

**Ce qui devient vrai.** Critère 5 : `get_change_records` liste les modifications de la séance pour le document actif, `record_change` y attache la raison ; le prompt MCP `change` guide l'assistant (survey → à blanc → garder → raison).

**Contenu.** `ToolOp`, `HistoryOp`, `RunJournal.ReadHistory/AttachReason`, serveur `tools/history.py`, prompt `change`.

**Preuve.** Tests 3, 4, 9. Dans Revit : critère 5.

## Tranche 4 — arrêt, protocole, clôture

**Ce qui devient vrai.** Critère 4 (annulation) : `cancel_job` arrête un script en cours et tout est annulé ; `docs/PROTOCOL.md` décrit `mode: modify`, `job.cancel`, `history.*` ; le catalogue, le README, le pamphlet et les permissions sont régénérés ; ADR-031 consigne les choix d'autopilote.

**Contenu.** `Job.Cancel`, `JobTable.TryCancel`, `JobOp`, serveur `tools/jobs.py`, PROTOCOL.md, ADR-031, CHANGELOG, SECURITY.md.

**Preuve.** Tests 5, 9, 10 (cancel). Dans Revit : critère 4 (annulation), recette des cinq critères consignée, clôture M2.

## Hors périmètre

- Outils typés, `survey`, `batch`, `result_ref` : M3.
- C#, commandes figées, exports, cycle de vie du document : M4.
- Fenêtre History et interrupteur ON/OFF dans Revit : chantier UI (M5).
