# Projet : petit tracker de temps pour Windows (C#)

Je dois noter le temps que je passe sur chaque tâche pour facturer mes heures. Je veux un outil personnel minimaliste, qui prend le moins de place possible à l'écran. Aucune donnée ne doit partir en ligne : tout reste en local sur mon PC. Priorité absolue : rester simple.

## Contraintes techniques

- C# / .NET, interface WinForms (ou WPF si tu le juges nettement mieux).
- Livrable : un seul .exe autonome, sans rien à installer (`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`).
- Windows 10/11. Pas de compte, pas de réseau, pas de télémétrie.
- Lecture/écriture du fichier Excel avec une bibliothèque sous licence permissive compatible usage professionnel (ex. ClosedXML). Indique la licence de chaque dépendance.
- Code simple et lisible : c'est un petit outil, pas d'architecture lourde.

## Environnement de développement

- Tu tournes dans une VM Windows temporaire sur Google Cloud, à laquelle j'accède depuis mon Mac en bureau à distance. Elle sera supprimée à la fin du projet : tout ce qui compte doit être dans le repo GitHub (code, config d'exemple, instructions de build). Pense à pousser régulièrement.
- Installe ce qui manque (SDK .NET, Git) et dis-moi ce que tu installes.
- Tu peux compiler et lancer l'appli toi-même pour tester. Les comportements visuels (ligne d'état, plein écran F11, bureaux virtuels, verrouillage de session), c'est moi qui les vérifie : donne-moi à chaque fois une courte liste de choses à tester.
- Excel n'est peut-être pas installé dans la VM : pour tester le mode édition, n'importe quel programme qui ouvre le .xlsx fait l'affaire (LibreOffice, ou un petit script).
- Je validerai aussi l'étape 0 sur mon vrai PC de travail.

## Étape 0 : prototype à valider AVANT tout le reste

Je travaille souvent en plein écran (F11) dans un terminal, sur plusieurs bureaux virtuels. Crée d'abord un mini-prototype qui affiche uniquement la ligne colorée décrite plus bas, puis arrête-toi pour que je teste :

- est-elle visible par-dessus un terminal en plein écran (F11) ?
- est-elle visible sur TOUS les bureaux virtuels ?

Piste : une fenêtre avec le style WS_EX_TOOLWINDOW semble apparaître sur tous les bureaux virtuels (non documenté officiellement). Si ça ne marche pas, plan B : la bibliothèque VirtualDesktop (Grabacr07) pour épingler la fenêtre, en sachant qu'elle repose sur des API internes de Windows. Si rien ne marche, on abandonne la ligne et on garde seulement l'icône.

## Interface

### 1. La ligne d'état (toujours visible)

- Fenêtre sans bordure, 3 px de haut (réglable), sur toute la largeur de l'écran, en haut ou en bas (réglable). Une par écran si plusieurs écrans.
- Toujours au premier plan, transparente aux clics (la souris passe à travers), ne prend jamais le focus, absente de la barre des tâches et d'Alt+Tab.
- Couleur de la tâche en cours ; gris quand je suis en pause. Pas de rappel ni de clignotement.

### 2. L'icône dans la zone de notification

- Petit carré de la même couleur que la ligne.
- Au survol : nom de la tâche et temps écoulé.
- Clic : ouvre / ferme la fenêtre de gestion des tâches.

### 3. La fenêtre de gestion des tâches (petite, s'ouvre au-dessus de l'icône)

- En-tête : tâche en cours, temps écoulé, bouton Pause / Reprendre.
- Liste des tâches (pastille de couleur, nom, tags) : un clic sur une tâche la démarre.
- Sur chaque tâche, un bouton crayon pour modifier son nom, ses tags et sa couleur.
- Bouton « Nouvelle tâche » : nom, tags (séparés par des virgules), couleur → « Créer et démarrer ».
- Bouton « Modifier dans Excel » (voir « Données »).
- La fenêtre se ferme quand elle perd le focus ou avec Échap.
- Structure plate : pas de projets ni de hiérarchie, seulement des tags.

### 4. Les couleurs des tâches

- À la création, une couleur est proposée automatiquement : générer des candidates à luminosité et saturation fixes dans l'espace OKLCH (lisibles avec du texte blanc), et garder celle dont la distance perceptuelle minimale aux couleurs des 5 dernières tâches utilisées est la plus grande. Éviter les gris (réservés à la pause).
- À la création et à l'édition, je peux choisir n'importe quelle couleur : le sélecteur Windows standard (`ColorDialog`) et un champ pour saisir un code hexadécimal (#2F6FD6). Aucune restriction sur ce choix manuel.

## Données : un seul fichier Excel, source de vérité

Toutes les données vivent dans un seul fichier `heures.xlsx` (emplacement réglable). Pas d'autre base de données ni de fichier d'état.

Feuille « Tâches » :

| ID | Nom | Tags | Couleur |
| --- | --- | --- | --- |
| T1 | Rapport mensuel | #clientA, #facturable | #2F6FD6 |

Feuille « Événements » (journal, une ligne par changement d'état) :

| Horodatage | Type | ID tâche |
| --- | --- | --- |
| 17.10.2026 09:00 | Début | T1 |
| 17.10.2026 10:15 | Début | T3 |
| 17.10.2026 12:00 | Pause | |

- Seulement deux types d'événements : « Début » (démarrer ou reprendre une tâche, ce qui termine implicitement la précédente) et « Pause » (arrêter sans démarrer autre chose). Pas d'événement « Stop ».
- Les durées ne sont pas stockées : elles se déduisent du temps jusqu'à l'événement suivant. Si l'appli est fermée, le dernier « Début » continue logiquement de compter.
- L'appli n'écrit qu'en ajoutant des lignes à la fin des Événements, et ne modifie la feuille Tâches que via ses propres actions (créer, éditer).
- À la lecture, trier les événements par horodatage (je peux insérer des lignes n'importe où à la main). La tâche en cours = le dernier événement.
- Les événements référencent la tâche par son ID : renommer une tâche ne casse pas l'historique. Un ID inconnu (tâche supprimée à la main) ne doit pas faire planter l'appli : l'afficher comme « tâche inconnue (T3) ».
- Tags dans une seule colonne, séparés par des virgules.
- Au démarrage, si le fichier n'existe pas, le créer avec les deux feuilles et les en-têtes.

### Verrou du fichier et mode édition (important)

- Tant que l'appli tourne, elle garde le fichier ouvert et verrouillé en écriture (partage en lecture seule autorisé si possible, pour que je puisse le consulter dans Excel sans pouvoir l'enregistrer). Elle est ainsi la seule à écrire : jamais de conflit, pas de file d'attente.
- Le bouton « Modifier dans Excel » : l'appli libère le verrou, ouvre le fichier dans l'application par défaut, et passe en mode édition. Dans ce mode, elle n'écrit plus rien : changer de tâche, pause, créer/éditer sont désactivés, et la fenêtre affiche « Ouvert dans Excel, en attente de fermeture ». La ligne et l'icône gardent la couleur de la tâche en cours, avec un petit indicateur sur l'icône.
- Quand le fichier est refermé (détecter quand le verrou peut être repris), l'appli recharge tout depuis l'Excel, reprend le verrou et revient en mode normal.
- Avant chaque écriture, faire une copie de sauvegarde datée (au moins une par jour, garder les N dernières) dans un sous-dossier.

## Verrouillage de session (Win+L)

- Écouter les événements de verrouillage / déverrouillage de session Windows et garder l'heure du verrouillage en mémoire.
- Au déverrouillage, si le verrouillage a duré plus de 5 minutes (réglable) et qu'une tâche était en cours, afficher une petite fenêtre : « Ton PC était verrouillé de 12:02 à 13:05 (1 h 03). C'était : [Pause] · [Toujours sur « Rapport mensuel »] · [Autre tâche…] ».
    - Pause : ajouter un événement Pause à l'heure du verrouillage et un Début de la tâche à l'heure du déverrouillage.
    - Toujours sur la tâche : ne rien ajouter.
    - Autre tâche : choisir une tâche dans la liste, ajouter son Début à l'heure du verrouillage.

## Vérification au démarrage

Au lancement de l'appli, si le dernier événement est un « Début » d'un jour précédent, demander : « Dernière tâche : « Rapport mensuel », démarrée hier à 16:30. À quelle heure as-tu arrêté ? », et ajouter la Pause correspondante. Ça couvre l'oubli de pause le soir et une extinction pendant un verrouillage.

## Pour mon prompt Bash (WSL)

À chaque changement d'état, écrire aussi un petit fichier texte `tache_en_cours.txt` (une ligne : nom de la tâche, ou « pause »), pour que mon prompt Bash puisse l'afficher avec un simple `cat`, sans lire le .xlsx. C'est une simple sortie d'affichage, pas une donnée.

## Réglages (fichier de config simple à côté de l'exe)

Chemin du .xlsx, position de la ligne (haut/bas), épaisseur de la ligne, seuil de la question au déverrouillage, lancement au démarrage de Windows.

## Démarche

1. Étape 0 (prototype de la ligne), puis tu t'arrêtes pour que je teste.
2. Ensuite l'icône, la fenêtre de gestion, l'écriture Excel avec le verrou et le mode édition.
3. Puis le verrouillage de session et la vérification au démarrage.
4. Explique-moi comment compiler et publier l'exe.

Pose-moi tes questions si quelque chose n'est pas clair avant de coder.
