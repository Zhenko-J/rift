# Produit : M1 — lecture seule de bout en bout

## Problème

Aujourd'hui, quand un BIM manager demande à son assistant « montre-moi tous les gabarits de vue de ce projet » ou « sors-moi l'inventaire des paramètres partagés », l'assistant passe par un relais qui a été construit pour dépanner : il accepte n'importe quelle demande venant de n'importe quel programme du poste, il n'a aucun moyen de prouver que c'est bien l'assistant qui parle, et il ne distingue pas une question (« liste ») d'une action (« renomme »). Pour un simple inventaire, le BIM manager prend donc le même risque que pour une modification.

Par ailleurs, les inventaires de gabarit — la base des audits de consolidation — sont produits par six scripts à lancer un par un, à la main, dans un ordre précis, en copiant-collant leur contenu dans l'assistant à chaque projet. Si un script est lancé deux fois ou oublié, les tableaux comparatifs se décalent et personne ne s'en aperçoit avant la réunion.

Ce que le BIM manager veut : poser une question sur le projet ouvert dans Revit et obtenir la réponse, sans risque pour la maquette, sans se demander qui d'autre pourrait parler à son Revit, et obtenir ses inventaires d'audit en une demande au lieu de six manipulations — avec exactement les mêmes tableaux qu'avant, pour que les comparaisons entre projets restent valables.

## Qui est l'utilisateur final

- **Le BIM manager** (le mainteneur du projet, puis toute personne qui installe RIFT) : il a Revit ouvert sur une maquette ou un gabarit et un assistant à côté ; il veut lire, inventorier, comparer. Il ne veut pas lire de code.
- **L'assistant** (Claude Code, Claude Desktop, Cursor…) : il reçoit la question en langage courant et doit pouvoir y répondre en interrogeant Revit, sans qu'on lui demande de deviner comment Revit est branché.

## Critère de réussite

1. **Mêmes résultats qu'avant, à la virgule près.** Sur un même projet, les six inventaires produits par RIFT et par les six scripts actuels, passés dans l'outil de comparaison existant, donnent **zéro différence**. Mesure : le comparatif sort vide. Précision (approbation du 2026-10-09) : la comparaison porte sur les tableaux par domaine, c'est-à-dire sur les clés et les valeurs des inventaires ; l'heure de génération, qui change à chaque passage, n'en fait pas partie.
2. **Un seul programme peut parler à Revit, et c'est le bon.** Un programme qui tente de joindre Revit sans être l'assistant installé est refusé avant d'avoir rien exécuté ; un deuxième assistant qui arrive pendant qu'un premier travaille se voit répondre « occupé par X » plutôt que servi en silence. Mesure : les deux scénarios sont rejoués et le refus est explicite.
3. **Une question ne peut pas abîmer la maquette.** Toute demande passée par M1 laisse la maquette exactement comme avant : rien à annuler dans Revit, fichier intact, et une tentative d'écriture déguisée en question est annulée et signalée. Mesure : la pile « Annuler » de Revit est vide après chaque demande ; la taille et la date du fichier n'ont pas bougé.
4. **Installé en une commande.** Un poste qui a Revit 2026 et Claude Code passe de « rien » à « l'assistant répond sur la maquette ouverte » avec une commande d'installation côté Revit et une côté assistant, sans éditer de fichier à la main.
5. **Un audit complet n'est pas plus long qu'avant.** Les six inventaires sur un gabarit courant prennent au plus le temps de la chaîne actuelle (mesuré sur le même poste, même projet).

## L'annonce avant la fonctionnalité

> Avec RIFT M1, votre assistant peut enfin regarder votre maquette Revit sans pouvoir la toucher. Vous lui demandez ce qui est ouvert, quels gabarits de vue, nomenclatures, styles de ligne, pochages ou paramètres existent, et il vous répond avec les mêmes tableaux que vos audits actuels — identiques, donc comparables avec tout ce que vous avez déjà produit. Chaque demande est signée : seul l'assistant que vous avez installé peut parler à votre Revit, et un seul à la fois. Chaque réponse se fait dans un mode où rien ne peut être enregistré, ni modifié, ni exporté : si une demande essaie quand même, elle est annulée et vous le voyez. L'installation tient en une commande de chaque côté. Les modifications gardées (avec aperçu et Garder/Annuler dans Revit) arrivent au jalon suivant.

## Ce que M1 ne fait pas (volontairement)

- Aucune modification de la maquette, même en aperçu : c'est M2.
- Pas d'installation pour d'autres assistants que Claude Code ; pas d'autre version de Revit que 2026.
- Pas de « bibliothèque » d'actions prêtes à l'emploi ni de rapport d'audit en un clic (`00_AUDIT`) : seulement les six inventaires bruts, identiques aux scripts actuels ; la fusion et les tableaux comparatifs continuent de se faire avec les outils existants hors Revit.
- Pas d'envoi vers Notion ni vers un tableur : M1 produit les données, pas les classeurs.

## Écrans

Un seul élément nouveau dans Revit : une **carte d'activité** (ajoutée le 2026-10-09). Le reste est déjà en place depuis M0 : le panneau « RIFT » (onglet « NORN » depuis ADR-028) avec le bouton **Status** (qui dira désormais aussi *qui* est connecté et depuis quand) et **Probe Python**. Côté assistant : une ligne de commande d'installation et les réponses en texte/tableaux.

**Carte d'activité.** Quand l'assistant parle à Revit, le BIM manager a les yeux sur Revit, pas sur la fenêtre de l'assistant. Aujourd'hui, si une demande échoue (aucun document ouvert, Revit occupé par une boîte de dialogue, demande refusée), il ne le voit que dans l'assistant, parfois trop tard. La carte d'activité est une petite fenêtre non modale, dans un coin de Revit, qui apparaît à l'événement et disparaît seule :

- **Ce qu'elle montre** : « Assistant connecté » à l'arrivée d'un client ; puis, à chaque demande terminée, le nom de l'outil, le résultat en un mot (réussi / échoué / refusé) et, en cas d'échec, la raison telle qu'elle est renvoyée à l'assistant (même code, même phrase). Une même carte est mise à jour par les demandes successives au lieu d'en empiler plusieurs.
- **Ce qu'elle ne fait jamais** : prendre le focus clavier, bloquer Revit, s'afficher par-dessus une boîte de dialogue modale (elle attend), montrer le contenu d'un script ou d'une donnée du modèle (le journal est déjà expurgé à la source, ADR-005 ; la carte ne montre que le nom de l'outil et le code d'erreur).
- **Réglable** : on peut la désactiver depuis le bouton **Status** et par la configuration locale ; elle est active par défaut.
- **Mesure** : sur chaque scénario de refus du critère 2 et sur chaque annulation du critère 3, la carte affiche le même code que celui reçu par l'assistant ; sur une demande réussie, elle disparaît sans action de l'utilisateur.

Référence observée : rvt-mcp (bimwright) affiche depuis sa v0.6 une carte de ce type dans Revit ; c'est son comportement sur une erreur « aucun document ouvert » qui a motivé cet ajout.
