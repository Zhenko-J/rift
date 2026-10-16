# Tranches : chantier UI — bandeau, fenêtres et carte d'activité (M5)

Porte 4, rédigée le 2026-10-13 après l'approbation de la porte 3. Chaque tranche est verticale (clic dans Revit ou appel de l'assistant → add-in → écran), exécutable, et se termine par un commit avec ses tests. Les numéros renvoient au plan de tests de la porte 3 (1-22 automatiques, 23-28 recettes dans Revit). Toute recette se fait sur une copie `SANDBOX` ; la session Revit est celle du mainteneur.

**Quand.** Aucune tranche n'ouvre avant la livraison de M2 (ADR-026 §5). Si M1 tranche 6 a déjà livré les éléments « tôt » (thème, carte restylée, icônes des boutons existants, « Probe Python » masqué), les tranches 1 et 2 ne reprennent que ce qui manque et le notent dans `00-status.md`.

## Tranche 1 — balle traçante : un clic met l'assistant en pause, la carte le dit

**Ce qui devient vrai.** Critère 1 du produit, de bout en bout : dans Revit, un clic sur le bouton du panneau `RIFT` met le serveur en pause ; tout appel de l'assistant reçoit `PAUSED` sans rien exécuter, le client oublie son bail ; la carte, servie depuis le nouveau projet `Rift.Ui`, affiche la pastille vide `paused by you` ; second clic → l'appel suivant passe.

**Contenu.** Projet `Rift.Ui` (csproj, ajout à `rift.sln`, `deps.json`, cible `Stage`) avec `Theme.cs` minimal et `ActivityCard` déplacée + `CardContent` (états connecté, résultat, en pause, repris) ; `PauseGate`, `JobTable.CancelNotStarted`, `Job.Finished`, porte de pause dans `RequestRouter` ; bootstrap : `App.Invoke`, `RelayCommand`, `ServerToggleCommand` (+ `StatusCommand`, `ProbePythonCommand` relayés) ; `Ribbon.Build` dans `Rift.Revit` avec le bouton on/off (libellés permutés `server on` / `paused`) ; `Host.Start(app, bootstrapPath)`, `Host.Invoke` ; client Python : `PAUSED` ; PipeHost : `RIFT_PIPEHOST_PAUSED` ; `docs/PROTOCOL.md` (`PAUSED`), ADR-021 renvoyé à ADR-029 §9.

**Bouchonné.** Boutons sans icône ; Status reste la boîte de dialogue texte actuelle (avec une ligne « server: paused since … ») ; pas de journal d'activité ni de fenêtres ; la carte garde son minimum Z_D01 (fond, pastille, police) sans le kit complet.

**Preuve.** Tests 1, 2, 3, 4, 10, 15 (partie fenêtre non activée et pastille vide), 20 (partie `Rift.Ui` sans API Revit et bootstrap), 21, 22. Dans Revit : recette 23 ; ligne de démarrage `alc=rift` et carte créée depuis `Rift.Ui` (lève l'incertitude 1) ; changement du bouton juste après le clic (incertitude 4). Si l'incertitude 1 tombe : repli de la porte 3 (fusion dans `Rift.Revit`) **avant** la tranche 2.

## Tranche 2 — le panneau complet, avec ses icônes

**Ce qui devient vrai.** Le panneau `RIFT` de l'onglet `NORN` a sa forme finale : on/off (grand), status, history, card (pile de trois), tools (grand), « Probe Python » seulement avec `developer: true` ; icônes Lucide nettes à 100 % et 150 % ; l'icône « on » est le seul élément orange du ruban ; `card` bascule la carte et `config.json` s'en souvient d'une séance à l'autre ; history et tools ouvrent pour l'instant une boîte texte « coming in this work stream » (aucune action cachée).

**Contenu.** `Icons.cs` (tracés Lucide + rendu), `Ribbon` complet (`SetPaused`, `SetCard`, pile, séparateurs, échelle du moniteur), `CardCommand`, `HistoryCommand`, `ToolsCommand`, `RiftConfig.Developer` + `WithActivityCard`, `Host.SetActivityCard` persistant, `THIRD_PARTY.md` (Lucide, ISC).

**Bouchonné.** History et Tools (boîte texte) ; Status toujours en texte.

**Preuve.** Tests 9, 16, 19, 20 (complet). Dans Revit : recette 28 (onglet partagé) ; partie « icônes » de la recette 25 à 100 % et 150 % (incertitude 3 ; repli PNG au build appliqué dans cette tranche s'il le faut) ; place dans l'onglet (incertitude 8).

## Tranche 3 — le kit Z_D01 et la fenêtre Status

**Ce qui devient vrai.** Status devient une vraie fenêtre Z_D01 (non modale, propriétaire Revit, une seule instance) : numéral `CALLS SERVED`, tuiles client et depuis quand / état du serveur / tube / journal, interrupteur `activity card`, boutons `pause` (même effet que le ruban) et `copy log path` ; la carte passe au kit complet (micro-libellé interlettré, aparté, code en échec). La boîte de dialogue texte disparaît.

**Contenu.** `Tracked`, `Z` (toutes les primitives), `ZTable`, `ZAudit`, `Theme` complet (ombre, mouvement, `ReduceMotion`), `RiftWindow`, `UiWindows`, `StatusWindow`, `StatusSnapshot` (remplace `StatusReport`), `IUiHost` implémenté par `Host`, presse-papiers avec un nouvel essai.

**Bouchonné.** History et Tools toujours en boîte texte.

**Preuve.** Tests 11, 12, 13 (Status et carte), 15 (complet), 18. Dans Revit : capture Z_D01 de Status et de la carte dans ses quatre états (partie de la recette 25) ; ombre et placement de la carte (incertitude 7) ; interlettrage à l'écran (incertitude 2).

## Tranche 4 — History : ce que l'assistant a fait pendant la séance

**Ce qui devient vrai.** Critère 2 (sans les reçus) et critère 4 : la fenêtre History liste chaque demande de la séance — heure, outil, résultat, code, durée — avec le numéral `THIS SESSION` et la grille de pastilles (7 par ligne) ; un job long n'occupe qu'une ligne, passée de l'orange à son état final même si l'assistant ne vient pas chercher le résultat ; les refus d'authentification y figurent ; carte et History disent la même chose parce qu'elles lisent la même table de résultats.

**Contenu.** `RequestRecord`, `ActivityLog`, `Outcome`, `RequestRouter.Completed(RequestRecord)` (pas d'événement pour `op: job`, second enregistrement à `Job.Finished`), `Host.OnCompleted`/`OnUnauthorized` vers le journal, `CardContent.From(record)`, `HistoryView`, `HistoryWindow`.

**Preuve.** Tests 5, 6, 7, 13 (History), 14. Dans Revit : recette 24 (scénarios 3, 8, 9, 10, 11, 12 — chaque code identique à celui reçu par l'assistant), recette 26 (rafale de 20 appels pendant la saisie ; boîte modale ouverte), `run_query` long qui finit sans être interrogé (incertitude 6).

## Tranche 5 — Tools, recette complète et clôture du chantier (hors reçus)

**Ce qui devient vrai.** Le catalogue s'ouvre depuis le ruban : numéral = nombre d'outils, pastille pleine = livré, vide = prévu, filtre par niveau de risque, même contenu que le pamphlet ; les cinq critères du produit sont recettés et consignés avec leurs mesures et captures.

**Contenu.** `ToolCatalog` (ressource `tools.json` liée), `ToolsWindow`, README (captures), `SECURITY.md` (pause côté add-in), `CHANGELOG.md`, `addin/README.md`.

**Preuve.** Tests 13 (Tools), 17. Dans Revit : recette 25 complète (grille en cinq points pour carte, Status, History, Tools), recette 27 (nom changé en une constante). Les cinq critères cochés dans `00-status.md`.

## Tranche 6 — reçus de changement dans History (après M2)

**Ce qui devient vrai.** Critère 2 complet : chaque appel qui écrit dans le modèle montre dans History ce qu'il a changé — ajoutés, modifiés, supprimés par catégorie — et la raison enregistrée par `record_change`.

**Contenu.** `ChangeSummary.From` calé sur le format `changes` que M2 aura fixé, colonne et détail dans `HistoryView`/`HistoryWindow`.

**Bouchonné.** Rien ; n'ouvre que lorsque M2 a livré `changes` et `record_change`. Si M2 est livré avant l'ouverture du chantier, cette tranche se fond dans la tranche 4.

**Preuve.** Test 8 ; dans Revit : un `run_modify` sur la copie `SANDBOX` → la ligne History montre les mêmes comptes que la réponse reçue par l'assistant.

## Hors périmètre

- **Boîte de réception des commandes figées** : écran conçu en porte 1, construit avec M4 (son format de stockage n'existe pas encore) ; le bouton n'apparaît qu'alors.
- Thème sombre, traduction de l'interface, logo (Z_D01 n'en a pas).
- Pause mémorisée entre deux séances ; historique conservé entre deux séances (la trace durable reste `runs\` et le journal de l'add-in).
- Ouverture de l'Explorateur ou de tout autre programme depuis l'add-in.
- Revit 2027 (`net10.0-windows`) et années antérieures : à la première configuration ajoutée, une recette des tranches 1 et 2 suffit.
- Toute commande de pause depuis l'assistant (aucune op de protocole).
