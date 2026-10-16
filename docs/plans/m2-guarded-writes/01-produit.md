# Produit : M2 — écritures gardées

Rédigé le 2026-10-13 (autopilote, à valider par le mainteneur).

## Problème

Avec M1, l'assistant peut regarder la maquette sans pouvoir la toucher. Dès qu'il faut *faire* quelque chose — renommer vingt gabarits de vue, créer un filtre, corriger un paramètre sur cent feuilles — le BIM manager repasse par les outils d'avant : un relais qui exécute n'importe quoi sur n'importe quel document ouvert, sans aperçu, sans trace de ce qui a changé, et dont les erreurs laissent la maquette dans un état intermédiaire que personne ne sait annuler proprement.

Ce que le BIM manager veut : demander une modification à l'assistant, voir **avant** ce qu'elle toucherait, décider de la garder ou non, et retrouver ensuite dans Revit **une seule** entrée « Annuler » pour tout ce que l'assistant a fait — et jamais sur sa vraie affaire par accident.

## Qui est l'utilisateur final

- **Le BIM manager** : il travaille sur une copie SANDBOX de son gabarit ou de son affaire, demande des modifications en langage courant, garde la main sur ce qui est conservé.
- **L'assistant** : il écrit le script, le fait tourner d'abord « à blanc », lit le reçu de changement, et ne demande la validation qu'une fois sûr de l'effet.

## Critère de réussite

1. **Une modification = une entrée Annuler.** Sur une copie SANDBOX, une demande `run_modify` validée laisse exactement **une** entrée dans la pile Annuler de Revit, nommée `Agent: <titre de la demande>` ; l'annuler remet la maquette comme avant. Mesure : la pile Annuler est lue avant et après.
2. **À blanc par défaut.** La même demande sans le mot « garde » (dry-run, la valeur par défaut) ne laisse **aucune** entrée Annuler, mais renvoie le même reçu de changement (ajoutés, modifiés, supprimés, par catégorie) que si elle avait été gardée. Mesure : pile Annuler vide, reçu identique.
3. **Jamais hors SANDBOX par accident.** La même demande sur un document dont le nom de fichier ne porte aucun marqueur SANDBOX est refusée `SANDBOX_REQUIRED` avant d'avoir rien exécuté ; sur un document non enregistré ou de référence, elle passe en « Garder / Annuler » dans Revit, jamais en automatique. Mesure : les trois cas rejoués.
4. **Ce qui casse est annulé en entier.** Une erreur de script, un dépassement de temps, un échec Revit (avertissement bloquant) ou une annulation depuis l'assistant ne laissent rien : la pile Annuler est vide, le reçu dit `rolled_back` et pourquoi. Mesure : les quatre cas rejoués.
5. **On sait ce que l'assistant a fait.** Après une séance, `get_change_records` liste chaque modification (titre, résultat, nombre d'éléments par catégorie, raison donnée par l'assistant) pour le document ouvert ; rien de cette liste n'est écrit dans la maquette. Mesure : la liste correspond aux demandes de la séance.

## L'annonce avant la fonctionnalité

> Avec RIFT M2, votre assistant peut modifier votre maquette — mais seulement une copie SANDBOX, seulement à blanc tant que vous n'avez pas dit « garde », et toujours en une seule entrée Annuler. Chaque modification revient avec son reçu : ce qui a été ajouté, modifié, supprimé, par catégorie. Sur un document sans marqueur, l'assistant est refusé avant d'avoir rien fait ; sur un document non enregistré, Revit vous demande Garder ou Annuler, avec les éléments touchés en surbrillance. Une erreur, un temps dépassé, un avertissement bloquant : tout est annulé, et vous le voyez sur la carte d'activité.

## Ce que M2 ne fait pas (volontairement)

- Pas d'outils typés (`apply_view_template`, `set_element_parameters`…) : M3. M2 donne le code libre gardé qui les rendra sûrs.
- Pas de C# (M4), pas de commandes figées, pas de sauvegarde ni de synchronisation depuis l'assistant (M4, `never_auto`).
- Pas d'isolement temporaire de vue pour l'aperçu : la sélection suffit (ADR-009 le laisse optionnel).
- Pas de fenêtre « History » dans Revit (chantier UI, M5) : l'historique se lit depuis l'assistant.

## Écrans

- **Boîte Garder / Annuler** (politique `ask` seulement) : une `TaskDialog` Revit, identifiant `rift.ask`, titre de la demande, reçu en chiffres (ajoutés / modifiés / supprimés), deux liens de commande « Garder » et « Annuler », **Annuler par défaut** ; les éléments touchés sont sélectionnés dans Revit pendant que la boîte est ouverte.
- **Carte d'activité** (déjà là) : montre `run_modify · committed`, `· rolled_back (dry_run)`, `· refused SANDBOX_REQUIRED`… avec le même code que l'assistant.
