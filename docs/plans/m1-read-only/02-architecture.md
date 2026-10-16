# Architecture : M1 — lecture seule de bout en bout

Brouillon du 2026-10-09, rédigé après lecture du code M0. Il ne devient la porte 2 qu'une fois la porte 1 approuvée.

## Code existant lu

| Fichier | Ce qu'il fait aujourd'hui (M0) | Ce que M1 en fait |
|---|---|---|
| `addin/src/Rift.Addin/App.cs`, `RiftLoadContext.cs`, `StatusCommand.cs`, `ProbePythonCommand.cs` | bootstrap : contexte de chargement isolé `rift`, charge `Rift.Revit` par réflexion (`Start`, `Stop`, `Status`, `ProbePython`), panneau « RIFT » de l'onglet « NORN » (Status, Probe Python) | inchangé sauf : `Status` renvoie aussi le client connecté ; un 3ᵉ membre réfléchi `SetActivityCard(bool)` pour l'interrupteur de la carte ; le bouton Probe Python reste (diagnostic) |
| `addin/src/Rift.Revit/Host.cs` | journal `addin-<pid>.log`, preuve du contexte isolé | devient le point d'assemblage : secret, fichier de découverte, écouteur, dispatcher, carte |
| `addin/src/Rift.Revit/ProbePython.cs` | prototype P-D4 (engine, trace, timeouts) | conservé tel quel ; son auto-test « trace events > 0 » est repris au démarrage (ADR-016) |
| `addin/src/Rift.Core/Transport/FrameHeader.cs` | sérialisation/parsing des en-têtes de trame (testé) | réutilisé par l'écouteur |
| `addin/src/Rift.Core/Security/HmacSigner.cs` | mac, proof, comparaison constante, secret, nonce (testé sur les vecteurs partagés) | réutilisé par l'écouteur |
| `addin/src/Rift.Core/Protocol/ErrorCodes.cs`, `ProtocolVersion.cs` | codes d'erreur add-in, `RIFT/1`, limites | complétés (`TIMEOUT_UNAVAILABLE`, `JOB_NOT_FOUND`, `WRITE_IN_QUERY`) |
| `addin/src/Rift.Scripting/IScriptEngine.cs`, `Python/IronPythonEngine.cs`, `Python/PyResultConverter.cs` | moteur IronPython 3.4.2 : `Compile`, `Run` avec deadline par trace, `NarrowBareExcept`, conversion DLR → JSON | réutilisés ; ajout du garde `__import__` (ADR-016, « import clr works ») et des variables `DB`, `UI`, `System`, `eid` du namespace ADR-015 |
| `addin/tests/Rift.Tests/*` | xUnit : vecteurs HMAC, en-têtes, moteur, architecture | étendus (écouteur contre faux dispatcher, bail, nonces, découverte, collecteur des presets) |
| `addin/tests/Rift.PipeHost/Program.cs` | bouchon | héberge le vrai `PipeListener` avec un dispatcher factice ; exercé par le client CPython en CI |
| `server/src/rift_mcp/transport/{signing,frames,pipe}.py` | client synchrone de tube nommé, signé, vérifiant le pid et la preuve (P-D3) | réutilisé tel quel, appelé depuis un thread de travail |
| `server/src/rift_mcp/cli.py` | squelette (`serve`, `install`, … « not implemented ») | `serve` et `install claude` implémentés ; `schemas --check`/`permissions --check` deviennent réels |
| `docs/PROTOCOL.md`, `shared/schemas/test-vectors/hmac.json` | protocole 1.0 (trames, ops, enveloppes, codes) | normatif ; `shared/schemas/transport-pipe.md` cité par ADR-001 n'existe pas encore → écrit à la porte 3 |
| `install.ps1`, `.github/workflows/ci.yml` | installation par utilisateur, CI build/test/stage | inchangés ; CI ajoute le test PipeHost ↔ client CPython |

Constat utile : la chaîne de parité (`p8_tableaux.py` → `p9_comparatif.py`, hors dépôt) lit **uniquement** `00_TOUT.json` (`document`, `generated`, `langue`, `data.<domaine>`) et compare des CSV par domaine ; les clés `_generated` ne sont pas comparées. La parité se joue donc sur les clés et valeurs de `data`, pas sur l'horodatage.

## Ce que ça touche

**Add-in (`addin/`)** — nouveaux composants, tous dans le contexte isolé :

- `Rift.Core` (sans Revit, testable) : `PipeListener` (ADR-001), `SecretStore` + `DiscoveryFile` (ADR-003), `NonceCache` (ADR-002), `Lease` (ADR-004), `JobTable` (ADR-008), `RequestRouter` (décodage de l'enveloppe, choix de l'op), `JsonCodec` (ADR-015), `Config` (`%LOCALAPPDATA%\rift\config.json` : `activity_card`, marqueurs sandbox, patrons d'agents), `RunJournal` (ADR-005, expurgé).
- `Rift.Revit` (Revit) : `Dispatcher` (`IExternalEventHandler` + `ExternalEvent` créés dans `Host.Start`), `ReadGuard` (sous-ensemble lecture d'ADR-006/007 : groupes de transactions toujours annulés, détection d'écriture), `ContextTool` (`op: context`), `QueryRunner` (`op: run`, `mode: query` seulement), `AuditPresets` (six scripts Python embarqués + collecteur en mémoire), `ActivityCard` (fenêtre WPF non modale), `StatusText`. `UseWPF` passe à `true` dans `Rift.Revit.csproj`.
- `Rift.Scripting` : garde `__import__`, namespace complet, `IResultCollector` (remplace l'écriture de fichiers des presets).
- `Rift.Tests`, `Rift.PipeHost`.

**Serveur (`server/`)** : `discovery.py` (instances, choix de la cible, vérification `psutil`), `client.py` (bail, renouvellement, codes serveur), `tools/` (`get_context`, `run_query`, `audit_template`), `registry.py` (`ToolSpec` + `risk`, ADR-012), `server.py` (MCP stdio sur le SDK `mcp`), `install.py` (`install claude`), `lint.py` (M2 : seul `compile` côté add-in sert de vérité en M1).

**Hors code** : `docs/DECISIONS.md` (ADR-023 carte d'activité, ADR-024 presets et parité, amendement ADR-008 pour la création des événements dans `Host.Start`), `docs/PROTOCOL.md` (codes ajoutés, `op: tool` restreint à `audit.*` en M1), README (table des outils générée), CHANGELOG.

**Aucune page Notion, aucun fichier de paramètres partagés, aucun gabarit** n'est touché : M1 ne crée rien dans Revit.

## Flux de bout en bout

1. **Installation.** `pwsh install.ps1 -Years 2026` (Revit fermé) ; `uv tool install rift-mcp` puis `rift-mcp install claude`, qui enregistre le serveur dans Claude Code (`claude mcp add --scope user rift -- rift-mcp serve`) et affiche ce qu'il a fait. Deux commandes, aucun fichier édité à la main (critère 4).
2. **Démarrage de Revit.** `Host.Start` : charge `config.json` ; crée le moteur IronPython et lance l'auto-test de trace (0 événement → `TIMEOUT_UNAVAILABLE` pour tout `run`) ; génère le secret et écrit `revit-<pid>.key` puis `revit-<pid>.json` (purge des pid morts) ; crée `ExternalEvent` + handler (contexte API valide) ; démarre le thread d'écoute du tube `\\.\pipe\rift-revit-<pid>` ; construit la carte (cachée) sur le thread UI de Revit, propriétaire = fenêtre principale.
3. **Premier contact de l'assistant.** Le serveur MCP (lancé par Claude Code) lit `%LOCALAPPDATA%\rift\instances\*.json`, vérifie pid + date de création (`psutil`), envoie `health` (non signé), puis `lease` signé. L'add-in répond `lease_until_utc` ou `LEASE_HELD {holder}`. À l'obtention du bail, la carte affiche « Assistant connecté : <client> » ; `Status` affiche le même détenteur et l'heure.
4. **`get_context`.** `op: context` → job mis en file → `ExternalEvent.Raise()` → `Execute` sur le thread API : versions, document actif (titre, chemin, classification sandbox/référence/unsaved/unmarked), documents ouverts, vue active, sélection, niveaux, politique (`read_only: true` en M1), coexistence (ADR-019) → réponse signée. Le thread du tube attend le job jusqu'à `timeout_s` ; si `job.started` n'est pas levé en 30 s → `REVIT_BUSY` avec la raison (fenêtre modale).
5. **`run_query`.** Le serveur envoie `op: compile` (hors thread API ; erreur de syntaxe → ligne/message, rien n'est exécuté), puis `op: run {mode: query, timeout_s}`. Dans `Execute` : `ReadGuard` ouvre un `TransactionGroup` sur chaque document ouvert modifiable, s'abonne à `DocumentChanged`, `DocumentSaving/SavingAs/Closing/Synchronizing`, `FileExporting`, `ViewExporting` (tous annulés) ; le moteur exécute avec la deadline coopérative ; en `finally` : désabonnement, puis **RollBack inconditionnel** de tous les groupes. Résultat `{outcome: ok | rolled_back(reason: write_in_query) | error | timeout, result, stdout, stderr, changes, blocked, timing}`. La carte montre `run_query · réussi` ou `· refusé WRITE_IN_QUERY` etc., même code que la réponse. La pile Annuler reste vide (critère 3).
6. **Audit (`audit_template`).** `op: tool {name: audit.p1 … audit.p6 | audit.all}` : même chemin que `run`, mais le script est une ressource embarquée et `dump(name, obj)` alimente un collecteur en mémoire au lieu d'écrire sous `~/Documents`. `audit.all` enchaîne p1→p5 puis le fusionneur (p6 : clés `data`, placement des légendes), dans **un seul** job pour tenir le critère 5, avec `report_progress` par script. Le serveur renvoie les six documents JSON ; sur demande (`write_to`, outil `ask`), il les écrit dans `<dossier>/<titre du document>/0N_*.json` + `00_TOUT.json` pour que `p8`/`p9` tournent sans changement.
7. **Deuxième client.** `lease` → `LEASE_HELD {holder: {name, pid, since}}` ; la carte l'affiche (critère 2). Requête non signée ou mal signée → `UNAUTHORIZED`, connexion fermée, rien n'est mis en file.
8. **Arrêt.** `Host.Stop` : ferme l'écouteur, supprime `.json`/`.key`, ferme la carte.

Threads : *tube* (écoute, décodage, signature, attente du job), *API Revit = UI* (`Execute`, scripts, carte), *serveur CPython* (boucle MCP asynchrone ; le client synchrone du tube tourne via `anyio.to_thread` pour ne pas bloquer stdio). Une seule requête en vol (`maxNumberOfServerInstances = 1`) ; un seul job exécuté à la fois.

## Données et paramètres Revit

**Lus (jamais créés ni modifiés)** — exactement le périmètre de p1-p6 :

| Préset | Objets Revit lus | Clés `data` produites |
|---|---|---|
| p1 | `View` (templates, légendes), types `OST_TitleBlocks` (`SHEET_WIDTH/HEIGHT`), sous-catégories de `OST_Lines` (poids, couleur, motif de projection), `LinePatternElement` | `gabarits_de_vue`, `legendes`, `cartouches`, `types_lignes`, `motifs_lignes` |
| p2 | `TextNoteType`, `DimensionType` (+ `params` localisés et `params_bip`) ; `doc.Application.Language` | `types_note_textuelle`, `types_cotation` |
| p3 | `FilledRegionType`, `FillPatternElement` | `types_pochage`, `motifs_remplissage` |
| p4 | `ViewSchedule` (définition, champs, filtres, tri), `ScheduleSheetInstance` | `nomenclatures` |
| p5 | `ParameterBindings`, `SharedParameterElement`, `LabelUtils` | `parametres_de_projet`, `inventaire_parametres_partages`, `_resume` |
| p6 | `Viewport` (légendes sur feuilles) | `00_TOUT.json` : `document`, `generated`, `langue`, `resume`, `data` |

`get_context` lit en plus : `Application.VersionNumber/VersionBuild`, documents ouverts, vue active, sélection, `Level`. Aucun paramètre partagé, aucune catégorie, aucun GUID n'est créé.

**Écrits hors Revit** (tous sous `%LOCALAPPDATA%\rift\`) : `instances\revit-<pid>.json` et `.key` (ADR-003), `config.json`, `logs\addin-<pid>.log`, `runs\<utc>-<run_id>\` (ADR-005, expurgé : script, requête, réponse). Côté serveur : fichiers JSON d'audit uniquement sur `write_to` explicite.

**Portage IronPython 2.7 → 3.4 des presets** (parité) : les scripts sont réécrits sans changer les clés ni les tris. Pièges identifiés : `sorted(..., key=lambda x: x["name"])` lève `TypeError` en Python 3 si un nom est `None` (Python 2 ordonnait `None` avant les chaînes) → clé `str(x["name"])` partout, comme p2/p3 le font déjà ; `except:` nus → réécrits en `except Exception` par le moteur, ce qui reste correct car les exceptions .NET de l'API arrivent comme `Exception` ; `"%d,%d,%d" %` et `dict(...)` identiques ; `os.path.expanduser` inutile (collecteur). Les valeurs localisées (`category`, `data_type`, `group`, `params`) dépendent de la langue de Revit, pas de RIFT : la parité se mesure **sur le même poste, même Revit, même document**.

## Dépendances et runtime

| Composant | Version | Remarque |
|---|---|---|
| Revit | 2026.3 (build 26.3.0.37), .NET 8 | référence unique de M1 ; la migration Autodesk de Revit 2026 vers .NET 10 (annoncée) impose de recompiler et re-tester le chargement par le manifeste `.addin` dès qu'elle est installée sur le poste ; `net10.0-windows` est déjà prévu par `Directory.Build.props` pour 2027 |
| Revit API | `Nice3point.Revit.Api.RevitAPI/UI` 2026.3.0, référence seule | inchangé |
| WPF | `PresentationFramework` du framework partagé `Microsoft.WindowsDesktop.App` 8 | nouveau : `UseWPF=true` sur `Rift.Revit` ; résolu dans le contexte par défaut (hors `deps.json`) ; la carte est construite **en code, sans XAML/BAML** pour éviter la résolution des ressources `pack://` par nom d'assembly depuis un contexte isolé |
| IronPython | 3.4.2 + StdLib (sous-ensemble) | inchangé ; options `NoAdaptiveCompilation`, `FullFrames`, `Tracing` obligatoires |
| .NET SDK | 8.0.400 (`global.json`), C# 12 | inchangé ; analyseur `BannedApiAnalyzers` interdit `Process`, réseau, `Console` dans l'add-in |
| CPython | ≥ 3.13, `uv` | inchangé |
| `mcp` | ≥ 1.29, < 2 (serveur stdio, `FastMCP`) | première utilisation réelle en M1 |
| `pydantic` ≥ 2.9, `psutil` ≥ 6, `anyio` ≥ 4 | — | `parso` reste déclaré mais n'est exercé qu'en M2 (lint) |
| Claude Code CLI | `claude mcp add` | `install claude` l'invoque ; absent → message et fichier à copier, sans édition automatique |
| Chaîne de parité | `p8_tableaux.py`, `p9_comparatif.py`, `openpyxl` (hors dépôt) | oracle de recette, jamais copiée dans le dépôt |

## Points à trancher à la porte 2 (proposés, à confirmer)

1. **Carte d'activité en code WPF pur, créée sur le thread UI via le dispatcher** (pas de XAML ; propriétaire = `MainWindowHandle` ; `ShowActivated=false`, `WS_EX_NOACTIVATE`, disparition par minuterie 6 s, mise à jour en place). Une boîte modale bloque `Execute`, donc la carte « attend » sans code supplémentaire. → ADR-023.
2. **Presets = scripts Python embarqués exécutés par le moteur avec un collecteur** (pas une réécriture en C#) : c'est le seul moyen de garantir la parité clé pour clé sans réinterpréter chaque `try/except` des scripts d'origine ; `audit.all` est un seul job. → ADR-024.
3. **Client de tube synchrone dans un thread** côté serveur plutôt que `create_pipe_connection` : le code P-D3 existe et est testé ; la version asynchrone est un gain M2+ sans effet sur les critères.
4. **`install claude` passe par la CLI `claude`** et n'écrit jamais `~/.claude.json` lui-même.
5. **`job`/`accepted` implémentés dès M1** (table de jobs minimale) parce que l'exécution est déjà asynchrone par nature ; `cancel` reste M2.
6. **Codes ajoutés au protocole** : `TIMEOUT_UNAVAILABLE` (auto-test de trace à 0), `WRITE_IN_QUERY` (raison de rollback), `JOB_NOT_FOUND`.
