<p align="center"><img src="media/icon.png" alt="Packwise" width="128"></p>

# Packwise

Petite TUI console pour gérer les dépendances **NuGet** (.NET) et **npm** (Node.js) d'un projet : versions déclarées
et résolues, dernière version disponible, paquets directs et transitifs, vulnérabilités, mise à jour et suppression.

- .NET 10, **aucun paquet NuGet** (System.Text.Json uniquement), interface ANSI écrite à la main
- Un fournisseur par écosystème derrière une interface commune (`IEcosystem`), comme l'extension VS Code
- NuGet : résolution, obsolescence et vulnérabilités via `dotnet list package` ; versions et métadonnées via l'API NuGet v3 (nuget.org)
- npm : arbre, obsolescence et vulnérabilités via `npm ls`, `npm outdated` et `npm audit` ; versions et métadonnées via le registre npm (registres de `.npmrc` respectés)

## Prérequis

- SDK .NET 10 pour les projets NuGet (et pour lancer depuis les sources)
- Node.js et npm pour les projets npm
- Un terminal ANSI (Windows Terminal, PowerShell moderne, etc.)
- Accès réseau pour les dernières versions, vulnérabilités et le détail (sans réseau, la liste reste affichée avec un avertissement)

## Installer

Chaque tag `vX.Y.Z` publie une [release GitLab](https://gitlab.hexxusdata.com/perso/packwise/tui/-/releases) avec un
exécutable autonome (pas besoin du runtime .NET pour lancer la TUI) pour Linux (x64, arm64), Windows (x64) et
macOS (x64, arm64). Décompresser l'archive et placer `Packwise` (`Packwise.exe`) dans le `PATH`.

Sur macOS, le binaire n'est pas notarié : `xattr -d com.apple.quarantine Packwise` s'il est bloqué au premier lancement.

Pour publier une version : mettre à jour `<Version>` dans `Packwise.csproj`, puis pousser le tag `vX.Y.Z`
correspondant (le pipeline échoue si les deux diffèrent).

## Lancer

```
Packwise [dossier]
```

Ou depuis les sources : `dotnet run -- [dossier]`.

Sans argument, le dossier courant est scanné (4 niveaux ; `bin`, `obj`, `node_modules`, dossiers cachés et dossier
`packages` de l'ancien NuGet ignorés) à la recherche de `.csproj` et de `package.json`. La colonne `TYPE` du choix
du projet indique l'écosystème.

- **NuGet** : un projet jamais restauré est restauré automatiquement.
- **npm** : chaque `package.json` est un projet, racine de workspaces comme membre ; un membre est piloté depuis sa
  racine avec `--workspace`. Les projets **pnpm** et **Yarn** (champ `packageManager` ou fichier de verrouillage) sont
  en **lecture seule** : paquets déclarés, versions installées (`node_modules`) et dernières versions, sans transitifs
  ni vulnérabilités.

## Écrans et raccourcis

**Sélection du projet** : `↑` `↓` naviguer, `Entrée` ouvrir, `F5` re-scanner, `q` ou `Échap` quitter.

**Liste des paquets** (colonnes : paquet, requis, résolu, dernière, type, état)

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

La colonne `TYPE` indique `direct` (déclaré dans le projet), `dev` / `optional` / `peer` (dépendance npm de ce type)
ou `transitif` (tiré par une autre dépendance).

**Détail** : description, auteurs, licence, dépendances, vulnérabilités et toutes les versions publiées. Les versions
à éviter sont marquées : `⚠ vulnérable (sévérité)` pour un advisory nuget.org, `⚠ dépréciée` pour une version npm
dépréciée.
`↑` `↓` choisir une version, `Entrée` ou `a` pour y passer, `q` / `Échap` pour revenir.

`Ctrl+C` quitte à tout moment.

## Limites

- Mettre à jour un paquet transitif l'ajoute comme référence directe (csproj, `dependencies` du package.json).
- NuGet : la mise à jour échoue avec la gestion centralisée des versions (`Directory.Packages.props`) ; seule la source
  nuget.org est interrogée pour le détail (`dotnet list` respecte, lui, votre `NuGet.Config`).
- npm : une mise à jour installe la version exacte et garde le préfixe de la plage (`^`, `~` ou version exacte) ; les
  registres authentifiés ne sont pas gérés pour le détail ; pas de marquage des versions vulnérables dans le détail
  (seules les vulnérabilités de la version installée, via `npm audit`).
- Python (PyPI) n'est pas encore pris en charge par la TUI (il l'est par l'extension VS Code).
