# Zeus Map Editor 0.4

## Compilar
Windows 10/11 + Visual Studio 2022 + .NET 8 SDK. Abra `ZeusMapEditor.csproj` e aperte F5. Sem pacotes NuGet.

## Como usar
**Jeito rápido:** Arquivo > **Abrir PASTA do mapa** (ex.: `data\maps\scland`). Carrega todos os IPL, IDE e IMG da pasta e procura `models\gta3.img` do jogo sozinho. Ao salvar, só os IPL alterados são gravados (com `.bak`).

Jeito manual:
1. **Arquivo > Abrir um IPL só** — o mapa é enquadrado automaticamente.
2. **Arquivo > Carregar IDE** (um ou vários) — nomes/IDs dos modelos.
3. **Arquivo > Carregar pasta de modelos (DFF/COL/IMG)** — aponte para a pasta do seu mapa
   (procura .dff, .col e .img em subpastas). Também há "Carregar arquivo IMG".
   Com isso os props aparecem como silhuetas e a colisão em vermelho.

## Mapa
- Roda: zoom no cursor · botão direito/meio: mover a vista · **F**: enquadrar tudo
- Clique: seleciona · **arraste**: move o objeto (Ctrl = encaixa em 1 unidade)
- Setas: move 1 unidade (Shift = 10, Alt = 0,1) · PageUp/PageDown: altura Z · Del: exclui
- Ctrl+Z / Ctrl+Y: desfazer/refazer (30 passos) · Ctrl+D: duplicar · Ctrl+S: salvar
- Menu Exibir: liga/desliga props, malha (wireframe), colisão, marcadores, nomes e grade.

## Propriedades
Posição, **Rotação Z em graus** (mais fácil que o quaternion), interior, LOD. Trocar o nome ou o ID
atualiza o outro se o IDE estiver carregado.

## Salvando
- Cria `arquivo.ipl.bak` na primeira vez (o original fica intacto).
- Seções que não são `inst` são preservadas. Linhas de `inst` em formato desconhecido também
  (ficam no topo da seção, sem edição). Comentários dentro de `inst` vão para o topo.

## Limitações
- Só IPL texto no formato San Andreas (11 campos por linha). IPL binário (`bnry`) e formatos III/VC não são editados.
- Visão 2D de cima. Os props aparecem como silhueta (caixa/malha), sem textura e sem 3D.
- DFF nativo de PS2/Xbox não é lido.
