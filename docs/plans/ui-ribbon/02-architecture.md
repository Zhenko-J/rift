# Architecture : chantier UI — bandeau, fenêtres et carte d'activité (M5)

Porte 2, rédigée le 2026-10-13 après l'approbation de la porte 1 (`01-produit.md`) et la lecture du code de M1 tranche 3 renommé RIFT (`672f181`). Conception seulement : rien de ce document n'entre dans le code avant la livraison de M2, sauf les éléments « tôt » (§ Calendrier) qui passent en M1 tranche 6.

## Code existant lu

| Fichier | Ce qu'il fait aujourd'hui | Ce que le chantier en fait |
|---|---|---|
| `addin/src/Rift.Addin/App.cs` | bootstrap : contexte isolé, `Start`/`Stop`/`Status`/`ProbePython` relayés par trois `MethodInfo`, construit le panneau `RIFT` de l'onglet `NORN` (deux boutons sans icône) | ne garde que le chargement du contexte, un seul relais `Host.Invoke(string command, object? arg)` et les classes de commande ; **la construction du ruban part dans `Rift.Revit`** (voir § Ruban) |
| `addin/src/Rift.Addin/StatusCommand.cs`, `ProbePythonCommand.cs` | `IExternalCommand` → `TaskDialog` / sonde | remplacées par une base `RelayCommand` et une sous-classe vide par bouton (`ServerToggleCommand`, `StatusCommand`, `HistoryCommand`, `CardCommand`, `InboxCommand`, `ToolsCommand`, `ProbePythonCommand`) |
| `addin/src/Rift.Core/Brand.cs` | nom affiché, identifiant, onglet, panneau (ADR-028) | inchangé ; seule source des chaînes de nom (critère 5) |
| `addin/src/Rift.Revit/Host.cs` | assemble config, secret, découverte, dispatcher, moteur, journal, routeur, écouteur, carte ; `Status()` texte ; `SetActivityCard(bool)` | ajoute l'état de pause, le journal d'activité de séance, le ruban et les fenêtres ; `Status()` texte disparaît au profit de la fenêtre |
| `addin/src/Rift.Revit/Ui/ActivityCard.cs` | carte WPF en code, sombre, couleurs en dur, liseré vert/rouge, `WS_EX_NOACTIVATE`, minuterie 6 s | même mécanique (ADR-023 intact), apparence Z_D01 par `Theme` et les primitives communes ; nouvel état « en pause » |
| `addin/src/Rift.Revit/Ui/StatusReport.cs` | formatage texte du Status | devient le modèle de la fenêtre Status (`StatusSnapshot`), plus de texte brut |
| `addin/src/Rift.Revit/Dispatch/RevitDispatcher.cs` | `ExternalEvent` unique, file de jobs + file d'actions UI (`EnqueueUi`) | inchangé ; la carte reste servie par `EnqueueUi` (jamais au-dessus d'une boîte modale) |
| `addin/src/Rift.Core/Protocol/RequestRouter.cs` | décode, admet par le bail, exécute, `Completed(label, status, code, message)` | une porte de pause avant le bail ; `Completed` porte un `RequestRecord` (heure, durée, `run_id`, reçu) au lieu de quatre chaînes |
| `addin/src/Rift.Core/Lease/LeaseManager.cs` | bail 60 s, `Revoke()` existe déjà, inutilisé | appelé par la pause |
| `addin/src/Rift.Core/Protocol/ErrorCodes.cs`, `docs/PROTOCOL.md` | `PAUSED` déclaré, jamais renvoyé ; « Pause revokes the lease » écrit | `PAUSED` devient réel ; le protocole n'ajoute **aucune** op (la pause ne se commande que depuis Revit) |
| `addin/src/Rift.Core/Config/RiftConfig.cs` | `activity_card`, marqueurs, patrons, `start_timeout_s` ; `Save` atomique existe | ajoute `developer` (bool) ; `Save` sert à mémoriser l'interrupteur de carte |
| `addin/src/Rift.Core/Journal/RunJournal.cs` | `runs\<utc>-<run_id>\` + `runs-index.jsonl` (runs de script seulement) | non lu par l'interface : History vit sur la séance en mémoire (voir point 3) |
| `addin/BannedSymbols.txt` | `System.Diagnostics.Process` interdit dans l'add-in (SECURITY.md) | conservé : pèse sur le bouton `open log folder` (point 5) |
| `docs/pamphlet/tools.json` | catalogue généré par `catalog.py` (ADR-027) : nom, vague, risque, statut, annotations, origine, description | embarqué tel quel dans `Rift.Revit` pour la fenêtre catalogue |
| `server/src/rift_mcp/` (client, outils) | traduit les codes add-in en erreurs MCP | ajoute le message de `PAUSED` (« paused by the user in Revit ») sans nouvel essai automatique |
| `addin/tests/Rift.Tests/ArchitectureTests.cs` | règles d'architecture (dépendances, pas de XAML…) | étendu par les règles du chantier (couleurs, nom, focus) |
| Z_D01 `design.md` (dépôt de design du mainteneur, hors dépôt RIFT) | valeurs reprises dans `01-produit.md` § Langage visuel | référence ; seules les valeurs de la porte 1 entrent dans `Theme.cs` |

Constats qui orientent l'architecture :

1. Le bootstrap ne référence que l'API Revit (ADR-021), mais les images du ruban sont des `ImageSource` WPF. Comme `Host.Start` reçoit déjà `UIControlledApplication` dans le contexte isolé, **c'est `Rift.Revit` qui peut construire le ruban** : un `PushButtonData` ne nomme la commande que par chaîne (chemin de `Rift.Addin.dll` et nom de classe), et WPF se résout dans le contexte par défaut des deux côtés, donc aucun problème d'identité de type.
2. `pack://` n'est pas sûr dans le contexte isolé (ADR-023) : aucune ressource WPF compilée ; les ressources (catalogue, icônes) sont des ressources de manifeste lues par `Assembly.GetManifestResourceStream`.
3. `PAUSED` et `Revoke()` existent déjà : la pause est une porte dans le routeur, pas un nouveau composant de transport.
4. `Process` est interdit dans l'add-in : ouvrir l'Explorateur demande soit une exception à cette règle, soit un autre geste.

## Ce que ça touche

**Add-in, `Rift.Core` (sans Revit, testable en xUnit)**

- `Control/PauseGate` : état `running | paused`, horodatage et origine de la pause ; `Pause()` révoque le bail et annule les jobs non démarrés ; lu par le routeur avant le bail.
- `Activity/RequestRecord` (heure UTC, libellé d'outil, statut, code, message, durée, `run_id`, `ChangeSummary?`) et `Activity/ActivityLog` : tampon circulaire de séance (500 entrées), compteurs, instantané immuable sous verrou. Alimenté par `RequestRouter.Completed` et par les refus du tube (`UNAUTHORIZED`).
- `Activity/ChangeSummary` (M2) : ajoutés / modifiés / supprimés par catégorie + raison `record_change`, extraits de `result.changes` de la réponse ; vide en M1.
- `Config/RiftConfig` : champ `developer`.

**Add-in, `Rift.Revit/Ui` (WPF en code, sans XAML)**

- `Theme.cs` : toutes les couleurs, rayons, tailles, police, ombre, durées de la porte 1 ; pinceaux gelés ; détection « réduire les animations » (`SystemParameters.ClientAreaAnimation`).
- `Kit/` — primitives Z_D01 construites en code : `CardPanel`, `Numeral`, `MicroLabel`, `Aside`, `Pastille` (pleine / orange / rouge / vide), `PastilleGrid` (7 par ligne), `InsetTile`, `GhostButton`, `Switch`, `DataTable` ; une seule règle d'accent par vue appliquée par la fenêtre, pas par la primitive.
- `Icons.cs` : tracés Lucide (données de chemin SVG, licence ISC) rendus au démarrage en `BitmapSource` 16 et 32 px à l'échelle du moniteur (point 4).
- `Ribbon.cs` : crée ou réutilise l'onglet `NORN` (convention ADR-028 §7), le panneau `RIFT` et ses boutons ; garde les références `PushButton` pour permuter libellé et icône du bouton on/off et griser `inbox`.
- `ActivityCard.cs` : restylée ; état « en pause ».
- `Windows/RiftWindow` (base : propriétaire = fenêtre principale de Revit, non topmost, une instance par fenêtre, rafraîchissement par minuterie de 1 s tant qu'elle est visible), `StatusWindow`, `HistoryWindow`, `ToolsWindow`, `InboxWindow` (M4).
- `ViewModels/` (testables sans WPF) : `StatusSnapshot`, `HistoryView` (lignes, grille de pastilles, compteurs), `ToolCatalog` (lecture de `tools.json`, filtres par risque), `InboxView` (M4).

**Add-in, `Rift.Addin`** : `App.cs` réduit (chargement, `Invoke`), `RelayCommand` + une classe par bouton. Le bootstrap ne référence toujours que l'API Revit.

**Serveur** : message et traitement de `PAUSED` dans le client (pas de nouvel essai, pas de nouveau bail tant que l'appel suivant n'est pas fait) ; aucune nouvelle op ni nouvel outil. `catalog.py` inchangé : `tools.json` est déjà la source unique du catalogue.

**Build** : `Rift.Revit.csproj` embarque `docs/pamphlet/tools.json` (lien `EmbeddedResource`) ; la CI vérifie déjà que `tools.json` est à jour (`rift-mcp tools --check`).

**Documentation** : `docs/DECISIONS.md` (ADR-029 « architecture de l'interface dans Revit », voir § Points à trancher), `docs/PROTOCOL.md` (sémantique de `PAUSED`), `THIRD_PARTY.md` (Lucide, ISC), `SECURITY.md` (pause = coupure côté add-in), README (captures), CHANGELOG.

**Rien dans le modèle Revit** : aucun paramètre, aucune page Notion, aucun gabarit n'est touché.

## Flux de bout en bout

**A. Démarrage de Revit**
1. `App.OnStartup` charge `Rift.Revit` dans le contexte `rift` et appelle `Host.Start(app, bootstrapPath)`.
2. `Host.Start` assemble comme aujourd'hui, puis crée `PauseGate`, `ActivityLog`, rend les icônes (`Icons`), construit le ruban (`Ribbon.Build`) : chaque `PushButtonData` nomme `Rift.Addin.dll` et la classe de commande ; « Probe Python » n'est ajouté que si `developer: true`.
3. La carte est créée comme aujourd'hui, stylée par `Theme`.

**B. Clic sur on / off (critère 1)**
1. Revit exécute `ServerToggleCommand.Execute` (thread de l'API) → `App.Invoke("server.toggle", uiapp)` → `Host.Invoke`.
2. `PauseGate.Pause()` : état `paused` ; `LeaseManager.Revoke()` ; les jobs encore en file et non démarrés sont annulés avec `PAUSED` (`JobTable.TryCancelBeforeStart`) ; un job déjà en cours **termine** (il est dans une transaction ou un groupe sur le thread de Revit, qu'on ne peut pas interrompre proprement avant `cancel` de M2).
3. `Ribbon.SetPaused(true)` permute libellé, icône et info-bulle ; la carte affiche la pastille vide `paused by you`.
4. Côté tube, toute requête authentifiée passe par `RequestRouter` : la porte de pause répond `refused / PAUSED` **avant** le bail et avant toute exécution ; l'écouteur reste ouvert (le client reçoit une réponse claire au lieu d'un tube absent). Les refus `UNAUTHORIZED` restent ceux de l'authentification.
5. Second clic : `Resume()`, bouton permuté, le premier appel suivant reprend le bail comme un client inactif.

**C. Une requête de l'assistant (carte et History, critères 2 et 4)**
1. Tube → authentification → pause → bail → handler / job (inchangé).
2. `Completed(RequestRecord)` sur le thread du tube → `ActivityLog.Add` (verrou court, aucun appel Revit) **et** `EnqueueUi(card.Show…)` : la carte reste servie par l'`ExternalEvent`, donc jamais au-dessus d'une boîte modale, jamais de focus (ADR-023).
3. Une fenêtre History ouverte lit `ActivityLog.Snapshot()` à chaque tic de sa minuterie (1 s, thread de l'interface) : pas de poussée inter-threads, pas d'appel à l'API Revit depuis une fenêtre.

**D. Fenêtres ouvertes par l'utilisateur (Status, History, Tools, Inbox)**
1. Bouton → commande → `Host.Invoke("status" | "history" | "tools" | "inbox")` sur le thread de l'API, donc le thread de l'interface de Revit.
2. `RiftWindow.ShowOrActivate()` : crée la fenêtre la première fois (propriétaire = `MainWindowHandle`, non modale, non topmost), sinon la ramène devant. Elle prend le focus puisqu'elle suit un clic (critère 4).
3. Status lit un `StatusSnapshot` (version, tube, appels servis, détenteur et depuis, génération, dernier refus, moteur, état de pause, carte) ; son interrupteur `activity card` écrit `config.json` (`RiftConfig.Save`) et bascule la carte ; son bouton `pause` passe par le même `PauseGate` que le ruban.
4. Tools lit `tools.json` embarqué une fois (numéral = nombre d'outils, pastille pleine = `implemented`, vide = `designed`, filtre par `risk`).
5. Inbox (M4) lit les propositions de commandes figées ; `accept script` / `reject script` écrivent la décision ; jamais d'acceptation implicite (ADR-020). Bouton grisé tant que la file est vide.

**E. Reçus de changement (M2 et après)**
`run_modify` et les outils typés d'écriture renvoient `changes` (ADR-026 §2) ; le routeur en extrait un `ChangeSummary` (comptes par catégorie, raison) dans le `RequestRecord`, que History affiche. Le chantier ne définit pas le format de `changes` : il le consomme tel que M2 le fixera.

## Données et paramètres Revit

- **Lus dans le modèle** : rien de plus qu'aujourd'hui. Les fenêtres n'appellent jamais l'API Revit ; elles lisent des instantanés en mémoire. Seuls les noms de catégorie des reçus (M2) apparaissent, jamais de valeur de paramètre ni de contenu de script (ADR-005, ADR-023).
- **Créés ou modifiés dans le modèle** : rien. Aucun paramètre partagé, aucun binding, aucune catégorie.
- **Hors modèle** : `%LOCALAPPDATA%\rift\config.json` gagne `developer` et voit `activity_card` écrit par l'interrupteur ; l'état de pause **n'est pas** mémorisé (point 2) ; History ne persiste rien (la trace durable reste `runs\` et le journal de l'add-in).
- **Ressources embarquées** : `tools.json` (régénéré, déjà vérifié en CI) ; tracés d'icônes Lucide dans le code (`Icons.cs`).

## Dépendances et runtime

- **Revit 2026** (`net8.0-windows`, configuration `R26`), seule cible construite aujourd'hui. Revit 2027 passera en `net10.0-windows` (`Directory.Build.props` le prévoit) : WPF en code et `PushButton` ne changent pas ; à revérifier à l'ajout de `R27` (rappel de la note de migration .NET 10 du second cerveau). Les années ≤ 2024 (`net48`) ne sont pas visées par ce chantier.
- **WPF** : `PresentationFramework`/`PresentationCore` du cadre partagé `Microsoft.WindowsDesktop.App`, résolus dans le contexte par défaut de Revit, absents de `Rift.Revit.deps.json` (comme la carte depuis M1). Aucun XAML, aucun `pack://`, aucune ressource BAML.
- **Ruban** : `UIControlledApplication.CreateRibbonTab/CreateRibbonPanel`, `PushButton.ItemText/LargeImage/Image/ToolTip/Enabled` modifiés sur le thread de l'API (dans une commande ou une action `EnqueueUi`). L'API n'a pas de bouton bascule : la permutation est la seule voie.
- **Icônes** : Lucide (ISC), données de chemin copiées dans `Icons.cs` avec l'avis de licence ; rendu par `Geometry.Parse` + `RenderTargetBitmap`. Aucune dépendance de développement (ni Python, ni Cairo).
- **Police** : Consolas, présente sur tout Windows ; repli `Courier New` déclaré dans `Theme`.
- **Aucun nouveau paquet NuGet**, aucune dépendance ajoutée côté serveur. `BannedSymbols.txt` inchangé (sous réserve du point 5).
- **IronPython** : non concerné.

## Calendrier

| Quand | Ce qui entre dans le code |
|---|---|
| M1 tranche 6 (« tôt », sans attendre les portes 3-4 de ce chantier) | `Theme.cs`, carte restylée, icônes pour les boutons existants, « Probe Python » derrière `developer`, interrupteur de carte dans le Status actuel ; le ruban passe dans `Rift.Revit` si la tranche 6 touche déjà `App.cs` |
| Après M2 (ce chantier, portes 3-4 approuvées) | `PauseGate` et bouton on/off, `ActivityLog`, fenêtres Status, History, Tools, kit Z_D01 ; reçus de M2 dans History |
| Après M4 | Inbox des commandes figées |

**Écart signalé** : la porte 1 fait de la pause un critère du chantier (M5), alors que `00-status.md` notait « op `pause` dès M1 T2 ». Aucune op `pause` n'a été écrite en M1 T2 et l'architecture ci-dessus n'en a pas besoin (la pause se commande depuis Revit, pas depuis le tube). Rien à rattraper en M1.

## Points à trancher à la porte 2 (proposés, à confirmer)

1. **Le ruban se construit dans `Rift.Revit`, le bootstrap ne garde que les commandes.** `App.cs` passe le chemin de `Rift.Addin.dll` à `Host.Start` ; un seul relais `Host.Invoke(string, object?)` remplace les `MethodInfo` un par un. Bénéfice : le bootstrap reste sans WPF et sans logique, les icônes et l'état du bouton vivent à côté de ce qui les change. → ADR-029 §1.
2. **La pause n'est pas mémorisée** : Revit redémarre toujours avec le serveur actif. La pause sert à « reprendre la main maintenant » ; la mémoriser créerait un serveur muet dont on a oublié la cause. Une pause coupe tout appel authentifié (y compris `get_context`), révoque le bail, annule les jobs non démarrés, laisse finir celui qui tourne. → ADR-029 §2.
3. **History = séance en mémoire** (500 dernières demandes), pas une lecture de `runs-index.jsonl` : l'index ne couvre que les runs de script, pas `get_context`, les refus ni les outils typés. Conséquence de produit : le micro-libellé `REQUESTS TODAY` devient **`THIS SESSION`** (la séance Revit, qui est ce que mesure le critère 2). Correction mineure de la porte 1 à valider avec celle-ci.
4. **Icônes rendues à l'exécution depuis les tracés Lucide**, plutôt que des PNG produits par un script Python et commités (`tools/gen_icons.py` prévu dans `00-status.md`) : nettes à toute échelle d'affichage, aucune chaîne d'outils de développement, aucun binaire dans le dépôt. Repli si le ruban de Revit les rend flous : PNG générés de la même façon au build.
5. **`open log folder` sans lancer de processus.** `System.Diagnostics.Process` est interdit dans l'add-in (SECURITY.md, analyseur). Deux options :
   - **(a, recommandée)** le bouton devient **`copy log path`** : chemin copié dans le presse-papiers, affiché en clair dans la tuile « journal » ; la promesse « l'add-in ne lance jamais de processus » reste vérifiable par l'analyseur sans exception ;
   - (b) `ShellExecuteW("open", dossier)` par P/Invoke, limité au dossier `%LOCALAPPDATA%\rift\logs` : garde le bouton de la porte 1, mais crée une exception documentée à une règle de sécurité (Explorer est lancé par le shell).
   Le choix (a) modifie un libellé de la porte 1 (`open log folder` → `copy log path`).
6. **Fenêtres non modales, une instance chacune, rafraîchies par minuterie** (1 s, uniquement visibles) plutôt que par notification inter-threads : aucune fenêtre n'appelle l'API Revit ni ne reçoit d'événement du thread du tube ; la carte reste la seule surface poussée, par l'`ExternalEvent` (ADR-023).
7. **Contrôle automatique d'une partie de Z_D01 et du critère 5 dans `ArchitectureTests`** : aucune couleur, police ni rayon littéral hors de `Theme.cs` ; aucune occurrence de `"RIFT"`/`"NORN"` hors de `Brand.cs` ; aucune fenêtre `Topmost`. L'auto-contrôle visuel en cinq points reste une recette manuelle avec captures.
8. **Inbox (M4) : stockage fichier dans `%LOCALAPPDATA%\rift\bake\`** (propositions, décisions, empreinte du script accepté) ; le format exact est laissé à M4, ce chantier ne fixe que « l'interface lit, la décision humaine écrit, rien n'est automatique ».
