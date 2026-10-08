# Flying Text Modifier

Plugin [Dalamud](https://github.com/goatcorp/Dalamud) pour Final Fantasy XIV : déplace les **textes défilants**
affichés sur ton personnage, directement depuis la **configuration de l'ATH**.

## Utilisation
- Ouvre la configuration de l'ATH : deux cadres « Texte défilant » apparaissent par-dessus l'éditeur.
  - **Soins reçus** : tous les soins reçus, soins sur la durée compris.
  - **Statuts / dégâts subis** : effets de statut gagnés ou perdus et dégâts subis.
- Fais glisser un cadre : le point jaune marque l'endroit où le jeu place les textes. Au lâcher, un texte de
  test apparaît à la nouvelle place.
- Clic droit sur un cadre : retour à la position d'origine du jeu.
- `/flytextmod` ouvre une petite fenêtre (Tester, Tout réinitialiser, langue). Les cadres s'affichent aussi tant
  qu'elle est ouverte.

Les positions sont enregistrées en proportion de l'écran : elles suivent un changement de résolution.
Quand le plugin est désactivé, les textes reprennent leur place d'origine.

Le regroupement des textes est imposé par le jeu. Les textes au-dessus des cibles ne sont pas concernés : ils
suivent la cible dans le décor.

## Compiler et tester
- `dotnet test FlyingTextModifier.Tests` : tests de la logique (recherche en mémoire, conversions, réglages).
- `dotnet build FlyingTextModifier` : la DLL de `FlyingTextModifier\bin\Debug\` est chargée par Dalamud comme
  plugin de développement. La version Debug note aussi dans le journal de Dalamud (lignes `[diag]`) l'état des
  groupes de textes, pour étude.

Projet local, non publié.

## Crédits
- [Dalamud](https://github.com/goatcorp/Dalamud) et [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) :
  cadre des plugins et structures du jeu.
- [FlyTextFilter](https://github.com/Aireil/FlyTextFilter) (Aireil) : c'est ce plugin qui a relevé où le jeu range
  la position des groupes de textes défilants. Aucun code n'en est repris.
- [xUnit](https://xunit.net/) pour les tests.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. Ce plugin n'est ni affilié ni approuvé par Square Enix.
