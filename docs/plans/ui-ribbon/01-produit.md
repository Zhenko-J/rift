# Produit : chantier UI — bandeau, fenêtres et carte d'activité (M5)

Porte 1 du chantier ouvert par ADR-026 §5. Approuvée le 2026-10-13 ; deux libellés retouchés à l'approbation de la porte 2 (`THIS SESSION`, `copy log path`). Conception seulement : aucun code de ce chantier n'est écrit avant la livraison de M2, sauf les éléments « tôt » listés à la fin.

**Noms (décision du mainteneur, 2026-10-13).** Le serveur MCP s'appelle **RIFT** (code, paquets, tube et dossier de données renommés le jour même, ADR-028 ; seul le dossier du dépôt reste à renommer). Ses boutons vivent dans l'onglet de ruban **NORN**, partagé par toute la suite de plugins Revit du mainteneur : RIFT y pose son panneau, à côté de ceux des autres plugins, et crée l'onglet s'il n'existe pas encore. **Toute la suite, NORN comme RIFT, suit Z_D01** ; aucune autre palette.

## Problème

Le BIM manager a les yeux sur Revit, pas sur la fenêtre de l'assistant. Aujourd'hui, dans Revit, l'add-in se résume à deux boutons sans icône (« Status », qui ouvre une boîte de dialogue Windows en texte brut, et « Probe Python », un outil de développement resté visible) et à une carte d'activité sombre aux couleurs codées en dur. Il ne peut pas :

- **couper l'assistant d'un clic** quand il reprend la main sur son modèle (aujourd'hui : fermer l'assistant ou Revit) ;
- **voir ce que l'assistant a fait** sans quitter Revit : quelles demandes, quand, avec quel résultat, et ce qu'elles ont changé (à partir de M2) ;
- **savoir ce que l'assistant sait faire** et avec quel niveau de risque ;
- **accepter ou refuser** un script répété que l'assistant propose de figer en commande (M4).

rvt-mcp offre déjà une partie de cette expérience (bouton ON/OFF, historique, carte). Le BIM manager veut la même chose, mais sous **sa propre identité visuelle**, celle qu'il utilise pour tous ses livrables : le système de design **Z_D01**. Une interface d'outil qui ressemble à ses rapports et à ses pages d'inventaire est une interface qu'il reconnaît et à laquelle il fait confiance ; une interface « par défaut » ressemble à un add-in de plus.

## Qui est l'utilisateur final

- **Le BIM manager dans Revit** (le mainteneur, puis toute personne qui installe l'add-in). Il travaille dans Revit pendant que l'assistant travaille à côté. Il ne veut ni être interrompu, ni lire de code, ni perdre le focus clavier.
- **Le relecteur** (le même BIM manager, plus tard) : il veut retrouver ce qui s'est passé pendant une séance, par exemple avant une synchronisation.

## Critère de réussite

1. **Couper l'assistant en un clic.** Un bouton du ruban met le serveur en pause ; dans la seconde qui suit, tout appel reçoit `PAUSED` et rien ne s'exécute. Le bouton montre son état (actif / en pause) sans ouvrir de fenêtre. Mesure : appel lancé juste après le clic → `PAUSED`, rejoué 10 fois.
2. **Voir l'activité sans quitter Revit.** Une fenêtre « History » liste les demandes de la séance (outil, résultat, code d'erreur, durée) et, à partir de M2, les reçus de changement (éléments ajoutés, modifiés, supprimés par catégorie). Mesure : chaque appel des scénarios de recette y figure avec le même code que celui reçu par l'assistant.
3. **Tout est Z_D01.** Chaque écran du chantier (carte d'activité, Status, History, catalogue, boîte de réception) passe l'auto-contrôle en cinq points de Z_D01 : exactement un élément orange `#D93611` par vue (ou aucun si rien n'est « en cours ») ; tous les coins arrondis, aucune ombre empilée ; libellés en capitales espacées et phrases en minuscules ; aucun blanc pur, aucun dégradé, aucun emoji ; un numéral géant sur chaque fenêtre (exception : la carte d'activité, voir Écrans). Mesure : grille de contrôle remplie écran par écran, avec capture, par le mainteneur.
4. **Jamais dans les jambes.** Aucune fenêtre du chantier ne prend le focus clavier sans un clic de l'utilisateur ; aucune ne s'ouvre par-dessus une boîte de dialogue modale de Revit ; seules les fenêtres que l'utilisateur ouvre lui-même peuvent être modales. Mesure : saisie au clavier dans Revit pendant une rafale de 20 appels, aucun caractère perdu.
5. **Le nom du produit est à un seul endroit.** Le nom affiché (onglet, titres, carte) vient d'une constante unique ; renommer le produit ne touche aucune chaîne d'interface. Mesure : changer la constante, reconstruire, aucune autre occurrence visible.

## L'annonce avant la fonctionnalité

> Votre assistant a maintenant son tableau de bord dans Revit. Un onglet, quatre panneaux : un bouton pour mettre l'assistant en pause d'un clic quand vous reprenez la main ; une fenêtre d'état qui dit qui est connecté et depuis quand ; un historique de la séance — chaque demande, son résultat et, dès qu'il peut écrire, ce qu'il a changé ; et le catalogue de ce qu'il sait faire, avec le niveau de risque de chaque outil. La petite carte qui apparaît dans un coin à chaque demande ne vous vole jamais le clavier. Tout est dessiné dans le même langage que vos rapports : papier chaud, une seule police, un seul point orange pour ce qui se passe maintenant.

## Ce que ce chantier ne fait pas (volontairement)

- Il ne change rien à ce que l'assistant a le droit de faire : les niveaux de risque, la politique de document et les reçus sont décidés côté serveur (ADR-012, ADR-026 §2). L'interface les montre, elle ne les règle pas.
- Pas de thème sombre : Z_D01 n'en a pas (lacune connue du système). Pas de traduction : l'interface est en anglais, langue du dépôt.
- Pas de personnalisation du ruban natif de Revit au-delà des icônes et des libellés : l'API ne permet ni couleur ni police sur les boutons.
- Pas de logo : Z_D01 n'en a pas ; le nom s'écrit en toutes lettres.
- Pas de renommage du code dans ce chantier : il est traité à part (critère 5 le rend sans effet sur l'interface).

## Écrans

### Langage visuel commun (Z_D01 appliqué à WPF)

Valeurs reprises telles quelles du système Z_D01, regroupées dans un seul fichier de thème de l'add-in (`Ui/Theme.cs`) :

| Rôle | Valeur |
|---|---|
| Fond de fenêtre (le « sol ») | `#CFCDC7` |
| Carte, panneau principal | `#EAE9E6`, coins 28 px (fenêtre) ou 20 px (carte flottante, panneau) |
| Zone en retrait (tableaux, tuiles) | `#E0DFDB`, coins 20 px, sans ombre |
| Encre : texte, sous-texte, discret | `#191918`, `#3C3C39`, `#64635E` |
| Filets | 1 px `#C6C5BF` ; 2 px `#1F1F1E` réservé aux pastilles et boutons fantômes |
| Accent unique « maintenant » | `#D93611` ; texte blanc dessus seulement |
| États réels | réussi `#3E7A5E`, attention `#B98A22`, échec `#B2452E` — jamais décoratifs |
| Police | Consolas partout ; numéral 900, interlettrage −5 % ; libellés 11 px capitales espacées 0,2 em |
| Ombre | une seule, chaude et large : `rgba(25,25,24,.16)`, flou 34 px, décalage 14 px |
| Mouvement | apparition 220 ms avec montée de 4 px ; aucun glissement latéral ; rien si « réduire les animations » |

Trois voix de texte, pas plus : le **numéral** (chiffres nus), le **micro-libellé** (`CALLS TODAY`, deux mots maximum) et l'**aparté en minuscules** sans point final (`paused by you · 2 min ago`). Les boutons sont des verbes en minuscules qui nomment ce qu'ils font (`pause`, `copy log path`, `reject script`). Les échecs donnent la cause (`refused — another assistant holds the lease`).

### Ruban (onglet NORN, panneau RIFT)

Dans l'onglet partagé, RIFT occupe un seul panneau `RIFT` (les sous-groupes ci-dessous deviennent des séparateurs ou un bouton déroulant si la place manque) :

| Groupe | Bouton | Comportement |
|---|---|---|
| Server | **on / off** | un clic met en pause ou relance ; libellé et icône permutés (`server on` / `paused`) ; l'icône « on » est le seul élément orange du ruban |
| Server | **status** | ouvre la fenêtre Status |
| Activity | **history** | ouvre la fenêtre History |
| Activity | **card** | affiche ou masque la carte d'activité ; l'état est retenu d'une séance à l'autre |
| Bake | **inbox** | ouvre la boîte de réception (M4) ; grisé tant qu'elle est vide |
| Help | **tools** | ouvre le catalogue des outils |

Icônes : jeu Lucide, trait 1,75 px, encre `#191918`, jamais remplies, rendues en images 16 et 32 px. « Probe Python » quitte le ruban (outil de développement, réservé à une configuration `developer`).

### Carte d'activité (l'« info-bulle »)

Petite fenêtre flottante dans le coin inférieur droit de Revit, qui apparaît à l'événement et disparaît seule au bout de 6 s. Elle suit les règles d'ADR-023 (jamais de focus, jamais par-dessus une boîte modale, jamais de contenu de script ni de donnée du modèle) et prend l'allure Z_D01 :

- **Forme** : carte `#EAE9E6`, coins 20 px, une ombre chaude, marge intérieure 20 px, largeur 260 à 420 px. Plus de liseré de couleur en bas.
- **Ligne 1** : une pastille de 22 px, puis le micro-libellé `RIFT · GET_CONTEXT` (nom du produit et nom de l'outil en capitales espacées, `#64635E`).
- **Ligne 2** : l'aparté en minuscules, `#191918`, 15 px : `get_context succeeded`, `run_query still running`, `assistant connected — claude code 2.x`.
- **Ligne 3 (en cas d'échec seulement)** : le code tel quel (`LEASE_HELD`) en micro-libellé, puis la phrase reçue par l'assistant, en `#3C3C39`.
- **Pastille** : pleine encre = réussi ou connecté ; **orange = en cours** (seul accent de la vue) ; pleine `#B2452E` = échoué ou refusé ; vide (contour 2 px) = en pause.
- **Exception assumée** : pas de numéral géant. La carte est une surface transitoire de quelques secondes ; un chiffre de 96 px y masquerait Revit. Validé par le mainteneur le 2026-10-13.

### Status

Fenêtre ouverte par l'utilisateur (elle peut donc prendre le focus), remplaçant la boîte de dialogue Windows actuelle, qui ne peut pas être stylée.

- Numéral : nombre d'appels servis pendant la séance ; micro-libellé `CALLS SERVED` ; aparté : `claude code connected since 09:24`.
- Tuiles en retrait : client et depuis quand ; état du serveur ; tube ; journal.
- Commandes : interrupteur `activity card` ; boutons `pause`, `copy log path` (le chemin du dossier de journaux est copié et affiché : l'add-in ne lance jamais de processus, porte 2 point 5).

### History

- Numéral : demandes de la séance Revit ; micro-libellé `THIS SESSION` (porte 2 point 3 : la séance, pas la journée).
- Grille de pastilles, 7 par ligne, dernière ligne laissée incomplète : une pastille par demande (pleine = réussie, rouge = échec ou refus, orange = en cours).
- Tableau : heure, outil, résultat, code, durée ; à partir de M2, le reçu de changement (ajoutés, modifiés, supprimés par catégorie) et la raison enregistrée par `record_change`.
- Rien du contenu des scripts ni des valeurs du modèle (ADR-005, ADR-023).

### Catalogue

Même contenu que le pamphlet `docs/pamphlet/index.html`, lu depuis le même `tools.json` embarqué : numéral = nombre d'outils, pastille pleine = livré, vide = prévu, filtre par niveau de risque. Aucun navigateur n'est lancé depuis Revit.

### Boîte de réception des commandes figées (M4)

Numéral = propositions en attente. Pour chacune : nom proposé, nombre d'exécutions identiques observées, emplacement de ruban visé ; boutons `accept script` et `reject script`. Rien n'est jamais accepté automatiquement (ADR-020).

## Ce qui peut passer tôt (M1 tranche 6, sans porte 2 et 3)

- `Ui/Theme.cs` et la carte d'activité restylée Z_D01 (même comportement, nouvelle apparence).
- Constante unique du nom de produit (critère 5).
- Icônes Lucide pour les boutons existants, « Probe Python » masqué.
- Interrupteur de la carte dans Status (déjà prévu en tranche 6).

## Questions ouvertes (à trancher avant la porte 2) — toutes tranchées le 2026-10-13

1. ~~Nouveau nom du produit~~ — tranché le 2026-10-13 : RIFT, dans l'onglet NORN ; Z_D01 pour toute la suite. Le renommage du dépôt, des paquets et du tube est traité à part (après la tranche 3 de M1) : fait le 2026-10-13 (ADR-028), le nom affiché vient de `Brand.cs` (critère 5).
2. ~~Carte sans numéral~~ — validé le 2026-10-13 : pas de numéral sur la carte.
3. ~~Rouge pour les échecs~~ — validé le 2026-10-13 : pastille pleine `#B2452E` pour un échec ou un refus, dans la carte et dans History.
