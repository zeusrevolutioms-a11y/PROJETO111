using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ZeusMapEditor.Core;

namespace ZeusMapEditor.UI;

public sealed class MainForm : Form
{
    private const int MaxUndo = 30;

    private readonly GtaMapDocument doc = new();
    private readonly AssetLibrary assets = new();
    private readonly ListBox objectList = new();
    private readonly TextBox objFilter = new();
    private readonly ListBox ideList = new();
    private readonly TextBox ideSearch = new();
    private readonly PropertyGrid props = new();
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Pronto" };
    private readonly ToolStripStatusLabel assetsLabel = new() { Text = "Modelos: nenhum carregado" };
    private readonly ToolStripStatusLabel coordsLabel = new() { Text = "X —  Y —" };
    private readonly MapCanvas3D canvas = new();
    private SplitContainer outer = null!, left = null!, right = null!;

    private MapObject? selected;
    private bool updating, dirty;
    private readonly List<List<MapObject>> undo = new();
    private readonly List<List<MapObject>> redo = new();
    private List<MapObject> committed = new();
    private readonly List<IdeDefinitionView> allIde = new();

    public MainForm()
    {
        Width = 1500; Height = 900; MinimumSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        canvas.Document = doc; canvas.Assets = assets;
        BuildUi();
        UpdateTitle();
        Shown += (_, _) => FixSplitters();
        FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
    }

    private void BuildUi()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("Arquivo");
        Add(file, "Abrir PASTA do mapa (IPL + IDE + IMG de uma vez)...", (_, _) => OpenMapFolder(), Keys.Control | Keys.Shift | Keys.O);
        Add(file, "Abrir um IPL só...", (_, _) => OpenIpl(), Keys.Control | Keys.O);
        Add(file, "Carregar IDE...", (_, _) => OpenIde());
        file.DropDownItems.Add(new ToolStripSeparator());
        Add(file, "Carregar pasta de modelos (DFF/COL/IMG)...", (_, _) => LoadAssetFolder());
        Add(file, "Carregar arquivo IMG...", (_, _) => LoadImg());
        file.DropDownItems.Add(new ToolStripSeparator());
        Add(file, "Salvar (só os IPL alterados)", (_, _) => Save(), Keys.Control | Keys.S);
        file.DropDownItems.Add(new ToolStripSeparator());
        Add(file, "Sair", (_, _) => Close());

        var edit = new ToolStripMenuItem("Editar");
        Add(edit, "Desfazer", (_, _) => Undo(), Keys.Control | Keys.Z);
        Add(edit, "Refazer", (_, _) => Redo(), Keys.Control | Keys.Y);
        edit.DropDownItems.Add(new ToolStripSeparator());
        Add(edit, "Duplicar selecionado", (_, _) => DuplicateSelected(), Keys.Control | Keys.D);
        var del = Add(edit, "Excluir selecionado(s)", (_, _) => DeleteSelected());
        del.ShortcutKeyDisplayString = "Del (no mapa)";
        Add(edit, "Adicionar modelo selecionado da biblioteca IDE", (_, _) => AddFromIde());

        var view = new ToolStripMenuItem("Exibir");
        var fit = Add(view, "Enquadrar mapa", (_, _) => canvas.FitToObjects());
        fit.ShortcutKeyDisplayString = "F (no mapa)";
        Add(view, "Ir para o selecionado", (_, _) => { if (selected != null) canvas.FocusOn(selected); });
        view.DropDownItems.Add(new ToolStripSeparator());
        var palItem = new ToolStripMenuItem("Texturas com paleta: trocar vermelho/azul") { CheckOnClick = true };
        palItem.CheckedChanged += (_, _) => { TextureDecoder.PaletteIsBgra = palItem.Checked; canvas.ReloadTextures(); };
        view.DropDownItems.Add(palItem);

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view });
        Controls.Add(menu); MainMenuStrip = menu;

        var strip = new StatusStrip();
        strip.Items.Add(status); strip.Items.Add(assetsLabel); strip.Items.Add(coordsLabel);
        Controls.Add(strip);

        outer = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        Controls.Add(outer); outer.BringToFront();

        left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        outer.Panel1.Controls.Add(left);

        var listGroup = new GroupBox { Text = "Objetos do mapa — ID e modelo", Dock = DockStyle.Fill, Padding = new Padding(6) };
        objFilter.Dock = DockStyle.Top; objFilter.PlaceholderText = "Filtrar objetos por nome ou ID...";
        objFilter.TextChanged += (_, _) => RefreshList();
        
        // AQUI: Habilita seleção múltipla na lista
        objectList.Dock = DockStyle.Fill; 
        objectList.IntegralHeight = false;
        objectList.SelectionMode = SelectionMode.MultiExtended; 
        
        objectList.SelectedIndexChanged += (_, _) => { if (!updating && objectList.SelectedItems.Count > 0) SelectObject(objectList.SelectedItems[objectList.SelectedItems.Count - 1] as MapObject, false); };
        objectList.DoubleClick += (_, _) => { if (selected != null) canvas.FocusOn(selected); };
        listGroup.Controls.Add(objectList); listGroup.Controls.Add(objFilter);
        left.Panel1.Controls.Add(listGroup);

        var ideGroup = new GroupBox { Text = "Biblioteca IDE (duplo clique adiciona)", Dock = DockStyle.Fill, Padding = new Padding(6) };
        ideSearch.Dock = DockStyle.Top; ideSearch.PlaceholderText = "Buscar modelo IDE por nome ou ID...";
        ideSearch.TextChanged += (_, _) => RefreshIdeList();
        var addButton = new Button { Text = "Adicionar no centro da vista", Dock = DockStyle.Bottom, Height = 32 };
        addButton.Click += (_, _) => AddFromIde();
        ideList.Dock = DockStyle.Fill; ideList.IntegralHeight = false;
        ideList.DoubleClick += (_, _) => AddFromIde();
        ideGroup.Controls.Add(ideList); ideGroup.Controls.Add(addButton); ideGroup.Controls.Add(ideSearch);
        left.Panel2.Controls.Add(ideGroup);

        right = new SplitContainer { Dock = DockStyle.Fill };
        outer.Panel2.Controls.Add(right);
        var viewGroup = new GroupBox { Text = "Mapa — visão 3D", Dock = DockStyle.Fill, Padding = new Padding(5) };
        viewGroup.Controls.Add(canvas); right.Panel1.Controls.Add(viewGroup);
        var propGroup = new GroupBox { Text = "Propriedades", Dock = DockStyle.Fill, Padding = new Padding(5) };
        props.Dock = DockStyle.Fill; props.PropertySort = PropertySort.Categorized;
        props.PropertyValueChanged += (_, e) => OnPropertyEdited(e.ChangedItem?.PropertyDescriptor?.Name);
        propGroup.Controls.Add(props); right.Panel2.Controls.Add(propGroup);

        canvas.CursorMoved += (x, y) => coordsLabel.Text = $"X {x:0.0}   Y {y:0.0}";
    }

    private void FixSplitters()
    {
        try
        {
            outer.SplitterDistance = 300;
            left.SplitterDistance = Math.Max(120, (int)(left.Height * 0.55));
            right.SplitterDistance = Math.Max(300, right.Width - 340);
        }
        catch { }
    }

    private static ToolStripMenuItem Add(ToolStripMenuItem parent, string text, EventHandler h, Keys keys = Keys.None)
    {
        var item = new ToolStripMenuItem(text, null, h) { ShortcutKeys = keys };
        parent.DropDownItems.Add(item);
        return item;
    }

    private void OpenIpl()
    {
        if (!ConfirmDiscard()) return;
        using var d = new OpenFileDialog { Filter = "GTA San Andreas IPL (*.ipl)|*.ipl|Todos os arquivos|*.*" };
        if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            doc.LoadIpl(d.FileName);
            ResetHistory();
            SelectObject(null, false);
            RefreshList();
            canvas.FitToObjects();
            string msg = $"{doc.Objects.Count} instâncias carregadas de {Path.GetFileName(d.FileName)}";
            if (doc.SkippedLines > 0) msg += $" — {doc.SkippedLines} linha(s) em formato desconhecido foram preservadas sem edição";
            if (doc.Objects.Count == 0) msg += ". Nenhuma instância no formato SA (11 campos) foi encontrada.";
            SetStatus(msg);
            StartPreload(null);
        }
        catch (Exception ex) { Error(ex); }
        UpdateTitle();
    }

    private async void OpenMapFolder()
    {
        if (!ConfirmDiscard()) return;
        using var d = new FolderBrowserDialog { Description = "Pasta do mapa (ex.: data\\maps\\scland)" };
        if (d.ShowDialog() != DialogResult.OK) return;
        string folder = d.SelectedPath;
        try
        {
            SetStatus("Lendo a pasta...");
            Cursor = Cursors.WaitCursor;
            var ipls = new List<string>(); var ides = new List<string>();
            int assetCount = 0; var extra = new List<string>();
            await Task.Run(() =>
            {
                foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f);
                    if (ext.Equals(".ipl", StringComparison.OrdinalIgnoreCase)) ipls.Add(f);
                    else if (ext.Equals(".ide", StringComparison.OrdinalIgnoreCase)) ides.Add(f);
                }
                assetCount += assets.AddFolder(folder);
                var dir = new DirectoryInfo(folder);
                for (int up = 0; up < 6 && dir != null; up++, dir = dir.Parent)
                    foreach (var name in new[] { "gta3.img", "gta_int.img" })
                    {
                        var cand = Path.Combine(dir.FullName, "models", name);
                        if (File.Exists(cand)) { assetCount += assets.AddImg(cand); extra.Add(cand); }
                    }
            });

            doc.Clear(); doc.ModelNames.Clear(); doc.ModelIds.Clear(); doc.ModelTxds.Clear();
            foreach (var f in ides.OrderBy(x => x)) { try { doc.LoadIde(f); } catch { } }
            assets.SetModelTxds(doc.ModelTxds);   // TXD de cada modelo (3º campo do IDE)
            allIde.Clear();
            foreach (var kv in doc.ModelNames.OrderBy(x => x.Key)) allIde.Add(new IdeDefinitionView(kv.Key, kv.Value));
            RefreshIdeList();
            foreach (var f in ipls.OrderBy(x => x)) { try { doc.AddIpl(f); } catch { } }

            ResetHistory();
            SelectObject(null, false);
            RefreshList();
            canvas.FitToObjects();
            string msg = $"{doc.Files.Count} IPL, {doc.Objects.Count} instâncias, {ides.Count} IDE ({allIde.Count} modelos), {assetCount} DFF/COL indexados";
            if (extra.Count > 0) msg += " (inclui " + string.Join(", ", extra.Select(x => Path.GetFileName(x))) + ")";
            else msg += " — gta3.img do jogo NÃO encontrado: use Arquivo > Carregar arquivo IMG";
            if (doc.BinaryFiles > 0) msg += $" · {doc.BinaryFiles} IPL binário ignorado";
            SetStatus(msg);
            StartPreload(null);
        }
        catch (Exception ex) { Error(ex); }
        finally { Cursor = Cursors.Default; UpdateTitle(); }
    }

    private void OpenIde()
    {
        using var d = new OpenFileDialog { Filter = "GTA IDE (*.ide)|*.ide|Todos os arquivos|*.*", Multiselect = true };
        if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            int total = 0;
            foreach (var f in d.FileNames) total += doc.LoadIde(f);
            assets.SetModelTxds(doc.ModelTxds);
            allIde.Clear();
            foreach (var kv in doc.ModelNames.OrderBy(x => x.Key)) allIde.Add(new IdeDefinitionView(kv.Key, kv.Value));
            RefreshIdeList();
            SetStatus($"{total} modelos lidos de {d.FileNames.Length} IDE(s). Total na biblioteca: {allIde.Count}.");
            StartPreload(null);   // carrega os TXD dos modelos do mapa
        }
        catch (Exception ex) { Error(ex); }
    }

    private async void LoadAssetFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Pasta com arquivos .dff / .col / .img do seu mapa" };
        if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            SetStatus("Indexando arquivos...");
            string folder = d.SelectedPath;
            int n = await Task.Run(() => assets.AddFolder(folder));
            SetStatus($"{n} arquivos DFF/COL indexados de {folder}");
            StartPreload(null);
        }
        catch (Exception ex) { Error(ex); }
    }

    private async void LoadImg()
    {
        using var d = new OpenFileDialog { Filter = "Arquivo IMG (*.img)|*.img", Multiselect = true };
        if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            SetStatus("Indexando IMG...");
            var files = d.FileNames;
            int n = await Task.Run(() => files.Sum(f => assets.AddImg(f)));
            SetStatus(n == 0 ? "Nenhum DFF/COL encontrado (IMG v1 precisa do .dir na mesma pasta)." : $"{n} DFF/COL indexados dos IMG.");
            StartPreload(null);
        }
        catch (Exception ex) { Error(ex); }
    }

    private async void StartPreload(IEnumerable<string>? names)
    {
        var list = (names ?? doc.Objects.Select(o => o.ModelName)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            await assets.PreloadAsync(list, new Progress<string>(s => { assetsLabel.Text = s; canvas.Invalidate(); }));
        }
        catch (Exception ex) { assetsLabel.Text = "Erro ao ler modelos: " + ex.Message; return; }
        var noFile = list.Where(n => !assets.HasSource(n)).ToList();
        int bad = assets.Unreadable.Count;
        assetsLabel.Text = $"TXD: {assets.TxdLoadedCount}/{assets.TxdFileCount} · texturas: {assets.TextureCount} · DFF: {assets.DffCount} · COL: {assets.CollisionCount} · modelos sem DFF: {noFile.Count}/{list.Count}" +
                           (bad > 0 ? $" · DFF ilegíveis: {bad}" : "");
        canvas.Invalidate();
    }

    private void Save()
    {
        try
        {
            var changed = doc.ChangedFiles();
            if (changed.Count == 0) { dirty = false; UpdateTitle(); SetStatus("Nada para salvar: nenhum IPL foi alterado."); return; }
            int baks = 0;
            foreach (var f in changed)
            {
                if (File.Exists(f.Path) && !File.Exists(f.Path + ".bak")) { File.Copy(f.Path, f.Path + ".bak"); baks++; }
                doc.SaveFile(f);
            }
            dirty = false; UpdateTitle();
            SetStatus($"Salvo(s) {changed.Count} IPL...");
        }
        catch (Exception ex) { Error(ex); }
    }

    private bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var r = MessageBox.Show("Há alterações não salvas. Salvar antes de continuar?", "Zeus Map Editor",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) { Save(); return !dirty; }
        return true;
    }

    private void RefreshList()
    {
        updating = true;
        objectList.BeginUpdate();
        objectList.Items.Clear();
        string f = objFilter.Text.Trim();
        var items = doc.Objects.Where(o => f.Length == 0 ||
            o.ModelName.Contains(f, StringComparison.OrdinalIgnoreCase) || o.Id.ToString().Contains(f)).Cast<object>().ToArray();
        objectList.Items.AddRange(items);
        if (selected != null) objectList.SelectedItem = selected;
        objectList.EndUpdate();
        updating = false;
        canvas.Invalidate();
    }

    private void RefreshIdeList()
    {
        string f = ideSearch.Text.Trim();
        ideList.BeginUpdate();
        ideList.Items.Clear();
        ideList.Items.AddRange(allIde.Where(d => f.Length == 0 ||
            d.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || d.Id.ToString().Contains(f)).Cast<object>().ToArray());
        ideList.EndUpdate();
    }

    private void SelectObject(MapObject? o, bool fromCanvas)
    {
        selected = o;
        canvas.Selected = o;
        props.SelectedObject = o;
        updating = true;
        if (o == null) objectList.ClearSelected();
        // Não força a seleção se já estiver selecionado (para não quebrar a seleção múltipla)
        else if (objectList.Items.Contains(o) && !objectList.SelectedItems.Contains(o)) objectList.SelectedItem = o;
        updating = false;
        canvas.Invalidate();
    }

    private void OnPropertyEdited(string? name)
    {
        if (selected == null) return;
        if (name == nameof(MapObject.ModelName) && doc.ModelIds.TryGetValue(selected.ModelName, out int id)) selected.Id = id;
        else if (name == nameof(MapObject.Id) && doc.ModelNames.TryGetValue(selected.Id, out var nm)) selected.ModelName = nm;
        if (name == nameof(MapObject.ModelName) || name == nameof(MapObject.Id)) StartPreload(new[] { selected.ModelName });
        props.Refresh();
        int i = objectList.Items.IndexOf(selected);
        if (i >= 0) { updating = true; objectList.Items[i] = selected; objectList.SelectedIndex = i; updating = false; }
        CommitEdit();
        canvas.Invalidate();
    }

    private List<MapObject> Snapshot() => doc.Objects.Select(o => o.Clone()).ToList();

    private void ResetHistory()
    {
        undo.Clear(); redo.Clear();
        committed = Snapshot();
        dirty = false;
    }

    private void CommitEdit()
    {
        undo.Add(committed);
        if (undo.Count > MaxUndo) undo.RemoveAt(0);
        redo.Clear();
        committed = Snapshot();
        dirty = true;
        UpdateTitle();
    }

    private void Undo()
    {
        if (undo.Count == 0) return;
        int si = selected == null ? -1 : doc.Objects.IndexOf(selected);
        redo.Add(committed);
        committed = undo[^1]; undo.RemoveAt(undo.Count - 1);
        ApplySnapshot(committed, si);
    }

    private void Redo()
    {
        if (redo.Count == 0) return;
        int si = selected == null ? -1 : doc.Objects.IndexOf(selected);
        undo.Add(committed);
        committed = redo[^1]; redo.RemoveAt(redo.Count - 1);
        ApplySnapshot(committed, si);
    }

    private void ApplySnapshot(List<MapObject> snap, int selectedIndex)
    {
        doc.ReplaceObjects(snap.Select(o => o.Clone()));
        dirty = true; UpdateTitle();
        selected = selectedIndex >= 0 && selectedIndex < doc.Objects.Count ? doc.Objects[selectedIndex] : null;
        canvas.Selected = selected; props.SelectedObject = selected;
        RefreshList();
    }

    private void DeleteSelected()
    {
        // AQUI: Permite deletar todos os itens selecionados de uma vez
        if (objectList.SelectedItems.Count == 0) return;

        var itemsToDelete = objectList.SelectedItems.Cast<MapObject>().ToList();
        
        foreach (var item in itemsToDelete)
        {
            doc.Remove(item);
        }

        SelectObject(null, false);
        RefreshList();
        CommitEdit();
        SetStatus($"{itemsToDelete.Count} objeto(s) excluído(s) (Ctrl+Z desfaz). Salve o IPL para gravar.");
    }

    private void DuplicateSelected()
    {
        if (selected == null) return;
        var c = selected.Clone(); c.X += 2; c.Y += 2;
        doc.Add(c);
        RefreshList(); SelectObject(c, false);
        CommitEdit();
        SetStatus($"{c.ModelName} duplicado.");
    }

    private void AddFromIde()
    {
        if (ideList.SelectedItem is not IdeDefinitionView def)
        { SetStatus("Carregue um IDE (Arquivo > Carregar IDE) e selecione um modelo na biblioteca."); return; }
        string? target = selected?.SourceFile ?? doc.Files.FirstOrDefault()?.Path;
        if (string.IsNullOrEmpty(target)) { SetStatus("Abra um IPL/pasta primeiro."); return; }
        var o = new MapObject
        {
            SourceFile = target,
            Id = def.Id, ModelName = def.Name, X = 0, Y = 0,
            Z = selected?.Z ?? 0f, Interior = selected?.Interior ?? 0
        };
        doc.Add(o);
        RefreshList(); SelectObject(o, false);
        StartPreload(new[] { o.ModelName });
        CommitEdit();
        SetStatus($"{def.Name} adicionado. Arraste no mapa ou edite as propriedades.");
    }

    private void UpdateTitle()
    {
        string f = doc.Files.Count == 0 ? "sem arquivo" : doc.Files.Count == 1 ? Path.GetFileName(doc.Files[0].Path) : $"{doc.Files.Count} IPL";
        Text = $"Zeus Map Editor 0.7 — {f}{(dirty ? " *" : "")}";
    }
    private void SetStatus(string s) => status.Text = s;
    private static void Error(Exception ex) => MessageBox.Show(ex.Message, "Zeus Map Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
    private sealed record IdeDefinitionView(int Id, string Name) { public override string ToString() => $"{Id} - {Name}"; }
}