# Flying Text Modifier

Plugin [Dalamud](https://github.com/goatcorp/Dalamud) pour Final Fantasy XIV : déplace les **textes défilants**
affichés sur ton personnage, directement depuis la **configuration de l'ATH**.

## Utilisation
- Ouvre la configuration de l'ATH : les cadres « Texte défilant » apparaissent par-dessus l'éditeur, avec la
  fenêtre du plugin.
- **Textes sur toi** (position fixe à l'écran) : la fenêtre propose quatre dispositions.
  - **Tout regroupé** (comme le jeu) : un seul cadre ; soins d'un côté, statuts et dégâts subis de l'autre.
  - **Soins séparés** : soins reçus · statuts et dégâts subis.
  - **Statuts séparés** : statuts · soins et dégâts subis.
  - **Tout séparé** : soins reçus · statuts · dégâts subis.

  Le jeu ne connaît que deux blocs (soins ; statuts et dégâts subis). Pour séparer les statuts, le plugin déplace
  chaque texte de statut un par un ; le jeu les empilant toujours avec les dégâts, un bloc peut garder un trou
  quand les deux arrivent en même temps.
- **Sur la cible** : tes coups sur la cible. Ces textes suivent toujours la cible ; on règle l'écart avec elle
  (le cercle marque la cible, ou ton personnage s'il n'y en a pas).
- Fais glisser un cadre (Maj enfoncée : déplacement fin) : le point jaune marque l'endroit où le jeu place les
  textes. Au lâcher, des textes de test défilent à la nouvelle place.
- Au pixel près : la fenêtre du plugin donne X et Y de chaque cadre, avec des boutons − / + (Ctrl+clic : par 10).
- Clic droit sur un cadre, ou bouton ↺ : retour à la position d'origine du jeu.
- **Afficher / taille** : pour les buffs / débuffs, les soins, les dégâts (sur toi comme sur la cible) et les
  autres textes (EXP, PM…), la fenêtre permet de les masquer ou de régler leur taille, de 50 % à 200 % de la
  taille du jeu. Le rebond des critiques est gardé.
- **Tester tous les textes** fait défiler sur ton personnage tous les types de textes : soins, soins critiques,
  statuts bénéfiques et néfastes gagnés puis perdus (de vrais statuts du jeu, tirés au hasard à chaque essai),
  dégâts, critiques, coups directs, auto-attaques, esquives…
- `/flytextmod` ouvre la fenêtre hors de l'éditeur d'ATH ; les cadres s'affichent tant qu'elle est ouverte.

Les positions sont enregistrées en proportion de l'écran : elles suivent un changement de résolution.
Quand le plugin est désactivé, les textes reprennent leur place d'origine.

Le regroupement des textes est imposé par le jeu. Pour décaler les textes sur la cible, le plugin déplace tout
le calque des textes défilants et compense la position des deux groupes du personnage.

## Compiler et tester
- `dotnet test FlyingTextModifier.Tests` : tests de la logique (recherche en mémoire, conversions, réglages).
- `dotnet build FlyingTextModifier -c Release` : la DLL de `FlyingTextModifier\bin\Release\` est celle que Dalamud
  charge comme plugin de développement (rechargée toute seule, même jeu ouvert).
- La version Debug (`dotnet build FlyingTextModifier`) note en plus dans le journal de Dalamud (lignes `[diag]`)
  l'état des groupes et de chaque texte, pour la mise au point. Elle écrit beaucoup : à ne charger que pour étudier.

Projet local, non publié.

## Crédits
- [Dalamud](https://github.com/goatcorp/Dalamud) et [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) :
  cadre des plugins et structures du jeu.
- [FlyTextFilter](https://github.com/Aireil/FlyTextFilter) (Aireil) : c'est ce plugin qui a relevé où le jeu range
  la position des groupes de textes défilants. Aucun code n'en est repris.
- [xUnit](https://xunit.net/) pour les tests.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. Ce plugin n'est ni affilié ni approuvé par Square Enix.
