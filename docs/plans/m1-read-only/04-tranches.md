# Tranches : M1 — lecture seule de bout en bout

Porte 4, rédigée le 2026-10-09 et amendée le même jour pour ADR-026 §7 (obligations à fermer avant tout écouteur, tranche 3 au plus tard). Chaque tranche est verticale (serveur → tube → add-in → Revit → retour), exécutable, et se termine par un commit avec ses tests. Les numéros de tests renvoient au plan de la porte 3. Chaque tranche qui touche Revit se recette sur une copie `SANDBOX` ; la session Revit est celle de l'utilisateur.

## Tranche 1 — balle traçante : `get_context` traverse tout, la carte s'allume

**Ce qui devient vrai.** Depuis Claude Code, `get_context` renvoie le titre du document actif et la version de Revit, en passant par le vrai tube signé, le vrai dispatcher dans le contexte isolé, et la carte d'activité affiche « Assistant connecté » puis `get_context · réussi`.

**Contenu.** `PipeListener` + `PipeAcl` (nom simple seulement), `RequestAuthenticator` réduit au mac (fenêtre de temps et nonces acceptés tels quels), `SecretStore` sans rotation, `DiscoveryFile.Write` sans purge, `RequestRouter` avec `health`, `lease`, `context` ; `JobTable` minimale (attente bornée, pas de `job`) ; `RevitDispatcher.Create/Enqueue/Execute` ; `ContextOp` réduit (titre, chemin, version, build) ; `ActivityCard` en code avec `ShowConnected`/`ShowResult`/`Hide` ; serveur : `discovery.list_instances` + `read_secret`, `RevitClient.call` sans renouvellement, `server.py` avec le seul outil `get_context`, enregistrement manuel dans Claude Code.

**Bouchonné.** `LeaseOp` accorde toujours ; pas de `psutil` ; `BusyProbe` renvoie `null` ; `Status` inchangé ; `config.json` ignoré (carte toujours active).

**Preuve.** Tests 1, 2 (partie `health`), 8 (partie `op` inconnu et `PROTOCOL_MISMATCH`), 15. Dans Revit : capture de la carte et ligne du log `alc=rift` dans `Execute` — ce qui lève les incertitudes 1 et 2 de la porte 3. Si l'une des deux tombe, on applique le repli indiqué **avant** la tranche 2.

## Tranche 2 — un seul client, et c'est le bon

**Ce qui devient vrai.** Critère 2 du produit : requête non signée → `UNAUTHORIZED` sans exécution ; nonce rejoué, horodatage hors fenêtre, dix échecs → rotation ; second assistant → `LEASE_HELD {holder}` affiché par la carte et par Status ; le serveur vérifie pid et date de création avant le premier appel et renouvelle le bail à chaque requête.

**Contenu.** `NonceCache`, `RequestAuthenticator` complet, `SecretStore.Rotate` + `generation`, `DiscoveryFile` avec ACL et purge, `LeaseManager` complet avec `Granted`, `EnqueueUi`, `StatusReport` (détenteur, depuis), `Host.Stop` qui supprime `.json`/`.key` ; serveur : `verify_alive`, `choose_target`, `ensure_lease`, codes `NO_TARGET`/`AMBIGUOUS_TARGET`/`TARGET_CHANGED`/`TARGET_BUSY` ; `PipeHost` héberge le vrai écouteur avec `FakeHandler`.

**Preuve.** Tests 3, 4, 5, 7, 16, 18 (partie bail et erreurs transport), 21 (intégration CI complète). Dans Revit : scénarios 10 et 12 du benchmark rejoués et consignés ; carte sur `LEASE_HELD`.

## Tranche 3 — `run_query` qui ne peut pas abîmer la maquette

**Ce qui devient vrai.** Critère 3 : un script libre en mode lecture s'exécute avec la deadline coopérative ; toute écriture déguisée est annulée et signalée `rolled_back / WRITE_IN_QUERY` ; sauvegarde, export, fermeture pendant le run sont bloqués et nommés ; la pile Annuler reste vide ; une erreur de syntaxe revient avec sa ligne sans rien exécuter ; `import clr` est refusé ; un script trop long bascule en `accepted {job_id}` et se termine par `job`.

**Contenu.** `CompileOp`, `RunQueryOp`, `ReadGuard` + `DocumentClassifier`, `ImportGuard` et sous-ensemble fermé de la bibliothèque standard, `Extras` (`DB`, `UI`, `System`, `eid`), `RevitJson` + assainisseur, `RunJournal` + `Redactor`, `JobOp` + rétention, `BusyProbe` réel (`REVIT_BUSY` nommant la modale) ; **ADR-026 §7** : invariant de timeout dans `Rift.Scripting` (`Run` refusé sans hook vivant → `TIMEOUT_UNAVAILABLE`), `ExceptNarrower` par l'AST (`KeyboardInterrupt`, tuples), `BoundedWriter` 1 MiB et `result_ref` au-delà de 700 KiB ; `RiftConfig` (marqueurs sandbox, `start_timeout_s`) ; serveur : outil `run_query` (`compile` puis `run`), `wait_job`, `READ_ONLY_MODE` pour tout `mode ≠ query`.

**Preuve.** Tests 6, 8 (complet), 9, 12, 13, 14, 15a, 15b, 15c, 18 (complet), 19 (partie `run_query`). Dans Revit : test 23 (Annuler vide, `IsModified`, taille et date du fichier), scénarios 3, 8, 9, 11 du benchmark, carte sur `WRITE_IN_QUERY` et `REVIT_BUSY`.

## Tranche 4 — trois premiers inventaires à parité

**Ce qui devient vrai.** `audit_template p1|p2|p3` renvoie les documents `01_vues_lignes_cartouches`, `02_notes_cotations`, `03_pochages` avec les mêmes clés et valeurs que les scripts actuels ; `write_to` les écrit à l'endroit attendu par la chaîne de comparaison.

**Contenu.** `IResultCollector` + `MemoryResultCollector`, `dump` exposé au script, `PresetCatalog`, portage de p1-p3 (clé de tri `str(...)`, `_document`, `_langue`, `_generated` conservés), `AuditOp` par préset, outil `audit_template` avec `write_to` (risque `ask`), `FIXED_READ_OPS` et registre avec `risk`.

**Preuve.** Tests 10, 11 (p1-p3), 17, 19 (partie `write_to`). Dans Revit : premier passage de `p8` puis `p9` sur les domaines 01 à 07 et 11 à 12 → comparatif vide ; **tant qu'il ne l'est pas, la tranche 5 n'ouvre pas** (incertitude 4).

## Tranche 5 — audit complet en une demande

**Ce qui devient vrai.** Critères 1 et 5 : `audit_template all` produit les six documents dont `00_TOUT.json` (fusion, résumé, légendes sur feuilles) dans un seul job avec progression, le comparatif `p9` est vide sur les douze domaines, et la durée ne dépasse pas celle de l'ancienne chaîne.

**Contenu.** Portage de p4, p5 et de la fusion p6 lisant le collecteur, `audit.all` enchaîné avec `report_progress`, mesure de durée consignée.

**Preuve.** Tests 11 (complet), 19 (complet). Dans Revit : tests 24 et 25, résultats datés dans `00-status.md`.

## Tranche 6 — installé en une commande, recette M1

**Ce qui devient vrai.** Critère 4 : `rift-mcp install claude` enregistre le serveur via la CLI `claude` ou affiche la commande et le bloc à coller ; `permissions --format claude` et `schemas` sont générés depuis le registre et vérifiés en CI ; le bouton Status permet de couper la carte et `config.json` la mémorise ; `doctor` dit quelle instance est vue et pourquoi pas.

**Contenu.** `install.py`, `cli.py` réel, `schemas --check`/`permissions --check` effectifs, table des outils du README générée, `SetActivityCard` + `StatusCommand` avec lien de commande, `shared/schemas/transport-pipe.md`, CHANGELOG, `addin/README.md`, `docs/PROTOCOL.md` final ; **ADR-026 §7** : `SHA256SUMS.txt` produit par `Stage` et vérifié en CI, ADR-002/004/008 réécrits pour le transport par tube, README et SECURITY.md disant ce qui est implémenté et ce qui est conçu.

**Preuve.** Tests 15d, 17 (génération), 20. Dans Revit : test 22 complet (carte sur chaque scénario), installation depuis un poste « vide » simulée par `install.ps1 -Uninstall` puis réinstallation et `install claude` ; les cinq critères de la porte 1 cochés avec leurs mesures dans `00-status.md`. Clôture de M1 et relecture de la règle d'arrêt (2 × 24 j-p).

## Hors périmètre

- `run_modify`, dry-run, Garder/Annuler, politique `auto`/`ask`, lint `parso`, `cancel` de job : M2.
- Repli HTTP (`HttpListener`), transport asynchrone côté serveur, signature de code, MSI : v1.1 ou plus.
- Autres versions de Revit que 2026, autres assistants que Claude Code (Claude Desktop reste une ligne de documentation), export vers Notion ou tableur, rapport fusionné : hors M1.
- Toute copie des scripts d'extraction dans le dépôt : ils sont l'oracle, les presets en sont le portage.
