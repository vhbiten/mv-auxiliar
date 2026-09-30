using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Tela ÚNICA para gerenciar as listas de contagem dos dois estoques. Master-detail
    // responsivo (redimensionável): árvore à esquerda, editor à direita com a lista
    // preenchendo o espaço. Adicionar/remover/restaurar/renomear SALVAM na hora (auto-save):
    // o usuário só fecha a janela. Nomes/validação vêm ao vivo do MV.
    public class GerenciarListasDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static readonly string[] Dias =
            { "Segunda-feira", "Terça-feira", "Quarta-feira", "Quinta-feira", "Sexta-feira", "Sábado", "Domingo" };

        private class Alvo
        {
            public bool Caf;
            public int Dia; public string Periodo;
            public int Semana; public string Tipo;
        }

        // Marca a linha "Adicionar tipo" (última de cada semana, só no CAF).
        private class AddNovo { public int Semana; }

        private readonly float _sc;
        private readonly Session _session;

        private StyledComboBox cmbEstoque;
        private TreeView tree;
        private Label lblTituloLista;
        private Label lblNomeMicro;
        private LineEdit txtNome;
        private Label lblCodMicro;
        private Label lblContagem;
        private TextBox txtNovoCodigo;
        private PrimaryButton btnAdicionar, btnRestaurar;
        private GridPro gridCodigos;
        private Label lblVazio;
        private Label btnX;
        private TableLayoutPanel edit;
        private Font _fontLixeira;   // Segoe MDL2 (lixeira na grade + ícones na árvore)
        private Form _overlay;

        // Hover dos ícones da árvore (lixeira no tipo, "+" na linha Adicionar).
        private TreeNode _noHover;
        private bool _iconHover;
        private bool _acaoArvore;   // ação de ícone já enfileirada (dedup de duplo clique)
        private const int IconeArvore = 30;   // largura reservada p/ o ícone à direita do nó

        private bool _loading;
        private bool _temAlvo;
        private bool _ocupado;
        private bool _fechando;   // durante o fechamento não abre modais (evita reentrância)
        private int _geracao;

        private const string SemNome = "—";
        private const string Verificando = "verificando…";

        private bool _caf;
        private int _dia; private string _periodo;
        private int _semana; private string _tipoOriginal;
        private int _semanaContexto = 1;

        public GerenciarListasDialog(Session session, bool caf, int dia, string periodo, int semana, string tipo)
        {
            _session = session;
            _caf = caf;

            this.AutoScaleMode = AutoScaleMode.None;
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.BackColor = Theme.CardBg;
            this.Font = Theme.Body;
            this.DoubleBuffered = true;
            this.KeyPreview = true;
            this.Padding = new Padding(S(1), S(5), S(1), S(1));   // deixa a borda/acento aparecerem

            using (var g = this.CreateGraphics()) _sc = g.DpiX / 96f;
            _fontLixeira = new Font("Segoe MDL2 Assets", 10f * _sc);

            BuildUI();
            this.MinimumSize = new Size(S(900), S(560));
            this.FormClosed += new FormClosedEventHandler(OnFechado);

            _loading = true;
            cmbEstoque.SelectedIndex = caf ? 1 : 0;
            _loading = false;

            PopularArvore(AlvoInicial(caf, dia, periodo, semana, tipo));
        }

        private int S(int v) { return (int)Math.Round(v * _sc); }
        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000 /* CS_DROPSHADOW */; return cp; }
        }

        // Tamanho adaptativo (proporção do app) e centralizado no pai.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var dono = this.Owner;
            int w = S(1000), h = S(660);
            if (dono != null && dono.Visible && dono.Width > 0)
            {
                w = Clamp((int)(dono.Width * 0.90), S(940), S(1280));
                h = Clamp((int)(dono.Height * 0.90), S(580), S(860));
            }
            this.Size = new Size(w, h);
            if (dono != null)
                this.Location = new Point(dono.Left + (dono.Width - w) / 2, dono.Top + (dono.Height - h) / 2);
            else
            {
                var wa = Screen.FromControl(this).WorkingArea;
                this.Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            }
        }

        // ===== construção da UI (docked/ancorado — responsivo) =====

        private void BuildUI()
        {
            // ---- corpo (preenche; à esquerda a árvore, à direita o editor) ----
            var pnlBody = new Panel();
            pnlBody.Dock = DockStyle.Fill;
            pnlBody.BackColor = Theme.CardBg;
            pnlBody.Padding = new Padding(S(24), S(8), S(24), S(18));

            // editor (direita) — TableLayoutPanel: título, nome (colapsa), entrada, lista, excluir
            var pnlEditor = new Panel();
            pnlEditor.Dock = DockStyle.Fill;
            pnlEditor.BackColor = Theme.CardBg;
            pnlEditor.Padding = new Padding(S(24), 0, 0, 0);

            edit = new TableLayoutPanel();
            edit.Dock = DockStyle.Fill;
            edit.BackColor = Theme.CardBg;
            edit.ColumnCount = 1;
            edit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            edit.RowCount = 5;
            edit.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 0 título
            edit.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 1 nome micro (colapsa)
            edit.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 2 nome campo (colapsa)
            edit.RowStyles.Add(new RowStyle(SizeType.Absolute, S(66)));   // 3 entrada
            edit.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // 4 lista

            lblTituloLista = new Label();
            lblTituloLista.Font = Theme.H3; lblTituloLista.ForeColor = Theme.TextDark; lblTituloLista.BackColor = Theme.CardBg;
            lblTituloLista.AutoSize = true; lblTituloLista.Margin = new Padding(0, 0, 0, S(14));
            edit.Controls.Add(lblTituloLista, 0, 0);

            lblNomeMicro = Micro("NOME DO TIPO");
            lblNomeMicro.Margin = new Padding(0, 0, 0, S(4));
            edit.Controls.Add(lblNomeMicro, 0, 1);

            txtNome = new LineEdit();
            txtNome.Height = S(36);
            txtNome.Margin = new Padding(0, 0, 0, S(14));
            txtNome.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            txtNome.Width = S(400);
            txtNome.Casing = CharacterCasing.Upper;
            txtNome.Inner.Leave += new EventHandler(delegate { ComitarNome(); });
            edit.Controls.Add(txtNome, 0, 2);

            edit.Controls.Add(BuildEntrada(), 0, 3);

            gridCodigos = NovaGrid();
            gridCodigos.Dock = DockStyle.Fill;
            gridCodigos.Margin = new Padding(0);
            edit.Controls.Add(gridCodigos, 0, 4);

            pnlEditor.Controls.Add(edit);

            lblVazio = new Label();
            lblVazio.Text = "Selecione uma lista à esquerda para editar.";
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            lblVazio.AutoSize = true; lblVazio.Location = new Point(S(28), S(8));
            lblVazio.Visible = false;
            pnlEditor.Controls.Add(lblVazio);
            lblVazio.BringToFront();

            pnlBody.Controls.Add(pnlEditor);   // Fill primeiro

            // coluna esquerda — largura fixa: ESTOQUE (topo) + árvore (elástica)
            var pnlTree = new Panel();
            pnlTree.Dock = DockStyle.Left;
            pnlTree.Width = S(312);
            pnlTree.BackColor = Theme.CardBg;

            // seletor de estoque preenche toda a largura da coluna
            var pnlEstoque = new Panel();
            pnlEstoque.Dock = DockStyle.Top;
            pnlEstoque.Height = S(74);
            pnlEstoque.BackColor = Theme.CardBg;

            var micEstoque = Micro("ESTOQUE");
            micEstoque.Location = new Point(0, 0);
            pnlEstoque.Controls.Add(micEstoque);

            cmbEstoque = new StyledComboBox();
            cmbEstoque.Location = new Point(0, S(22));
            cmbEstoque.Width = S(312);   // coluna tem largura fixa: sem Anchor.Right (evita recorte da seta)
            cmbEstoque.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            cmbEstoque.Items.Add("Farmácia Central");
            cmbEstoque.Items.Add("CAF");
            cmbEstoque.SelectedIndexChanged += new EventHandler(OnEstoqueMudou);
            pnlEstoque.Controls.Add(cmbEstoque);

            var molduraTree = new FieldHost();
            molduraTree.Padding = new Padding(2);
            molduraTree.Dock = DockStyle.Fill;

            tree = new TreeView();
            tree.Dock = DockStyle.Fill;
            tree.BorderStyle = BorderStyle.None;
            tree.BackColor = Color.White; tree.ForeColor = Theme.TextDark; tree.Font = Theme.Body;
            tree.FullRowSelect = true; tree.HideSelection = false;
            tree.ShowLines = false; tree.ShowPlusMinus = true; tree.ShowRootLines = false;
            tree.ItemHeight = S(28); tree.Indent = S(22);
            tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tree.DrawNode += new DrawTreeNodeEventHandler(OnDrawNode);
            tree.BeforeSelect += new TreeViewCancelEventHandler(OnAntesSelecionar);
            tree.AfterSelect += new TreeViewEventHandler(OnDepoisSelecionar);
            tree.MouseMove += new MouseEventHandler(OnTreeMouseMove);
            tree.MouseDown += new MouseEventHandler(OnTreeMouseDown);
            tree.MouseLeave += new EventHandler(OnTreeMouseLeave);
            molduraTree.Controls.Add(tree);

            pnlTree.Controls.Add(molduraTree);   // Fill primeiro
            pnlTree.Controls.Add(pnlEstoque);    // depois o Top
            pnlBody.Controls.Add(pnlTree);       // Left por último

            this.Controls.Add(pnlBody);

            // ---- cabeçalho (faixa de título) ----
            var pnlHeader = new Panel();
            pnlHeader.Dock = DockStyle.Top;
            pnlHeader.Height = S(56);
            pnlHeader.BackColor = Theme.CardBg;

            var lblTitulo = new Label();
            lblTitulo.Text = "Gerenciar listas de contagem";
            lblTitulo.Font = Theme.H2; lblTitulo.ForeColor = Theme.TextDark; lblTitulo.BackColor = Theme.CardBg;
            lblTitulo.AutoSize = true; lblTitulo.Location = new Point(S(24), S(16));
            pnlHeader.Controls.Add(lblTitulo);

            btnX = new Label();
            btnX.Text = "✕";
            btnX.Font = new Font(Theme.Body.FontFamily, 11f, FontStyle.Regular);
            btnX.ForeColor = Theme.TextSoft; btnX.BackColor = Theme.CardBg;
            btnX.TextAlign = ContentAlignment.MiddleCenter;
            btnX.Size = new Size(S(44), S(32));
            btnX.Location = new Point(pnlHeader.Width - S(46), S(2));
            btnX.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnX.Cursor = Cursors.Hand;
            btnX.MouseEnter += new EventHandler(delegate { btnX.BackColor = Theme.Error; btnX.ForeColor = Color.White; });
            btnX.MouseLeave += new EventHandler(delegate { btnX.BackColor = Theme.CardBg; btnX.ForeColor = Theme.TextSoft; });
            btnX.Click += new EventHandler(OnFechar);
            pnlHeader.Controls.Add(btnX);

            this.Controls.Add(pnlHeader);   // Top por cima do Fill
        }

        // Linha de entrada: micro + contador (topo) e input + Adicionar + Restaurar (base).
        private Panel BuildEntrada()
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Theme.CardBg;
            p.Margin = new Padding(0);

            lblCodMicro = Micro("ADICIONAR CÓDIGO DO PRODUTO");
            lblCodMicro.Location = new Point(0, 0);
            p.Controls.Add(lblCodMicro);

            lblContagem = new Label();
            lblContagem.Font = Theme.Caption; lblContagem.ForeColor = Theme.TextSoft; lblContagem.BackColor = Theme.CardBg;
            lblContagem.AutoSize = false; lblContagem.Size = new Size(S(130), S(16));
            lblContagem.TextAlign = ContentAlignment.TopRight;
            lblContagem.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            p.Controls.Add(lblContagem);
            p.Resize += new EventHandler(delegate { lblContagem.Location = new Point(p.Width - S(130), 0); });

            var hostCod = new FieldHost();
            hostCod.Location = new Point(0, S(22));
            hostCod.Size = new Size(S(160), S(36));
            txtNovoCodigo = new TextBox();
            txtNovoCodigo.BorderStyle = BorderStyle.None; txtNovoCodigo.Font = Theme.Input;
            txtNovoCodigo.ForeColor = Theme.TextDark; txtNovoCodigo.BackColor = Color.White;
            txtNovoCodigo.Dock = DockStyle.Fill;
            txtNovoCodigo.KeyDown += new KeyEventHandler(OnCodigoKeyDown);
            hostCod.Controls.Add(txtNovoCodigo);
            p.Controls.Add(hostCod);

            btnAdicionar = new PrimaryButton();
            btnAdicionar.Text = "Adicionar";
            btnAdicionar.Size = new Size(S(120), S(36));
            btnAdicionar.Location = new Point(S(168), S(22));
            btnAdicionar.Click += new EventHandler(OnAdicionar);
            p.Controls.Add(btnAdicionar);

            btnRestaurar = new PrimaryButton();
            btnRestaurar.Ghost = true; btnRestaurar.Text = "Restaurar padrão";
            btnRestaurar.Size = new Size(S(150), S(36));
            btnRestaurar.Location = new Point(S(168) + S(128), S(22));
            btnRestaurar.Click += new EventHandler(OnRestaurar);
            p.Controls.Add(btnRestaurar);

            return p;
        }

        private Label Micro(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft; l.BackColor = Theme.CardBg;
            l.AutoSize = true;
            return l;
        }

        // ===== redimensionar (borda) e arrastar (título) do modal sem borda =====

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                base.WndProc(ref m);
                int lp = m.LParam.ToInt32();
                var pt = this.PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int g = S(6), w = this.ClientSize.Width, h = this.ClientSize.Height;
                bool le = pt.X <= g, ri = pt.X >= w - g, to = pt.Y <= g, bo = pt.Y >= h - g;
                if (to && le) m.Result = (IntPtr)13;
                else if (to && ri) m.Result = (IntPtr)14;
                else if (bo && le) m.Result = (IntPtr)16;
                else if (bo && ri) m.Result = (IntPtr)17;
                else if (le) m.Result = (IntPtr)10;
                else if (ri) m.Result = (IntPtr)11;
                else if (to) m.Result = (IntPtr)12;
                else if (bo) m.Result = (IntPtr)15;
                else if (pt.Y >= S(8) && pt.Y <= S(48) && pt.X < w - S(60)) m.Result = (IntPtr)2;   // HTCAPTION: arrasta pelo título
                return;
            }
            base.WndProc(ref m);
        }

        // ===== profundidade: overlay escurecido sobre o app, atrás do modal =====

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // A grade abre limpa: o WinForms re-seleciona a linha 0 na primeira exibição,
            // desfazendo qualquer limpeza feita antes do Show (na carga do construtor).
            gridCodigos.LimparSelecao();
            var dono = this.Owner;
            if (dono == null || !dono.Visible) return;
            _overlay = new OverlayEscurecido(dono);   // não-ativável + transparente a cliques (senão o app "trava" ao clicar fora)
            _overlay.Show();
            this.BringToFront();
            this.Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            _fechando = true;
            ComitarNome();   // salva um rename pendente em qualquer forma de fechar (X, Esc, etc.)
            // Remove o overlay ANTES de o modal ser destruído e devolve o foco ao app: senão o
            // Windows, ao reativar por cima de uma janela desabilitada (o overlay), minimiza a principal.
            if (_overlay != null) { try { _overlay.Hide(); _overlay.Close(); _overlay.Dispose(); } catch { } _overlay = null; }
            var dono = this.Owner;
            if (dono != null) { try { dono.Activate(); } catch { } }
        }

        private void OnFechado(object sender, FormClosedEventArgs e)
        {
            if (_overlay != null) { try { _overlay.Close(); _overlay.Dispose(); } catch { } _overlay = null; }
            if (_fontLixeira != null) { _fontLixeira.Dispose(); _fontLixeira = null; }
        }

        // ===== árvore =====

        private Alvo AlvoInicial(bool caf, int dia, string periodo, int semana, string tipo)
        {
            var a = new Alvo();
            a.Caf = caf;
            if (caf) { a.Semana = semana >= 1 && semana <= 5 ? semana : 1; a.Tipo = tipo; }
            else { a.Dia = dia >= 1 && dia <= 7 ? dia : 1; a.Periodo = periodo != null ? periodo : "Diurno"; }
            return a;
        }

        private void OnEstoqueMudou(object sender, EventArgs e)
        {
            if (_loading) return;
            ComitarNome();
            _caf = cmbEstoque.SelectedIndex == 1;
            PopularArvore(null);
        }

        private void PopularArvore(Alvo alvo)
        {
            _loading = true;
            _noHover = null; _iconHover = false;   // nós antigos serão descartados
            tree.BeginUpdate();
            tree.Nodes.Clear();

            TreeNode selecionar = null;
            if (_caf)
            {
                for (int s = 1; s <= 5; s++)
                {
                    var pai = new TreeNode("Semana " + s); pai.Tag = null;
                    tree.Nodes.Add(pai);
                    foreach (var t in ContagemCaf.TiposDaSemana(s))
                    {
                        var a = new Alvo(); a.Caf = true; a.Semana = s; a.Tipo = t.Nome;
                        var no = new TreeNode(t.Nome); no.Tag = a;
                        pai.Nodes.Add(no);
                        if (selecionar == null && alvo != null && alvo.Caf && alvo.Semana == s
                            && (alvo.Tipo == null || Igual(alvo.Tipo, t.Nome)))
                            selecionar = no;
                    }
                    var mk = new AddNovo(); mk.Semana = s;
                    var addNo = new TreeNode("Adicionar"); addNo.Tag = mk;
                    pai.Nodes.Add(addNo);
                    pai.Expand();
                }
            }
            else
            {
                string[] periodos = { "Diurno", "Noturno" };
                for (int d = 1; d <= 7; d++)
                {
                    var pai = new TreeNode(Dias[d - 1]); pai.Tag = null;
                    tree.Nodes.Add(pai);
                    foreach (var p in periodos)
                    {
                        var a = new Alvo(); a.Caf = false; a.Dia = d; a.Periodo = p;
                        var no = new TreeNode(p); no.Tag = a;
                        pai.Nodes.Add(no);
                        if (selecionar == null && alvo != null && !alvo.Caf && alvo.Dia == d && Igual(alvo.Periodo, p))
                            selecionar = no;
                    }
                    pai.Expand();
                }
            }

            if (selecionar == null) selecionar = PrimeiraFolha();
            tree.EndUpdate();

            if (selecionar != null)
            {
                tree.SelectedNode = selecionar;
                _loading = false;
                CarregarDetalhe(selecionar);
            }
            else
            {
                _loading = false;
                _temAlvo = false;
                MostrarEditor(false);
            }
        }

        private TreeNode PrimeiraFolha()
        {
            foreach (TreeNode pai in tree.Nodes)
                foreach (TreeNode f in pai.Nodes)
                    if (f.Tag is Alvo) return f;
            return null;
        }

        private void OnAntesSelecionar(object sender, TreeViewCancelEventArgs e)
        {
            // A linha "Adicionar tipo" é selecionável (CarregarDetalhe a trata como vazio):
            // NÃO cancelamos aqui, senão a seta do teclado ficaria presa nela.
            if (_loading) return;
            ComitarNome();
        }

        private void OnDepoisSelecionar(object sender, TreeViewEventArgs e)
        {
            if (_loading) return;
            CarregarDetalhe(e.Node);
        }

        private void OnDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            int right = tree.ClientSize.Width;
            var full = new Rectangle(e.Bounds.Left, e.Bounds.Top, right - e.Bounds.Left, e.Bounds.Height);

            // Linha "Adicionar": "+" num círculo + rótulo; a linha inteira é o botão.
            var add = e.Node.Tag as AddNovo;
            if (add != null)
            {
                bool hv = _noHover == e.Node;
                using (var br = new SolidBrush(hv ? Theme.HoverBg : Color.White))
                    e.Graphics.FillRectangle(br, full);
                DesenharCirculoMais(e.Graphics, e.Bounds, hv);
                int d = S(16);
                int tx = e.Bounds.Left + S(2) + d + S(8);
                var rcTxt = new Rectangle(tx, e.Bounds.Top, right - tx - S(6), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, "Adicionar", Theme.Body, rcTxt, Theme.Primary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                return;
            }

            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            bool pai = !(e.Node.Tag is Alvo);
            // seleção discreta (SelBg); hover = fundo suave que acompanha o mouse (só folhas entram em _noHover).
            Color bg = sel ? Theme.SelBg : (_noHover == e.Node ? Theme.HoverBg : Color.White);
            using (var br = new SolidBrush(bg))
                e.Graphics.FillRectangle(br, full);

            // Lixeira só nos tipos (folhas) do CAF; a semana (pai) não tem ícone. Espaço
            // sempre reservado (texto não desloca), mas a lixeira só aparece no hover da linha.
            bool temTrash = _caf && !pai;
            int reserva = temTrash ? S(IconeArvore) : S(6);
            var rc = new Rectangle(e.Bounds.Left, e.Bounds.Top, right - e.Bounds.Left - reserva, e.Bounds.Height);
            var font = pai ? Theme.BodyBold : Theme.Body;
            var cor = pai ? Theme.TextDark : (sel ? Theme.Primary : Theme.TextSoft);
            TextRenderer.DrawText(e.Graphics, e.Node.Text, font, rc, cor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (temTrash && _fontLixeira != null && _noHover == e.Node)
            {
                var ico = new Rectangle(right - S(IconeArvore), e.Bounds.Top, S(IconeArvore), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, ((char)0xE74D).ToString(), _fontLixeira, ico, Theme.Error,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        // Desenha um "+" dentro de um círculo (botão de adicionar), à esquerda da linha.
        private void DesenharCirculoMais(Graphics g, Rectangle bounds, bool forte)
        {
            int d = S(16);
            int x0 = bounds.Left + S(2);
            int y0 = bounds.Top + (bounds.Height - d) / 2;
            var antes = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color cor = forte ? Theme.Primary : Color.FromArgb(190, Theme.Primary);
            using (var pen = new Pen(cor, 1.5f))
            {
                g.DrawEllipse(pen, x0, y0, d, d);
                int cx = x0 + d / 2, cy = y0 + d / 2, arm = S(4);
                g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
                g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
            }
            g.SmoothingMode = antes;
        }

        // ===== ícones da árvore (hover + clique) =====

        private bool NaZonaIcone(int x) { return x >= tree.ClientSize.Width - S(IconeArvore); }

        private Rectangle LinhaDoNo(TreeNode n)
        {
            return new Rectangle(0, n.Bounds.Top, tree.ClientSize.Width, n.Bounds.Height);
        }

        private void OnTreeMouseMove(object sender, MouseEventArgs e)
        {
            if (!_caf) { LimparHover(); return; }
            TreeNode no = tree.GetNodeAt(S(4), e.Y);
            // A linha "sob o mouse" (tipo ou "Adicionar") mostra sua lixeira/realce.
            TreeNode linha = (no != null && ((no.Tag is Alvo) || (no.Tag is AddNovo))) ? no : null;
            // Zona clicável (cursor de mão): "Adicionar" a linha toda; tipo só na lixeira.
            bool clic = linha != null && ((linha.Tag is AddNovo) || NaZonaIcone(e.X));
            if (linha == _noHover && clic == _iconHover) return;
            TreeNode antigo = _noHover;
            _noHover = linha; _iconHover = clic;
            tree.Cursor = clic ? Cursors.Hand : Cursors.Default;
            if (antigo != null) tree.Invalidate(LinhaDoNo(antigo));
            if (_noHover != null) tree.Invalidate(LinhaDoNo(_noHover));
        }

        private void OnTreeMouseDown(object sender, MouseEventArgs e)
        {
            if (!_caf || e.Button != MouseButtons.Left || _acaoArvore) return;   // _acaoArvore: ignora o 2º clique do duplo
            TreeNode no = tree.GetNodeAt(S(4), e.Y);
            if (no == null) return;

            // Ações que reconstroem a árvore/abrem modal: adiada p/ depois do processamento
            // do clique (senão o TreeView reaplicaria a seleção sobre a árvore recriada).
            var add = no.Tag as AddNovo;
            if (add != null)
            {
                int semana = add.Semana;
                _acaoArvore = true;
                this.BeginInvoke((MethodInvoker)delegate { _acaoArvore = false; AddTipoNaSemana(semana); });
                return;
            }
            // só exclui se a lixeira estiver de fato visível (linha sob o mouse): evita
            // excluir por engano após rolar a roda sem mover o cursor (hover desatualizado).
            var alvo = no.Tag as Alvo;
            if (alvo != null && no == _noHover && NaZonaIcone(e.X))
            {
                int semana = alvo.Semana; string tipo = alvo.Tipo;
                _acaoArvore = true;
                this.BeginInvoke((MethodInvoker)delegate { _acaoArvore = false; ExcluirTipoAlvo(semana, tipo); });
            }
        }

        private void OnTreeMouseLeave(object sender, EventArgs e) { LimparHover(); }

        private void LimparHover()
        {
            if (_noHover == null && !_iconHover) { tree.Cursor = Cursors.Default; return; }
            TreeNode antigo = _noHover;
            _noHover = null; _iconHover = false;
            tree.Cursor = Cursors.Default;
            if (antigo != null) tree.Invalidate(LinhaDoNo(antigo));
        }

        // ===== editor =====

        private void CarregarDetalhe(TreeNode node)
        {
            _geracao++;
            var alvo = node == null ? null : node.Tag as Alvo;
            if (alvo == null)
            {
                _temAlvo = false;
                gridCodigos.Rows.Clear();
                if (node != null && node.Tag == null) _semanaContexto = SemanaDoNo(node);
                MostrarEditor(false);
                return;
            }

            _loading = true;
            _temAlvo = true;
            _caf = alvo.Caf;
            MostrarEditor(true);

            if (alvo.Caf)
            {
                _semana = alvo.Semana; _tipoOriginal = alvo.Tipo; _semanaContexto = alvo.Semana;
                lblTituloLista.Text = "CAF · Semana " + alvo.Semana;
                lblNomeMicro.Visible = true; txtNome.Visible = true;
                txtNome.Text = alvo.Tipo;
                CarregarCodigos(CodigosDoTipo(alvo.Semana, alvo.Tipo));
                btnRestaurar.Visible = ContagemCaf.PadraoDoTipo(alvo.Semana, alvo.Tipo) != null;
            }
            else
            {
                _dia = alvo.Dia; _periodo = alvo.Periodo;
                lblTituloLista.Text = "Farmácia Central · " + Dias[alvo.Dia - 1] + " / " + alvo.Periodo;
                lblNomeMicro.Visible = false; txtNome.Visible = false;
                CarregarCodigos(ContagemFarmacia.Codigos(alvo.Dia, alvo.Periodo));
                btnRestaurar.Visible = true;
            }

            _loading = false;
            AtualizarContagem();
            ResolverPendentesAsync();
        }

        private void MostrarEditor(bool mostrar)
        {
            edit.Visible = mostrar;
            lblVazio.Visible = !mostrar;
        }

        private GridPro NovaGrid()
        {
            // Grade padrão do app (double buffer, cabeçalho claro, seleção discreta,
            // hover estável — inclusive recalculado ao rolar — e sem foco pontilhado);
            // aqui só o que é deste diálogo: colunas e a lixeira pintada por conta própria.
            var g = new GridPro();
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridZebra;

            var cCod = new DataGridViewTextBoxColumn();
            cCod.Name = "codigo"; cCod.HeaderText = "Código"; cCod.Width = S(96);
            cCod.SortMode = DataGridViewColumnSortMode.NotSortable;
            // Número curto centralizado, cabeçalho junto — como nas grades das telas.
            cCod.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            cCod.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            g.Columns.Add(cCod);

            var cNome = new DataGridViewTextBoxColumn();
            cNome.Name = "produto"; cNome.HeaderText = "Produto";
            cNome.SortMode = DataGridViewColumnSortMode.NotSortable;
            cNome.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            g.Columns.Add(cNome);

            var cRem = new DataGridViewTextBoxColumn();
            cRem.Name = "remover"; cRem.HeaderText = ""; cRem.Width = S(46);
            cRem.SortMode = DataGridViewColumnSortMode.NotSortable;
            g.Columns.Add(cRem);

            g.CellClick += new DataGridViewCellEventHandler(OnGridCellClick);
            g.CellPainting += new DataGridViewCellPaintingEventHandler(OnGridCellPainting);
            g.MouseMove += new MouseEventHandler(OnGridMouseMove);
            g.MouseLeave += new EventHandler(OnGridMouseLeave);
            g.KeyDown += new KeyEventHandler(OnGridKeyDown);
            return g;
        }

        private void OnGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (gridCodigos.Columns[e.ColumnIndex].Name != "remover") return;
            // Fundo: como este e.Handled tira a célula do pintor padrão (o GridPro o
            // respeita), o hover da linha é reproduzido aqui — senão a faixa sob o
            // mouse ficaria "furada" na coluna da lixeira.
            bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
            if (!sel && e.RowIndex == gridCodigos.LinhaHover)
            {
                using (var hb = new SolidBrush(Theme.HoverBg))
                    e.Graphics.FillRectangle(hb, e.CellBounds);
            }
            else e.PaintBackground(e.CellBounds, sel);
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Border);   // mantém a linha 1px
            // lixeira só na linha sob o mouse (o GridPro recalcula o hover até no scroll)
            if (e.RowIndex == gridCodigos.LinhaHover && _fontLixeira != null)
                TextRenderer.DrawText(e.Graphics, ((char)0xE74D).ToString(), _fontLixeira, e.CellBounds, Theme.Error,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            e.Handled = true;
        }

        private void OnGridMouseMove(object sender, MouseEventArgs e)
        {
            // o fundo de hover é do GridPro; aqui só o cursor de mão sobre a lixeira
            var hit = gridCodigos.HitTest(e.X, e.Y);
            bool sobreRemover = hit.RowIndex >= 0 && hit.ColumnIndex >= 0
                && gridCodigos.Columns[hit.ColumnIndex].Name == "remover";
            gridCodigos.Cursor = sobreRemover ? Cursors.Hand : Cursors.Default;
        }

        private void OnGridMouseLeave(object sender, EventArgs e)
        {
            gridCodigos.Cursor = Cursors.Default;
        }

        private void CarregarCodigos(List<int> codigos)
        {
            gridCodigos.Rows.Clear();
            if (codigos != null)
                foreach (int cd in codigos) AdicionarLinha(cd);
            gridCodigos.LimparSelecao();
        }

        private void AdicionarLinha(int cd)
        {
            int r = gridCodigos.Rows.Add(cd.ToString(), "", "");
            SetNomeLinha(gridCodigos.Rows[r], ProdutoNomes.Get(cd));
        }

        private void SetNomeLinha(DataGridViewRow row, string nome)
        {
            bool tem = nome != null && nome.Length > 0;
            row.Cells["produto"].Value = tem ? nome : SemNome;
            row.Cells["produto"].Style.ForeColor = tem ? Theme.TextDark : Theme.TextSoft;
        }

        private void SetNomeLinhaPorCodigo(int cd, string nome)
        {
            foreach (DataGridViewRow row in gridCodigos.Rows)
            {
                if (row.IsNewRow) continue;
                int c;
                if (int.TryParse(Convert.ToString(row.Cells["codigo"].Value), out c) && c == cd)
                { SetNomeLinha(row, nome); return; }
            }
        }

        private List<int> CodigosAtuais()
        {
            var lista = new List<int>();
            foreach (DataGridViewRow row in gridCodigos.Rows)
            {
                if (row.IsNewRow) continue;
                int cd;
                if (int.TryParse(Convert.ToString(row.Cells["codigo"].Value), out cd) && cd > 0) lista.Add(cd);
            }
            return lista;
        }

        private List<int> CodigosSemNome()
        {
            var lista = new List<int>();
            foreach (DataGridViewRow row in gridCodigos.Rows)
            {
                if (row.IsNewRow) continue;
                if (Convert.ToString(row.Cells["produto"].Value) != SemNome) continue;
                int cd;
                if (int.TryParse(Convert.ToString(row.Cells["codigo"].Value), out cd) && cd > 0) lista.Add(cd);
            }
            return lista;
        }

        private void AtualizarContagem()
        {
            int n = CodigosAtuais().Count;
            lblContagem.Text = n + (n == 1 ? " código" : " códigos");
        }

        private void SetOcupado(bool ocupado)
        {
            _ocupado = ocupado;
            txtNovoCodigo.Enabled = !ocupado;
            btnAdicionar.Enabled = !ocupado;
            lblCodMicro.Text = ocupado ? "VERIFICANDO NO MV…" : "ADICIONAR CÓDIGO DO PRODUTO";
        }

        // ===== auto-save =====

        private void PersistirCodigos()
        {
            if (!_temAlvo || _session == null) return;   // preview não persiste
            var codigos = CodigosAtuais();
            bool ok;
            if (_caf)
                ok = ContagemCaf.Salvar(_semana, _tipoOriginal, _tipoOriginal, codigos);
            else
                ok = MesmaLista(codigos, ContagemFarmacia.Padrao(_dia, _periodo))
                    ? ContagemFarmacia.RestaurarPadrao(_dia, _periodo)
                    : ContagemFarmacia.Salvar(_dia, _periodo, codigos);
            if (!ok)
                StyledDialog.Aviso(this, "Não foi possível salvar",
                    "O arquivo de listas não pôde ser gravado (verifique se não está aberto/bloqueado).", "Entendi");
        }

        private void ComitarNome()
        {
            if (!_temAlvo || !_caf || txtNome == null || _session == null) return;   // preview não persiste
            string nome = txtNome.Text.Trim();
            if (Igual(nome, _tipoOriginal)) return;   // sem mudança (ignora caixa)
            if (nome.Length == 0 || ContagemCaf.NomeEmUso(_semana, nome, _tipoOriginal))
            {
                _loading = true; txtNome.Text = _tipoOriginal; _loading = false;
                return;
            }
            if (!ContagemCaf.Salvar(_semana, _tipoOriginal, nome, CodigosAtuais()))
            {
                if (_fechando) { Logger.Log("ComitarNome: falha ao salvar rename durante o fechamento."); return; }
                StyledDialog.Aviso(this, "Não foi possível salvar", "O arquivo dados\\contagem-caf.txt não pôde ser gravado.", "Entendi");
                _loading = true; txtNome.Text = _tipoOriginal; _loading = false;
                return;
            }
            _tipoOriginal = nome;
            TreeNode no = tree.SelectedNode;
            if (no != null) { no.Text = nome; var a = no.Tag as Alvo; if (a != null) a.Tipo = nome; }
        }

        // ===== adicionar =====

        private void OnCodigoKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AdicionarDoTexto(); }
        }

        private void OnAdicionar(object sender, EventArgs e) { AdicionarDoTexto(); }

        private async void AdicionarDoTexto()
        {
            if (_ocupado) return;
            int geracao = _geracao;
            int ign;
            var codigos = ContagemCodigos.Parse(txtNovoCodigo.Text, out ign);
            if (codigos.Count == 0) { txtNovoCodigo.Focus(); txtNovoCodigo.SelectAll(); return; }

            var existentes = new HashSet<int>(CodigosAtuais());
            var novos = new List<int>();
            foreach (int cd in codigos) if (existentes.Add(cd)) novos.Add(cd);
            if (novos.Count == 0) { txtNovoCodigo.Focus(); txtNovoCodigo.SelectAll(); return; }
            txtNovoCodigo.Text = "";

            if (_session == null)
            {
                foreach (int cd in novos) AdicionarLinha(cd);
                AtualizarContagem(); RolarFim(); txtNovoCodigo.Focus();
                return;
            }

            int estoque = _caf ? ContagemCaf.Estoque : ContagemFarmacia.Estoque;
            System.Collections.Generic.Dictionary<int, string> achados;
            SetOcupado(true);
            bool tenho = false;
            try
            {
                // Portão único do MV (o monitor de solicitações varre em segundo plano, e o
                // laço deste diálogo modal continua bombeando o timer dele).
                tenho = await MvGate.EntrarAsync("listas-resolver-nomes", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                {
                    SetOcupado(false);
                    StyledDialog.Aviso(this, "Servidor ocupado",
                        "O MV Soul está respondendo outra consulta. Tente adicionar de novo em instantes.", "Entendi");
                    return;
                }
                achados = await MorphisClient.ResolverNomesAsync(_session, estoque, novos, null);
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) { SetOcupado(false);
                    StyledDialog.Aviso(this, "Sessão expirada", "Feche e faça login novamente para adicionar produtos.", "Entendi"); }
                return;
            }
            catch (Exception ex)
            {
                Logger.Log("Validar código: " + ex.Message);
                if (!IsDisposed && !Disposing) { SetOcupado(false);
                    StyledDialog.Aviso(this, "Não foi possível verificar", "Erro ao consultar o MV Soul: " + ex.Message, "Entendi"); }
                return;
            }
            finally { if (tenho) MvGate.Sair(); }
            if (IsDisposed || Disposing) return;
            SetOcupado(false);
            if (geracao != _geracao) { ResolverPendentesAsync(); return; }

            var invalidos = new List<int>();
            bool mudou = false;
            foreach (int cd in novos)
            {
                if (!achados.ContainsKey(cd)) { invalidos.Add(cd); continue; }
                AdicionarLinha(cd);
                mudou = true;
            }
            if (mudou) { AtualizarContagem(); RolarFim(); PersistirCodigos(); }
            txtNovoCodigo.Focus();

            if (invalidos.Count > 0)
                StyledDialog.Aviso(this, "Código não encontrado",
                    (invalidos.Count == 1 ? "O código " : "Os códigos ") + string.Join(", ", invalidos.ConvertAll(Convert.ToString)) +
                    (invalidos.Count == 1 ? " não existe" : " não existem") + " neste estoque do MV e não " +
                    (invalidos.Count == 1 ? "foi adicionado" : "foram adicionados") + ".", "Entendi");
        }

        private void RolarFim()
        {
            if (gridCodigos.Rows.Count > 0)
                gridCodigos.FirstDisplayedScrollingRowIndex = gridCodigos.Rows.Count - 1;
        }

        private async void ResolverPendentesAsync()
        {
            if (_session == null || _ocupado || !_temAlvo) return;
            var pendentes = CodigosSemNome();
            if (pendentes.Count == 0) return;
            int geracao = _geracao;
            int estoque = _caf ? ContagemCaf.Estoque : ContagemFarmacia.Estoque;

            foreach (int cd in pendentes) SetNomeLinhaVerificando(cd);
            SetOcupado(true);
            Action<int, string> cb = delegate(int cd, string nome)
            {
                if (IsDisposed || Disposing || geracao != _geracao) return;
                SetNomeLinhaPorCodigo(cd, nome);
            };
            bool tenho = false;
            try
            {
                tenho = await MvGate.EntrarAsync("listas-repuxar-nomes", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho) { Logger.Log("Repuxar nomes: servidor ocupado, adiado."); return; }
                await MorphisClient.ResolverNomesAsync(_session, estoque, pendentes, cb);
            }
            catch (Exception ex) { Logger.Log("Repuxar nomes: " + ex.Message); }
            finally
            {
                if (tenho) MvGate.Sair();
                if (!IsDisposed && !Disposing) SetOcupado(false);
            }
            if (IsDisposed || Disposing) return;
            if (geracao != _geracao) { ResolverPendentesAsync(); return; }
            AtualizarNomesDoCache();
        }

        private void SetNomeLinhaVerificando(int cd)
        {
            foreach (DataGridViewRow row in gridCodigos.Rows)
            {
                if (row.IsNewRow) continue;
                int c;
                if (int.TryParse(Convert.ToString(row.Cells["codigo"].Value), out c) && c == cd)
                { row.Cells["produto"].Value = Verificando; row.Cells["produto"].Style.ForeColor = Theme.TextSoft; return; }
            }
        }

        private void AtualizarNomesDoCache()
        {
            foreach (DataGridViewRow row in gridCodigos.Rows)
            {
                if (row.IsNewRow) continue;
                string p = Convert.ToString(row.Cells["produto"].Value);
                if (p != Verificando && p != SemNome) continue;
                int cd;
                if (int.TryParse(Convert.ToString(row.Cells["codigo"].Value), out cd))
                    SetNomeLinha(row, ProdutoNomes.Get(cd));
            }
        }

        // ===== remover =====

        private void OnGridCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (gridCodigos.Columns[e.ColumnIndex].Name == "remover") RemoverLinha(e.RowIndex);
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && gridCodigos.CurrentRow != null)
            {
                e.SuppressKeyPress = true;
                // O Delete remove e SALVA na hora (auto-save, sem desfazer). Diferente da
                // lixeira, o teclado não exige mirar em nada — então confirma antes.
                var row = gridCodigos.CurrentRow;
                string cod = Convert.ToString(row.Cells["codigo"].Value);
                string nome = Convert.ToString(row.Cells["produto"].Value);
                string quem = (nome != null && nome.Length > 0 && nome != SemNome && nome != Verificando)
                    ? cod + " — " + nome : cod;
                bool sim = StyledDialog.Confirmar(this, "Remover código",
                    "Remover " + quem + " desta lista? A alteração é salva na hora.",
                    "Remover", "Cancelar");
                if (!sim) return;
                if (row.Index >= 0) RemoverLinha(row.Index);
            }
        }

        private void RemoverLinha(int index)
        {
            if (index < 0 || index >= gridCodigos.Rows.Count) return;
            if (gridCodigos.Rows[index].IsNewRow) return;
            gridCodigos.Rows.RemoveAt(index);
            AtualizarContagem();
            PersistirCodigos();
        }

        // ===== outras ações =====

        private void OnRestaurar(object sender, EventArgs e)
        {
            if (!_temAlvo) return;
            bool sim = StyledDialog.Confirmar(this, "Restaurar padrão",
                "Recarregar a lista original da escala? Isso substitui a lista atual e é salvo na hora.",
                "Restaurar", "Cancelar");
            if (!sim) return;
            List<int> padrao = _caf ? ContagemCaf.PadraoDoTipo(_semana, _tipoOriginal) : ContagemFarmacia.Padrao(_dia, _periodo);
            _loading = true;
            CarregarCodigos(padrao != null ? padrao : new List<int>());
            _loading = false;
            AtualizarContagem();
            PersistirCodigos();
            _geracao++;
            ResolverPendentesAsync();
        }

        // Adiciona um tipo novo à semana (ícone "+" na árvore) e já o seleciona p/ renomear.
        private void AddTipoNaSemana(int semana)
        {
            if (!_caf || _session == null) return;   // preview não persiste estrutura
            ComitarNome();
            if (semana < 1 || semana > 5) semana = 1;
            string nome = NomeNovoTipo(semana);
            if (!ContagemCaf.Salvar(semana, null, nome, new List<int>()))
            {
                StyledDialog.Aviso(this, "Não foi possível criar", "O arquivo dados\\contagem-caf.txt não pôde ser gravado.", "Entendi");
                return;
            }
            var alvo = new Alvo(); alvo.Caf = true; alvo.Semana = semana; alvo.Tipo = nome;
            PopularArvore(alvo);
            txtNome.FocusInput();
            txtNome.Inner.SelectAll();
        }

        private string NomeNovoTipo(int semana)
        {
            string bas = "NOVO TIPO";
            if (!ContagemCaf.NomeEmUso(semana, bas, null)) return bas;
            for (int i = 2; i < 100; i++)
            {
                string tent = bas + " " + i;
                if (!ContagemCaf.NomeEmUso(semana, tent, null)) return tent;
            }
            return bas + " " + semana;
        }

        // Exclui um tipo (ícone lixeira na árvore) após confirmação.
        private void ExcluirTipoAlvo(int semana, string tipo)
        {
            if (!_caf || tipo == null || _session == null) return;   // preview não persiste estrutura
            bool sim = StyledDialog.Confirmar(this, "Excluir tipo",
                "Excluir \"" + tipo + "\" da Semana " + semana + "? Os códigos desta lista serão perdidos.",
                "Excluir", "Cancelar");
            if (!sim) return;
            if (!ContagemCaf.Excluir(semana, tipo))
            {
                StyledDialog.Aviso(this, "Não foi possível excluir", "O arquivo dados\\contagem-caf.txt não pôde ser gravado.", "Entendi");
                return;
            }
            var alvo = new Alvo(); alvo.Caf = true; alvo.Semana = semana; alvo.Tipo = null;
            PopularArvore(alvo);
        }

        private void OnFechar(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.OK;   // ComitarNome roda no OnFormClosing
            this.Close();
        }

        // ===== helpers =====

        private int SemanaDoNo(TreeNode node)
        {
            if (node != null && node.Text.StartsWith("Semana "))
            {
                int v;
                if (int.TryParse(node.Text.Substring(7).Trim(), out v)) return v;
            }
            return _semanaContexto;
        }

        private List<int> CodigosDoTipo(int semana, string nome)
        {
            foreach (var t in ContagemCaf.TiposDaSemana(semana))
                if (Igual(t.Nome, nome)) return t.Codigos;
            return new List<int>();
        }

        private static bool Igual(string a, string b)
        {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static bool MesmaLista(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            var set = new HashSet<int>(b);
            foreach (int x in a) if (!set.Contains(x)) return false;
            return true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 2; DwmSetWindowAttribute(this.Handle, 33, ref v, 4); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            using (var br = new SolidBrush(Theme.Primary))
                g.FillRectangle(br, 0, 0, this.Width, S(4));
            using (var pen = new Pen(Theme.Border))
                g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
        }
    }
}
