# Packwise

Petite TUI console pour gérer les dépendances NuGet d'un projet .NET : versions déclarées et résolues,
dernière version disponible, paquets directs et transitifs, vulnérabilités, mise à jour et suppression.

- .NET 10, **aucun paquet NuGet** (System.Text.Json uniquement), interface ANSI écrite à la main
- Résolution, obsolescence et vulnérabilités via le CLI `dotnet list package`
- Historique des versions et métadonnées via l'API NuGet v3 (nuget.org)

## Prérequis

- SDK .NET 10
- Un terminal ANSI (Windows Terminal, PowerShell moderne, etc.)
- Accès réseau pour les dernières versions, vulnérabilités et le détail (sans réseau, la liste reste affichée avec un avertissement)

## Lancer

```
dotnet run -- [dossier]
```

Sans argument, le dossier courant est scanné (4 niveaux, `bin`/`obj`/`node_modules` ignorés) à la recherche de `.csproj`.
Un projet jamais restauré est restauré automatiquement.

## Écrans et raccourcis

**Sélection du projet** : `↑` `↓` naviguer, `Entrée` ouvrir, `F5` re-scanner, `q` ou `Échap` quitter.

**Liste des paquets** (colonnes : paquet, requis, résolu, dernière, `D`irect/`T`ransitif, état)

| Touche | Action |
|---|---|
| `↑` `↓` `PgUp` `PgDn` `Home` `End` | naviguer |
| `/` | rechercher (projets ou paquets) ; `Entrée` valide, `Échap` efface |
| `?` / `F1` | aide |
| `Entrée` / `d` | détail du paquet |
| `u` | mettre à jour à la dernière version stable (confirmation) |
| `x` | supprimer, paquets directs uniquement (confirmation) |
| `1` `2` `3` / `t` | filtre : tous / obsolètes / vulnérables |
| `F5` | recharger |
| `q` / `Échap` | retour au choix du projet |

**Détail** : description, auteurs, dépendances, téléchargements, vulnérabilités et toutes les versions publiées. La colonne `TYPE` (liste) et l'en-tête (détail) indiquent `direct` (référencé dans le csproj) ou `transitif` (tiré par une autre dépendance). Chaque version touchée par un advisory nuget.org est marquée `⚠ vulnérable (sévérité)` : à éviter.
`↑` `↓` choisir une version, `Entrée` ou `a` pour y passer, `q` / `Échap` pour revenir.

`Ctrl+C` quitte à tout moment.

## Limites

- Mettre à jour un paquet transitif l'ajoute comme référence directe dans le csproj.
- La mise à jour échoue avec la gestion centralisée des versions (`Directory.Packages.props`).
- Seule la source nuget.org est interrogée pour le détail ; `dotnet list` respecte, lui, votre `NuGet.Config`.
