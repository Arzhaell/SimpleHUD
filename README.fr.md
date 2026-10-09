# Simple HUD

🇬🇧 [English version](README.md)

Plugin [Dalamud](https://github.com/goatcorp/Dalamud) pour Final Fantasy XIV : des réglages simples de l'affichage,
directement depuis la **configuration de l'ATH**. Il déplace les **textes défilants** affichés sur ton personnage
et règle au pixel près tous les **éléments de l'ATH** du jeu.

Anciennement « Flying Text Modifier » (jusqu'à la version 1.1.0).

## Installation
1. En jeu, tape `/xlsettings` et ouvre l'onglet **Expérimental**.
2. Dans **Custom Plugin Repositories**, ajoute cette adresse, clique sur **+**, coche la case puis enregistre :
   ```
   https://raw.githubusercontent.com/Arzhaell/SimpleHUD/main/repo.json
   ```
3. Tape `/xlplugins`, cherche **Simple HUD** et installe-le.

## Utilisation
- Ouvre la configuration de l'ATH : les cadres « Texte défilant » apparaissent par-dessus l'éditeur, avec la
  fenêtre du plugin.
- **Textes sur toi** (position fixe à l'écran) : la fenêtre propose quatre dispositions.
  - **Tout regroupé** (comme le jeu) : un seul cadre ; soins d'un côté, statuts et dégâts subis de l'autre.
  - **Soins séparés** : soins reçus · statuts et dégâts subis.
  - **Statuts séparés** : statuts · soins et dégâts subis.
  - **Tout séparé** : soins reçus · statuts · dégâts subis.

  Le jeu ne connaît que deux blocs (soins ; statuts et dégâts subis). Pour séparer les statuts, le plugin déplace
  chaque texte de statut un par un. Quand un texte arrive, le jeu pousse vers le bas ceux de son bloc pour lui
  faire de la place : le plugin annule cette poussée pour les textes d'un autre cadre, et chaque cadre défile de son
  côté.
- **Autres textes dans leur propre cadre** (case à cocher) : l'EXP et les objets obtenus, que le jeu range avec les
  dégâts subis, ont leur cadre à part, quelle que soit la disposition. Les PM récupérés restent avec les soins.
- **Sur la cible** : tes coups sur la cible. Ces textes suivent toujours la cible ; on règle l'écart avec elle
  (le cercle marque la cible, ou ton personnage s'il n'y en a pas).
- Fais glisser un cadre (Maj enfoncée : déplacement fin) : le point jaune marque l'endroit où le jeu place les
  textes. Au lâcher, des textes de test défilent à la nouvelle place.
- Au pixel près : l'onglet « Textes défilants » de la fenêtre du plugin donne X et Y de chaque cadre, avec des
  boutons − / + (Ctrl+clic : par 10).
- Clic droit sur un cadre, ou bouton ↺ : retour à la position d'origine du jeu.
- **Afficher / taille** : pour les buffs / débuffs, les soins (PM récupérés compris), les dégâts subis (sur toi),
  les dégâts infligés (sur tes cibles) et les autres textes (EXP, objets…), la fenêtre permet de les masquer ou de
  régler leur taille, de 50 % à 200 % de la taille du jeu. Le rebond des critiques est gardé.
- **Tester tous les textes** fait défiler sur ton personnage tous les types de textes : soins, soins critiques,
  statuts bénéfiques et néfastes gagnés puis perdus (de vrais statuts du jeu, tirés au hasard à chaque essai),
  dégâts, critiques, coups directs, auto-attaques, esquives, EXP, PM…
- `/simplehud` ouvre la fenêtre hors de l'éditeur d'ATH ; les cadres s'affichent tant qu'elle est ouverte.

### ATH du jeu au pixel
- Dans la configuration de l'ATH, survole un élément (barres de raccourcis, équipe, cible, boussole…, y compris
  ceux masqués en jeu) : juste au-dessus de son cadre s'affiche la position de son coin haut-gauche à l'écran, en
  pixels. L'onglet « ATH du jeu » de la fenêtre du plugin peut les afficher toutes en même temps.
- **Au pixel près** : sous l'élément sélectionné, un panneau donne X et Y (boutons − / +, Ctrl+clic : par 10). Il
  bouge comme si tu le faisais glisser : c'est le bouton « Sauvegarder » de l'éditeur qui garde la nouvelle
  position, et fermer sans sauvegarder l'annule.
- La version Debug note en plus dans le journal de Dalamud (lignes `[hud]`) ce que fait l'éditeur d'ATH.

Les positions sont enregistrées en proportion de l'écran : elles suivent un changement de résolution.
Quand le plugin est désactivé, les textes reprennent leur place d'origine.

Le regroupement des textes est imposé par le jeu. Pour décaler les textes sur la cible, le plugin déplace tout
le calque des textes défilants et compense la position des deux groupes du personnage.

Le plugin suit la langue de Dalamud (français ou anglais) ; la fenêtre permet de la forcer.

Si [FlyTextFilter](https://github.com/Aireil/FlyTextFilter) est chargé aussi et déplace ces textes, les positions
choisies dans Simple HUD l'emportent.

## Avertissement
Comme tout outil tiers, l'utilisation de Dalamud et de ses plugins est contraire aux conditions d'utilisation de
FFXIV. Tu l'utilises à tes risques.

## Compiler et tester
- `dotnet test SimpleHUD.Tests -c Release` : tests de la logique (recherche en mémoire, conversions, réglages).
- `dotnet build SimpleHUD -c Release` : pour l'essayer, ajoute `SimpleHUD\bin\Release\SimpleHUD.dll` aux
  emplacements des plugins de développement (`/xlsettings`, onglet **Expérimental**) ; Dalamud la recharge toute
  seule, même jeu ouvert. Il ne charge pas en même temps la version installée depuis le dépôt : désactive-la.
- La version Debug (`dotnet build SimpleHUD`) note en plus dans le journal de Dalamud (lignes `[diag]`)
  l'état des groupes et de chaque texte, pour la mise au point. Elle écrit beaucoup : à ne charger que pour étudier.

## Publier une nouvelle version
Pousser un tag annoté `vX.Y.Z` suffit : le workflow GitHub lance les tests, compile le plugin, crée la release (ses
notes sont le message du tag) et met à jour `repo.json`.

```bash
git tag -a v1.2.0 --cleanup=verbatim -F notes.txt
git push origin v1.2.0
```

## Crédits
- [Dalamud](https://github.com/goatcorp/Dalamud) et [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) :
  cadre des plugins et structures du jeu.
- [FlyTextFilter](https://github.com/Aireil/FlyTextFilter) (Aireil) : c'est ce plugin qui a relevé où le jeu range
  la position des groupes de textes défilants. Aucun code n'en est repris.
- [xUnit](https://xunit.net/) pour les tests.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. Ce plugin n'est ni affilié ni approuvé par Square Enix.
