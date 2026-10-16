# État : catalogue d'outils, README et pamphlet

Ouvert le 2026-10-12. **Livré le 2026-10-12** (avancé de T4/T6, sans dépendance à Revit) : registre, générateur, tableau README, `tools.json`, pamphlet, contrôles CI, ADR-027. Reste : brancher chaque nouvel outil au registre au fil des tranches, et `schemas` généré en T6.

Principe : **tout ce qui liste des outils est généré** depuis le registre `ToolSpec` du serveur, jamais écrit à la main. Le registre naît en M1 T4 (déjà prévu par `m1-read-only/03-conception.md`) ; la génération du README et du pamphlet est livrée en M1 T6.

## Couverture des serveurs existants
- 2026-10-13 : **approuvé** (sauf les 26 outils qui recoupent NORN FORGE/PRESS et `revit_drawing_layout`) → ADR-030 ; registre : `PromptSpec`, `ResourceSpec`, `EXCLUDED`, générés dans le README et `tools.json` ; restes `zenrvt` nettoyés sur le poste (enregistrement MCP, dossier de données ; liste de termes privés copiée dans le dossier `rift`). Proposition initiale : `01-couverture-2026-10-13.md` — sort des 167 outils rvt-mcp et de `pyrevit:place_family` non cités par le registre (94 écartés, dont 86 déjà par ADR-027 §4 ; 47 proposés en M2/M3 ; 26 à arbitrer avec NORN FORGE et PRESS), prompts et ressource rvt-mcp à modéliser, relation avec le hook le hook client de garde, restes `zenrvt`. Fait sans attendre : origine de `export_image` complétée (`revit_export_image`), `tools --check` vert.

## Une source, plusieurs sorties
- `server/src/rift_mcp/registry.py` : `ToolSpec` (`name, wave, toolset, risk, read_only, destructive, idempotent, description, input_model, remote_op, origin, status`), `FIXED_READ_OPS`, `validate()` (règle ADR-012 : `auto` seulement si `remote_op ∈ FIXED_READ_OPS`).
- `server/src/rift_mcp/catalog.py` : `to_document()` → JSON canonique ; rendus `permissions --format claude`, `schemas`, tableau README.
- CLI : `rift-mcp tools --format json|md`, chaque sortie avec `--check` en CI.
- Artefacts commités : bloc README entre `<!-- tools:begin -->` et `<!-- tools:end -->`, `docs/pamphlet/tools.json`.
- `tools/gen_pamphlet.py` (stdlib seule) → `docs/pamphlet/index.html`, page autonome au format « Z#D01 ». Les outils `designed` apparaissent atténués : le pamphlet dit ce qui tourne et ce qui est seulement conçu (ADR-026 §7).

## Décisions à consigner en ADR avant le code
- **Nommage** : espace de noms RIFT `<verbe>_<nom>` sans préfixe `revit_`. Le vocabulaire de rvt-mcp est conservé (verbes, millimètres, noms de catégories humains) ; le champ `origin` garde la trace (`rvt-mcp:revit_list_sheets`, `pyrevit:list_levels`, `rift`).
- **Périmètre** : l'inventaire du poste montre 189 outils rvt-mcp sur 233 jamais appelés et le code libre largement en tête. M3 reste centré gabarit ; l'ambition passe par le statut `designed` du catalogue, pas par du code.

## Correspondance des vagues (résumé)
| Vague | Outils RIFT | Sources |
|---|---|---|
| M1 | `get_context`, `run_query`, `audit_template`, `audit_template_write` | extension pyRevit (statut, modèle, vue, niveaux, code), rvt-mcp (cible, vue, listes de gabarit) |
| M2 | `run_modify`, `record_change`, `get_change_records`, `cancel_job` | `execute_revit_code` / `send_code_to_revit` en écriture, journal de changements rvt-mcp |
| M3 | gabarits de vue, filtres et surcharges, paramètres, nomenclatures, feuilles et cartouches, matériaux, familles, requête/modification d'éléments, lint, `survey`, `batch`, `result_ref` | toolsets rvt-mcp organization, graphics, parameters, schedule, sheets, materials, families, query, modify, delete, lint ; pyRevit `color_splash`, `list_families`… |
| M4 | C# Roslyn, commandes figées, cibles multiples, exports, cycle de vie document (`open` en `ask`, `save`/`close`/`sync` en `never_auto`), prompts MCP | toolsets meta, toolbaker, export ; pyRevit `open/save/close/sync/launch` |
| plus tard | création d'éléments, pièces, annotation, liens, géométrie, structure, CVC | 0 appel dans l'inventaire |
