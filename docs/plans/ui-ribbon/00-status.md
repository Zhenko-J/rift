# État : chantier UI — bandeau et identité visuelle (M5)

Chantier séparé ouvert le 2026-10-12 en application d'ADR-026 §5. Il a ses propres portes et ne bloque jamais M1-M2 : **conception seulement** tant que M2 n'est pas livré. Seule la carte d'activité (ADR-023) est livrée avec M1.

- Porte 1 — Produit : APPROUVÉE 2026-10-13 (`01-produit.md`) — noms (RIFT dans l'onglet NORN), Z_D01 partout, carte sans numéral, pastille rouge pour les échecs : questions ouvertes toutes tranchées
- Porte 2 — Architecture : APPROUVÉE 2026-10-13 (`02-architecture.md`) — les 8 points retenus tels que proposés (option (a) pour le point 5) ; porte 1 retouchée en conséquence (`THIS SESSION`, `copy log path`) ; → ADR-029
- Porte 3 — Conception : APPROUVÉE 2026-10-13 (`03-conception.md`) — projet `Rift.Ui` retenu, pastille vide pour `PAUSED` confirmée ; → amendement d'ADR-029 (§9-10)
- Porte 4 — Plan de tranches : APPROUVÉE 2026-10-13 (`04-tranches.md`) — 6 tranches, balle traçante = pause de bout en bout ; aucune n'ouvre avant la livraison de M2 (prochaine étape du chantier : tranche 1, après M2)

## Tranches
- [x] Tranche 1 — balle traçante : un clic met l'assistant en pause, la carte le dit (`Rift.Ui`, `PauseGate`, ruban dans `Rift.Revit`, `PAUSED` côté client)
- [x] Tranche 2 — le panneau complet, avec ses icônes (Lucide, `developer`, carte mémorisée)
- [x] Tranche 3 — le kit Z_D01 et la fenêtre Status
- [x] Tranche 4 — History : ce que l'assistant a fait pendant la séance
- [x] Tranche 5 — Tools, recette complète et clôture (hors reçus)
- [x] Tranche 6 — reçus de changement dans History (après M2)

## Cible (point de départ de la porte 1)

Panneau `RIFT` dans l'onglet `NORN` partagé par la suite de plugins du mainteneur (ADR-028), expérience calquée sur le bandeau de rvt-mcp (ON/OFF, état, historique, carte, boîte de réception des commandes figées), restylée selon la charte « Z#D01 » du mainteneur.

| Panneau | Bouton | Action | Quand |
|---|---|---|---|
| Server | ON / OFF | `Host.Pause()` / `Resume()` : révoque le bail, l'écouteur répond `PAUSED` ; `PushButton` dont le texte et l'icône sont permutés à l'exécution (l'API n'a pas de bouton bascule) | M5 (pas d'op de protocole : la pause se commande depuis Revit, porte 2) |
| Server | Status | TaskDialog avec liens « Activity card on/off » et « copy log path » (pas de processus lancé, porte 2 point 5) | M1 T6 |
| Activity | History | fenêtre WPF construite en code (`Ui/HistoryWindow.cs`) : table de jobs + journal | M5 |
| Activity | Card | interrupteur persisté dans `config.json` (`activity_card`) | lien Status en M1 T6, bouton en M5 |
| Bake | Inbox | commandes figées proposées, en attente d'acceptation humaine (ADR-020) | M5, après M4 |
| Help | Tools | fenêtre catalogue lisant le `tools.json` embarqué (même source que le pamphlet) ; pas de lancement de navigateur depuis `Rift.Revit` | M5 |

## Contraintes déjà connues
- Toute `IExternalCommand` vit dans l'assembly d'amorçage `Rift.Addin` (`App.cs`) et relaie par réflexion par un point d'entrée unique `Host.Invoke(string command, object? arg)` ; le ruban lui-même est construit dans `Rift.Revit` (porte 2 point 1).
- Fenêtres construites en code, sans XAML (ADR-021/023), créées sur le thread de l'API via `EnqueueUi`.
- Icônes Lucide (licence ISC → `THIRD_PARTY.md`) : tracés copiés dans le code et rendus à l'exécution en 16/32 px à l'échelle du moniteur (porte 2 point 4) ; repli : PNG générés au build.
- Couleurs, rayons et police dans un seul `Ui/Theme.cs` qui remplace les littéraux de `ActivityCard.cs`.
- Textes d'interface en anglais (langue du dépôt).

## Ce qui peut passer tôt (M1 T6, sans porte)

- 2026-10-13, à la demande du mainteneur (boutons sans icône, doublon d'onglet) : **fait en avance** — ruban construit dans `Rift.Revit` (`Ui/Ribbon.cs`, ADR-029 §1 ; `Host.Start(app, bootstrapPath)`), icônes Lucide rendues au démarrage à l'échelle du moniteur (`Ui/Icons.cs`, `gauge` pour Status, `flask-conical` pour Probe Python), « Probe Python » masqué sauf `developer: true` dans `config.json`. `Icons.cs` vit dans `Rift.Revit` jusqu'à la création de `Rift.Ui` (tranche 1 du chantier), compilé en lien dans `Rift.Tests` pour être testé. Doublon : l'ancien add-in `zenrvt` est retiré par `install.ps1 -RemoveLegacy`, Revit fermé.

Status avec interrupteur de carte, `Ui/Theme.cs` appliqué à la carte, icônes PNG des boutons existants, « Probe Python » masqué derrière `config.json` `developer: true`.

## Tranche 1 — balle traçante (code, 2026-10-16)

- **Fait, hors Revit.** Cœur : `Rift.Core/Control/PauseGate.cs` (`Pause`/`Resume`/`Toggle`, révocation du bail, annulation des jobs non démarrés en `PAUSED` refusé, `Changed`), `Job.Finished`, `JobTable.CancelNotStarted`, porte de pause du `RequestRouter` avant la recherche du handler et avant le bail (seconde vérification juste avant la création d'un job). Nouveau projet **`Rift.Ui`** (sans API Revit) : `Theme.cs` (13 couleurs de la porte 1, tailles, rayons, Consolas, pinceaux gelés), `CardContent` (connecté, résultat, en pause, repris ; un refus `PAUSED` = pastille vide), `ActivityCard` déplacée et passée au minimum Z_D01 (carte `#EAE9E6`, coins 20 px, pastille 22 px, micro-libellé `RIFT · OUTIL`, aparté, code et message seulement en échec ; plus de liseré). Ruban : grand bouton `server on` / `paused` (icône Lucide `power` en accent tant que le serveur tourne, `pause` à l'encre en pause), `status` et `probe python` en minuscules ; **infobulles et descriptions longues** sur chaque bouton (textes de la revue d'interface du 2026-10-16, voix Z_D01) ; `AvailabilityClassName` : `server` et `status` utilisables sans document ouvert. Amorçage : `App.Invoke` (point d'entrée unique par réflexion), `RelayCommand`, `ServerToggleCommand`, `AlwaysAvailable`. Hôte : `PauseGate` créé avec le bail et la table des jobs, `Host.Invoke("server.toggle")`, ligne `server:` dans Status. Client Python : `PAUSED` oublie le bail, sans nouvel essai. PipeHost : `RIFT_PIPEHOST_PAUSED=1`. `PROTOCOL.md` (sémantique de `PAUSED`), ADR-021 amendé, `THIRD_PARTY.md` (Lucide, ISC).
- **Tests.** 1 à 4 (`PauseGateTests`), 10 (palette), 15 en partie (carte : `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, jamais activée ni au premier plan, une seule pastille accent pour un appel en cours, carte désactivée = invisible ; apartés en minuscules sans point), 20 en partie (`Rift.Ui` sans API Revit, aucun `Topmost = true`), 21 et 22 (pytest). 352 xUnit, 137 pytest.
- **Écarts.** `Icons.cs` reste dans `Rift.Revit` (déplacement en tranche 2) ; la carte n'a pas encore l'ombre ni l'interlettrage (tranche 3) ; le libellé du bouton tient sur deux lignes (`server` / `on`) pour rester dans la largeur d'un grand bouton ; le ruban est rafraîchi à la fois directement dans la commande et par `Changed` (incertitude 4 : on garde les deux).
- **Reste : recette 23 dans Revit** (copie SANDBOX) — clic `server on` → `paused`, dix `get_context` → dix `PAUSED`, aucune ligne `execute op=` pendant la pause, second clic → l'appel suivant passe ; carte servie depuis `Rift.Ui` (incertitude 1) ; infobulles lues au survol.
- **2026-10-16 — recette 23 dans Revit 2026.3** (add-in `0.1.0+fa54c72`, copie SANDBOX, un seul client). Démarrage : `alc=rift`, ruban `NORN/RIFT` construit, **carte créée depuis `Rift.Ui` sans erreur** (incertitude 1 levée : un second assembly WPF du contexte `rift` se charge dans Revit). Clic du mainteneur sur `server on` → journal `server paused by you` ; dix `get_context` envoyés aussitôt → **dix `PAUSED`** (3 à 7 ms chacun, 44 ms au total) ; **aucune ligne `execute op=`** entre la pause et la reprise ; second clic → `server resumed`, l'appel suivant reprend le bail et passe (`ok`, 23 ms). Critère 1 du produit tenu. Reste à consigner par le mainteneur : aspect du bouton (libellé et icône permutés), infobulles et descriptions longues au survol, carte « paused by you » à pastille vide puis « resumed ».

## Tranches 2 à 6 — code (2026-10-16)

- **Fait, hors Revit.** Ruban : boutons `history` (Lucide `history`) et `tools` (Lucide `list`), infobulles et descriptions longues, utilisables sans document ; commandes `StatusCommand`, `HistoryCommand`, `ToolsCommand` relayées par `App.Invoke` (`status.open`, `history.open`, `tools.open`), réflexions devenues inutiles retirées d'`App`. Cœur : `Rift.Core/Activity/ActivityLog.cs` (`RequestRecord`, `ChangeSummary` lu dans `result.changes.by_category`, anneau de 500, mise à jour en place par clé, une ligne finie ne repasse jamais en cours), `RequestRouter.Recorded` (un enregistrement par requête, aucun pour `op: job`, second enregistrement sous la même clé à `Job.Finished`). `Rift.Ui` : kit `Kit/Z.cs` (carte, numéral, micro-libellé interlettré à 0,2 em, aparté, valeur, pastille et grille de 7, tuile en creux, bouton fantôme 44 px), `Views.cs` (`StatusSnapshot`, `IUiHost`, `HistoryView`, `ToolCatalog` lu dans `tools.json` embarqué), `Windows/` (`RiftWindow` : non modale, propriétaire Revit, Échap ferme, rafraîchie seulement quand l'empreinte change ; `UiWindows` : une instance par fenêtre ; `StatusWindow`, `HistoryWindow`, `ToolsWindow`). Hôte : implémente `IUiHost`, journal d'activité alimenté par le routeur et par les refus d'authentification (sous la même limite de 2 s que la carte), fenêtres fermées à l'arrêt ; Status retombe sur le texte si la fenêtre ne s'ouvre pas.
- **Tests.** `ActivityTests` : clé unique mise à jour en place, anneau, reçus par catégorie, routeur (une ligne par requête, jamais le sondage, job long accepté puis fini sous la même clé), formats de History, catalogue embarqué = fichier du pamphlet, les trois fenêtres construisent leur corps sans Revit. 386 xUnit.
- **Écarts.** Pas de bouton `card` dans le ruban : l'interrupteur reste dans Status ; `Icons.cs` reste dans `Rift.Revit` ; ombre de la carte et `ReduceMotion` non faits ; pas de capture Z_D01 automatique (ZAudit).
- **Reste : recettes 24 à 28 dans Revit** (copie SANDBOX) — ouvrir Status, History et Tools ; un `run_modify` gardé → la ligne History montre les mêmes comptes que la réponse ; un `run_query` long qui finit sans être sondé ; rafale de 20 appels ; captures.

## Clôture du code (2026-10-16)

- **Fait.** Bouton `card` du ruban (Lucide `message-square`, libellé `card on`/`card off`, mémorisé dans `config.json`), ombre unique sous la carte d'activité (`Theme.CardShadow`), bouton et fenêtre `inbox` des commandes figées (ADR-034, le script entier est montré, seul l'utilisateur accepte), fenêtre de séance (ADR-033) ; l'option choisie de la fenêtre de séance et du filtre de Tools est remplie de l'accent (retour de la recette du 2026-10-16). Les six tranches sont codées ; `Icons.cs` reste dans `Rift.Revit` (écart accepté : rien ne l'utilise hors du ruban).
- **Recette du 2026-10-16 (séance).** Séance en attente → `SESSION_PENDING` (17 ms, rien d'exécuté) ; réponse « sandbox · supervised » puis « full auto » via `change session`, lues par `get_context` ; essai à blanc sans dialogue en mode supervisé ; écritures gardées refusées par `AGENT_CONFLICT` (pyRevit MCP et BIMWright chargés) — `coexist_with_other_agents` activé par le mainteneur pour la recette suivante.
- **Reste : recettes 24 à 28 dans Revit** avec l'add-in à jour (Status, History, Tools, inbox, card, Garder/Annuler en mode supervisé, full auto).

## Recette M4 et M5 dans Revit 2026.3 (2026-10-16 soir)

Add-in `962109b`, copie SANDBOX, pyRevit MCP et BIMWright chargés, `coexist_with_other_agents: true`, une seule séance MCP.

| Point | Résultat |
|---|---|
| Séance en attente → écriture refusée | `SESSION_PENDING`, rien d'exécuté |
| « sandbox · supervised » | essai à blanc sans dialogue ; écriture gardée → Garder/Annuler dans Revit, **Keep** → `committed`, `policy_reason: supervised_session` |
| `change session` → « full auto » | écriture gardée sans dialogue, `policy_applied: auto` |
| C# `run_query` | 64 vues lues, `print` dans stdout, 67 ms |
| C# hors liste blanche (`System.IO.File`) | refusé à la compilation, rien d'exécuté |
| C# boucle infinie avec `catch {}`, délai 3 s | `TIMEOUT` à 3 s, Revit répond aussitôt après |
| C# `run_modify` (renommer une feuille, `Transaction` dans le script) | gardé, reçu « Sheets: 1 modified » |
| Commande figée | refusée avant acceptation ; acceptée dans la boîte de réception ; exécutée sous empreinte vérifiée (le script de test lui-même avait une erreur de nom) |
| Exports | PDF, DWG (+ .pcp), PNG, IFC écrits ; second PDF sans `overwrite` → `FILE_EXISTS` ; NWC → `EXPORTER_UNAVAILABLE` ; dossier de RIFT → refusé ; CSV de nomenclature écrit |
| `save_document` | confirmation dans Revit, **no** → `USER_DECLINED`, rien d'enregistré |
| `close_document` sur le document actif / `sync_with_central` hors travail partagé | refusés avec la raison |
| Nettoyage en full auto | feuilles de test supprimées |

Corrigé après la recette : le refus C# s'affichait « SyntaxError at line 1 » et une erreur de `run_query` « NameError at line 1 » ; le message du script (ou les symboles refusés) est maintenant celui que reçoit l'assistant.
