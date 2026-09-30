using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // "Cadastro de embalagens" (Pedido CAF): uma grade única estilo planilha com TODOS os
    // produtos listados. O usuário preenche/altera a quantidade na própria grade; a última
    // linha (vazia) serve para ADICIONAR um código novo. Tudo SALVA automaticamente a cada
    // célula confirmada; não há botão de salvar nem de adicionar. Lixeira no hover remove.
    // Nome do produto vem do cadastro/cache local e, quando há sessão, é resolvido ao vivo.
    public class CaixasDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // EM_SETCUEBANNER do campo de busca (placeholder nativo, some ao digitar).
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private readonly float _sc;
        private readonly Session _session;

        private GridPro grid;
        private TextBox txtBusca;
        private Label btnX;
        private Font _fontLixeira;
        private Form _overlay;

        private int _linhaCorrente = -1;   // linha da célula corrente (repinta ao mudar)
        private bool _carregando;   // suprime handlers durante o preenchimento da grade

        // Zebra por linha VISÍVEL: com a busca escondendo linhas, alternar pelo índice
        // físico deixava duas vizinhas da mesma cor. Recalculada a cada filtro/refresh.
        private readonly Dictionary<DataGridViewRow, bool> _faixa = new Dictionary<DataGridViewRow, bool>();

        private const string SemNome = "—";
        private const string Verificando = "verificando…";

        // Realce ÚNICO da linha ativa (sob o mouse, em edição OU corrente) — suave. O campo
        // de edição e o painel dele são pintados com esta MESMA cor, então editar não cria
        // emenda branca na linha.
        private static readonly Color Realce = Color.FromArgb(236, 243, 250);

        public CaixasDialog(Session session)
        {
            _session = session;

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
            this.MinimumSize = new Size(S(560), S(420));
            this.FormClosed += new FormClosedEventHandler(OnFechado);
            this.KeyDown += new KeyEventHandler(delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Escape && !grid.IsCurrentCellInEditMode)
                { ke.SuppressKeyPress = true; Close(); }
            });

            CarregarGrade();
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
            int w = S(720), h = S(580);
            if (dono != null && dono.Visible && dono.Width > 0)
            {
                w = Clamp((int)(dono.Width * 0.58), S(640), S(900));
                h = Clamp((int)(dono.Height * 0.85), S(480), S(820));
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

        // ===== construção da UI =====

        private void BuildUI()
        {
            var pnlBody = new Panel();
            pnlBody.Dock = DockStyle.Fill;
            pnlBody.BackColor = Theme.CardBg;
            pnlBody.Padding = new Padding(S(24), S(4), S(24), S(18));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowCount = 3;
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // explicação (quebra conforme a largura)
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // busca
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // grade

            var lblExpl = new Label();
            lblExpl.Text = "Adicione as quantidades que vêm na embalagem fechada do produto. O sistema " +
                "usará essas quantidades para priorizar pedidos com embalagens fechadas. Para incluir um " +
                "produto novo, digite o código na última linha da lista.";
            lblExpl.Font = Theme.Body; lblExpl.ForeColor = Theme.TextSoft; lblExpl.BackColor = Theme.CardBg;
            lblExpl.AutoSize = true;
            lblExpl.Margin = new Padding(0, 0, 0, S(12));
            tl.Controls.Add(lblExpl, 0, 0);
            // responsivo: o texto quebra acompanhando a largura do modal
            tl.Resize += new EventHandler(delegate { lblExpl.MaximumSize = new Size(tl.ClientSize.Width, 0); });

            // Busca: 341 produtos sem filtro obrigavam a rolar a lista inteira para achar
            // um item. Mesmo desenho das telas (FieldHost + lupa), filtrando ao digitar.
            var hostBusca = new FieldHost();
            hostBusca.Anchor = AnchorStyles.Left;
            hostBusca.Size = new Size(S(340), S(34));
            hostBusca.Margin = new Padding(0, 0, 0, S(12));
            hostBusca.Padding = new Padding(S(10), S(7), S(8), S(6));

            var lupa = new Label();
            lupa.Text = "";                  // glifo "Search" (Segoe MDL2 Assets)
            lupa.Font = Theme.Icon; lupa.ForeColor = Theme.TextSoft; lupa.BackColor = Color.White;
            lupa.Dock = DockStyle.Left; lupa.Width = S(24);
            lupa.TextAlign = ContentAlignment.MiddleLeft;

            txtBusca = new TextBox();
            txtBusca.BorderStyle = BorderStyle.None;
            txtBusca.Font = Theme.Body; txtBusca.ForeColor = Theme.TextDark; txtBusca.BackColor = Color.White;
            txtBusca.Dock = DockStyle.Fill;
            var espera = new System.Windows.Forms.Timer();
            espera.Interval = 180;
            espera.Tick += new EventHandler(delegate { espera.Stop(); AplicarFiltro(); });
            txtBusca.TextChanged += new EventHandler(delegate { espera.Stop(); espera.Start(); });
            this.Disposed += new EventHandler(delegate { espera.Dispose(); });
            txtBusca.KeyDown += new KeyEventHandler(delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Escape && txtBusca.Text.Length > 0)
                { txtBusca.Text = ""; ke.SuppressKeyPress = true; }
            });
            txtBusca.HandleCreated += new EventHandler(delegate
            {
                SendMessage(txtBusca.Handle, 0x1501, (IntPtr)1, "Buscar produto ou código");
            });

            hostBusca.Controls.Add(txtBusca);
            hostBusca.Controls.Add(lupa);
            tl.Controls.Add(hostBusca, 0, 1);

            grid = NovaGrid();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            tl.Controls.Add(grid, 0, 2);

            pnlBody.Controls.Add(tl);
            this.Controls.Add(pnlBody);

            // ---- cabeçalho (faixa de título) ----
            var pnlHeader = new Panel();
            pnlHeader.Dock = DockStyle.Top;
            pnlHeader.Height = S(56);
            pnlHeader.BackColor = Theme.CardBg;

            var lblTitulo = new Label();
            lblTitulo.Text = "Cadastro de embalagens";
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
            btnX.Click += new EventHandler(delegate { Close(); });
            pnlHeader.Controls.Add(btnX);

            this.Controls.Add(pnlHeader);   // Top por cima do Fill
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
                else if (pt.Y >= S(8) && pt.Y <= S(48) && pt.X < w - S(60)) m.Result = (IntPtr)2;   // HTCAPTION
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 2; DwmSetWindowAttribute(this.Handle, 33, ref v, 4); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var br = new SolidBrush(Theme.Primary))
                e.Graphics.FillRectangle(br, 0, 0, this.Width, S(4));
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
        }

        // ===== overlay escurecido =====

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // sem seleção/foco inicial: a grade abre "limpa" (nada de linha azul parada)
            try { grid.LimparSelecao(); } catch { }

            var dono = this.Owner;
            if (dono == null || !dono.Visible) return;
            _overlay = new OverlayEscurecido(dono);
            _overlay.Show();
            this.BringToFront();
            this.Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            if (grid.IsCurrentCellInEditMode) { try { grid.EndEdit(); } catch { } }   // comita edição pendente
            // remove o overlay ANTES da destruição e devolve o foco (senão o Windows minimiza o app)
            if (_overlay != null) { try { _overlay.Hide(); _overlay.Close(); _overlay.Dispose(); } catch { } _overlay = null; }
            var dono = this.Owner;
            if (dono != null) { try { dono.Activate(); } catch { } }
        }

        private void OnFechado(object sender, FormClosedEventArgs e)
        {
            if (_overlay != null) { try { _overlay.Close(); _overlay.Dispose(); } catch { } _overlay = null; }
            if (_fontLixeira != null) { _fontLixeira.Dispose(); _fontLixeira = null; }
        }

        // ===== grade =====

        private GridPro NovaGrid()
        {
            var g = new GridPro();
            // A pintura das células é 100% própria (OnGridCellPainting); o GridPro entra pelo
            // resto: double buffer, cabeçalho flat claro e o hover rastreado (LinhaHover, que
            // se recalcula no scroll e solta ao sair das linhas). A pintura de hover dele fica
            // desligada — quem colore é o painter daqui.
            g.HoverPintado = false;
            // A limpeza inicial é da própria tela (OnShown abre a grade "limpa").
            g.SemSelecaoInicial = false;
            g.AllowUserToAddRows = true;   // a ÚLTIMA linha (vazia) é o "adicionar produto"
            g.AllowUserToResizeColumns = false;
            g.ReadOnly = false;            // a grade edita: código (linha nova) e quantidade
            g.EditMode = DataGridViewEditMode.EditOnEnter;   // 1 clique já libera a edição da célula
            g.AllowUserToOrderColumns = false;
            // Linha um pouco mais alta que o padrão: a grade é uma planilha de EDIÇÃO, e
            // o alvo de clique/toque agradece o respiro.
            g.RowTemplate.Height = S(40);
            // A padding participa do desenho custom e do encaixe do campo de edição.
            g.DefaultCellStyle.Padding = new Padding(S(8), 0, S(4), 0);
            // A grade é pintada célula a célula (OnGridCellPainting) — a seleção nativa é
            // neutralizada aqui para nunca competir com o realce custom.
            g.DefaultCellStyle.SelectionBackColor = Color.White;
            g.DefaultCellStyle.SelectionForeColor = Theme.TextDark;

            var cCod = new DataGridViewTextBoxColumn();
            cCod.Name = "codigo"; cCod.HeaderText = "Código"; cCod.Width = S(96);
            cCod.SortMode = DataGridViewColumnSortMode.NotSortable;
            cCod.ReadOnly = false;   // só editável na ÚLTIMA linha (adicionar); nas demais é travado por célula
            cCod.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            cCod.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            g.Columns.Add(cCod);

            var cNome = new DataGridViewTextBoxColumn();
            cNome.Name = "produto"; cNome.HeaderText = "Produto";
            cNome.SortMode = DataGridViewColumnSortMode.NotSortable;
            cNome.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            cNome.MinimumWidth = S(180);
            cNome.ReadOnly = true;
            g.Columns.Add(cNome);

            var cQtd = new DataGridViewTextBoxColumn();
            cQtd.Name = "caixa"; cQtd.HeaderText = "Quantidade"; cQtd.Width = S(120);
            cQtd.SortMode = DataGridViewColumnSortMode.NotSortable;
            cQtd.ReadOnly = false;
            cQtd.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            cQtd.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            cQtd.DefaultCellStyle.Font = Theme.BodyBold;
            g.Columns.Add(cQtd);

            // Coluna PRÓPRIA da lixeira (read-only: clicar NUNCA abre edição). No fim da linha.
            var cRem = new DataGridViewTextBoxColumn();
            cRem.Name = "remover"; cRem.HeaderText = ""; cRem.Width = S(44);
            cRem.SortMode = DataGridViewColumnSortMode.NotSortable;
            cRem.ReadOnly = true;
            g.Columns.Add(cRem);

            g.CellMouseClick += new DataGridViewCellMouseEventHandler(OnGridCellMouseClick);
            g.CellPainting += new DataGridViewCellPaintingEventHandler(OnGridCellPainting);
            g.CellBeginEdit += new DataGridViewCellCancelEventHandler(delegate(object s, DataGridViewCellCancelEventArgs ce) { InvalidateLinha(ce.RowIndex); });
            g.CellEndEdit += new DataGridViewCellEventHandler(OnGridEndEdit);
            g.CellEndEdit += new DataGridViewCellEventHandler(delegate(object s, DataGridViewCellEventArgs ce) { InvalidateLinha(ce.RowIndex); });
            g.EditingControlShowing += new DataGridViewEditingControlShowingEventHandler(OnGridEditing);
            g.MouseMove += new MouseEventHandler(OnGridMouseMove);
            g.MouseLeave += new EventHandler(OnGridMouseLeave);
            // Navegar por teclado (setas/Tab) precisa de referência visível: a linha corrente
            // entra no realce da pintura custom, e a troca repinta a antiga e a nova.
            g.CurrentCellChanged += new EventHandler(OnGridCurrentCellChanged);
            return g;
        }

        private void OnGridCurrentCellChanged(object sender, EventArgs e)
        {
            if (grid == null) return;
            int atual = grid.CurrentCell != null ? grid.CurrentCell.RowIndex : -1;
            if (atual == _linhaCorrente) return;
            int antiga = _linhaCorrente;
            _linhaCorrente = atual;
            InvalidateLinha(antiga); InvalidateLinha(atual);
        }

        private void CarregarGrade()
        {
            _carregando = true;
            grid.Rows.Clear();
            foreach (var it in PedidoEmbalagens.Listar())
            {
                // Qtd < 2 = listado aguardando preenchimento: célula vazia
                int r = grid.Rows.Add(it.Codigo.ToString(), "", it.Qtd >= 2 ? it.Qtd.ToString() : "", "");
                grid.Rows[r].Tag = it.Codigo;   // código "dono" da linha (p/ detectar alteração)
                grid.Rows[r].Cells["codigo"].ReadOnly = true;   // código só se digita na linha de adicionar
                string nome = it.Nome.Length > 0 ? it.Nome : ProdutoNomes.Get(it.Codigo);
                SetNomeLinha(grid.Rows[r], nome);
            }
            grid.LimparSelecao();
            RecalcularZebra();
            _carregando = false;
        }

        // Filtra as linhas pelo código/nome, ao digitar. A linha de ADICIONAR fica sempre
        // visível — incluir um produto novo não pode depender de limpar a busca.
        private void AplicarFiltro()
        {
            if (grid == null) return;
            string busca = txtBusca != null ? txtBusca.Text : "";
            if (grid.IsCurrentCellInEditMode) { try { grid.EndEdit(); } catch { } }
            // a linha corrente não pode ficar invisível (o WinForms recusa): solta antes
            try { grid.CurrentCell = null; } catch { }
            grid.SuspendLayout();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                string alvo = Convert.ToString(row.Cells["codigo"].Value) + " "
                            + Convert.ToString(row.Cells["produto"].Value);
                try { row.Visible = BuscaSolicitacao.CorrespondeTexto(alvo, busca); }
                catch { }
            }
            RecalcularZebra();
            grid.ResumeLayout();
            grid.Invalidate();
        }

        // A faixa da zebra segue as linhas VISÍVEIS (ver campo _faixa).
        private void RecalcularZebra()
        {
            _faixa.Clear();
            bool faixa = false;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow || !row.Visible) continue;
                _faixa[row] = faixa;
                faixa = !faixa;
            }
        }

        private void SetNomeLinha(DataGridViewRow row, string nome)
        {
            bool tem = nome != null && nome.Length > 0;
            row.Cells["produto"].Value = tem ? nome : SemNome;
            row.Cells["produto"].Style.ForeColor = tem ? Theme.TextDark : Theme.TextSoft;
        }

        // ===== visual: pintura 100% própria de cada célula =====
        // Realce ÚNICO: a linha é destacada se está SOB O MOUSE, EM EDIÇÃO ou é a CORRENTE
        // (mesma cor) — a corrente entra para quem navega por teclado não digitar "no escuro"
        // num diálogo em que a célula confirmada SALVA na hora. A lixeira mora em coluna
        // própria (read-only) e aparece na linha realçada. Como o campo de edição (e o painel
        // dele) é pintado com a MESMA cor de realce, editar não cria emenda branca, e a
        // lixeira continua visível (coluna à parte) enquanto se digita a quantidade.

        private int LinhaEmEdicao()
        {
            return grid.IsCurrentCellInEditMode && grid.CurrentCell != null ? grid.CurrentCell.RowIndex : -1;
        }

        private void OnGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;   // cabeçalho: o GridPro pinta (flat claro)
            string col = grid.Columns[e.ColumnIndex].Name;
            var row = grid.Rows[e.RowIndex];

            bool realce = (e.RowIndex == grid.LinhaHover) || (e.RowIndex == LinhaEmEdicao())
                || (grid.CurrentCell != null && grid.CurrentCell.RowIndex == e.RowIndex);
            bool faixa;
            _faixa.TryGetValue(row, out faixa);
            Color fundo = realce ? Realce : (faixa ? Theme.GridZebra : Color.White);

            using (var br = new SolidBrush(fundo))
                e.Graphics.FillRectangle(br, e.CellBounds);
            using (var pen = new Pen(Theme.GridLine))
                e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);

            string texto = Convert.ToString(e.Value);
            if (texto == null) texto = "";

            if (col == "codigo")
            {
                // Centralizado, como as colunas curtas das grades (retângulo simétrico,
                // senão a padding entortaria o centro).
                var rc = new Rectangle(e.CellBounds.Left + S(6), e.CellBounds.Top, e.CellBounds.Width - S(12), e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, texto, Theme.Body, rc, Theme.TextDark,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            else if (col == "produto")
            {
                // A última linha (vazia) é o "adicionar produto" — sem esta dica ela
                // parecia uma linha quebrada, e a instrução vivia só no parágrafo do topo.
                if (row.IsNewRow && texto.Length == 0)
                {
                    var rcDica = new Rectangle(e.CellBounds.Left + S(8), e.CellBounds.Top, e.CellBounds.Width - S(16), e.CellBounds.Height);
                    TextRenderer.DrawText(e.Graphics, "digite o código ao lado para incluir um produto",
                        Theme.Small, rcDica, Theme.TextSoft,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    e.Handled = true;
                    return;
                }
                bool vazio = texto == SemNome || texto == Verificando || texto.Length == 0;
                var rc = new Rectangle(e.CellBounds.Left + S(8), e.CellBounds.Top, e.CellBounds.Width - S(16), e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, texto, Theme.Body, rc, vazio ? Theme.TextSoft : Theme.TextDark,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            else if (col == "caixa")
            {
                var rc = new Rectangle(e.CellBounds.Left + S(8), e.CellBounds.Top, e.CellBounds.Width - S(16), e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, texto, Theme.BodyBold, rc, Theme.TextDark,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else if (col == "remover")
            {
                if (realce && _fontLixeira != null && !row.IsNewRow)
                    TextRenderer.DrawText(e.Graphics, ((char)0xE74D).ToString(), _fontLixeira, e.CellBounds, Theme.Error,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            e.Handled = true;
        }

        // O hover em si é do GridPro (que já repinta as linhas afetadas, inclusive ao rolar
        // com a roda sem mover o mouse); aqui fica só o cursor de mãozinha sobre a lixeira.
        private void OnGridMouseMove(object sender, MouseEventArgs e)
        {
            var hit = grid.HitTest(e.X, e.Y);
            bool sobreLixeira = hit.RowIndex >= 0 && hit.ColumnIndex >= 0
                && grid.Columns[hit.ColumnIndex].Name == "remover" && !grid.Rows[hit.RowIndex].IsNewRow;
            grid.Cursor = sobreLixeira ? Cursors.Hand : Cursors.Default;
        }

        private void OnGridMouseLeave(object sender, EventArgs e)
        {
            grid.Cursor = Cursors.Default;
        }

        private void InvalidateLinha(int r)
        {
            if (r >= 0 && r < grid.Rows.Count) grid.InvalidateRow(r);
        }

        // ===== remover =====

        // Clique na coluna da lixeira (read-only, então nunca abre edição) remove a linha.
        private void OnGridCellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "remover") return;
            if (grid.Rows[e.RowIndex].IsNewRow) return;
            int r = e.RowIndex;
            this.BeginInvoke((MethodInvoker)delegate { RemoverLinha(r); });   // remove após o clique
        }

        private void RemoverLinha(int r)
        {
            if (r < 0 || r >= grid.Rows.Count || grid.Rows[r].IsNewRow) return;
            int cod;
            if (!int.TryParse(Convert.ToString(grid.Rows[r].Cells["codigo"].Value), out cod)) cod = 0;
            if (cod > 0) PedidoEmbalagens.Remover(cod);
            grid.Rows.RemoveAt(r);
            RecalcularZebra();
        }

        // Descarta uma linha com segurança. A "nova linha" (placeholder de adicionar) NÃO
        // pode ser removida — o WinForms lança InvalidOperationException ("não é possível
        // excluir a nova linha não confirmada"). Nela, apenas devolvemos a célula ao vazio.
        private void DescartarLinha(DataGridViewRow row)
        {
            if (row == null) return;
            if (row.IsNewRow)
            {
                try { row.Cells["codigo"].Value = ""; } catch { }
                return;
            }
            try { grid.Rows.Remove(row); } catch { }
            RecalcularZebra();
        }

        // ===== edição (auto-save) =====

        private void OnGridEditing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var tb = e.Control as TextBox;
            if (tb == null) return;
            // fundo do campo = realce da linha (a linha em edição é sempre realçada) → sem emenda
            tb.BackColor = Realce;
            tb.ForeColor = Theme.TextDark;
            tb.KeyPress -= SoDigitos;
            if (grid.CurrentCell != null)
            {
                string col = grid.Columns[grid.CurrentCell.ColumnIndex].Name;
                if (col == "caixa" || col == "codigo") tb.KeyPress += SoDigitos;
            }
            // O TextBox não ocupa a célula toda: o resto é o painel de edição do DataGridView,
            // que sai BRANCO (BackColor da célula) e abria uma faixa clara na região da
            // padding, cortando o realce bem na célula em edição. O painel só existe depois
            // deste evento — daí o BeginInvoke.
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (tb.IsDisposed || tb.Parent == null) return;
                tb.Parent.BackColor = Realce;
            });
        }

        private void SoDigitos(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        }

        private void OnGridEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (_carregando) return;
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var row = grid.Rows[e.RowIndex];
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col == "codigo") ComitarCodigo(row);              // inclui a linha de adicionar
            else if (col == "caixa" && !row.IsNewRow) ComitarQuantidade(row);
        }

        // Código digitado/alterado: nova linha vira cadastro; linha existente é "re-codificada".
        private void ComitarCodigo(DataGridViewRow row)
        {
            int original = row.Tag is int ? (int)row.Tag : 0;
            string texto = Convert.ToString(row.Cells["codigo"].Value);
            texto = texto == null ? "" : texto.Trim();
            int cod;
            int.TryParse(texto, out cod);

            if (cod <= 0)
            {
                if (original > 0)
                {
                    row.Cells["codigo"].Value = original.ToString();   // apagou: volta o que era
                    return;
                }
                // linha nova sem código válido: se também não tem quantidade, descarta.
                // (na linha-fantasma isso apenas limpa a célula — ela NÃO pode ser removida)
                if (Convert.ToString(row.Cells["caixa"].Value) == null ||
                    Convert.ToString(row.Cells["caixa"].Value).Trim().Length == 0)
                    DescartarLinha(row);
                return;
            }
            if (cod == original) return;   // sem mudança

            if (PedidoEmbalagens.Existe(cod))
            {
                StyledDialog.Aviso(this, "Código já listado",
                    "O produto " + cod + " já está na lista. Ajuste a quantidade na linha dele.", "Entendi");
                if (original > 0) row.Cells["codigo"].Value = original.ToString();
                else DescartarLinha(row);
                return;
            }

            // quantidade que a linha carrega (pode estar vazia)
            int qtd;
            string qtxt = Convert.ToString(row.Cells["caixa"].Value);
            if (!int.TryParse(qtxt == null ? "" : qtxt.Trim(), out qtd) || qtd < 2) qtd = 0;

            if (original > 0) PedidoEmbalagens.Remover(original);   // re-codificação: sai o antigo
            if (qtd >= 2) PedidoEmbalagens.Definir(cod, qtd, null);
            else PedidoEmbalagens.AdicionarSemQtd(cod, null);
            row.Tag = cod;
            row.Cells["codigo"].ReadOnly = true;   // após adicionar, o código fica travado (como as demais linhas)
            RecalcularZebra();

            ResolverNome(row, cod);

            // fluxo natural: código digitado -> foco vai direto para a QUANTIDADE da mesma
            // linha (e não para uma nova linha abaixo). Deferido: o move roda depois que o
            // grid termina de confirmar a edição atual (senão a linha-fantasma se move sozinha).
            int destino = row.Index;
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (destino < 0 || destino >= grid.Rows.Count || grid.Rows[destino].IsNewRow) return;
                try
                {
                    grid.CurrentCell = grid.Rows[destino].Cells["caixa"];
                    grid.BeginEdit(true);
                }
                catch { }
            });
        }

        // Quantidade digitada/alterada numa linha com código: salva na hora.
        private void ComitarQuantidade(DataGridViewRow row)
        {
            int cod = row.Tag is int ? (int)row.Tag : 0;
            if (cod <= 0) return;   // linha nova ainda sem código: espera o código

            string texto = Convert.ToString(row.Cells["caixa"].Value);
            texto = texto == null ? "" : texto.Trim();
            int qtd;
            if (texto.Length == 0 || (int.TryParse(texto, out qtd) && qtd == 0))
            {
                PedidoEmbalagens.LimparQtd(cod);           // apagou: continua listado, sem quantidade
                row.Cells["caixa"].Value = "";
            }
            else if (int.TryParse(texto, out qtd) && qtd >= 2)
            {
                string nome = Convert.ToString(row.Cells["produto"].Value);
                PedidoEmbalagens.Definir(cod, qtd, nome == SemNome || nome == Verificando ? null : nome);
                row.Cells["caixa"].Value = qtd.ToString();
            }
            else
            {
                int atual = PedidoEmbalagens.Obter(cod);   // inválido (ex.: 1): volta o que estava
                row.Cells["caixa"].Value = atual >= 2 ? atual.ToString() : "";
            }
        }

        // Nome do produto de um código novo: cache local primeiro; com sessão, resolve ao vivo.
        private async void ResolverNome(DataGridViewRow row, int cod)
        {
            string nome = PedidoEmbalagens.Nome(cod);
            if (nome.Length == 0) nome = ProdutoNomes.Get(cod);
            if (nome != null && nome.Length > 0)
            {
                SetNomeLinha(row, nome);
                PedidoEmbalagens.AtualizarNome(cod, nome);
                return;
            }
            if (_session == null) { SetNomeLinha(row, null); return; }   // offline: fica "—"

            row.Cells["produto"].Value = Verificando;
            row.Cells["produto"].Style.ForeColor = Theme.TextSoft;
            bool tenho = false;
            try
            {
                // Portão único do MV: o monitor de solicitações roda em segundo plano mesmo com
                // este diálogo aberto (o laço modal continua bombeando o timer).
                tenho = await MvGate.EntrarAsync("caixas-resolver-nome", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho) { SetNomeLinha(row, null); return; }

                var achados = await MorphisClient.ResolverNomesAsync(
                    _session, ContagemFarmacia.Estoque, new List<int> { cod }, null);
                if (IsDisposed || Disposing) return;
                string nm;
                if (achados.TryGetValue(cod, out nm))
                {
                    // a linha pode ter sido re-codificada/removida durante o await
                    if (row.Index >= 0 && (row.Tag is int) && (int)row.Tag == cod)
                    { SetNomeLinha(row, nm); PedidoEmbalagens.AtualizarNome(cod, nm); }
                }
                else
                {
                    StyledDialog.Aviso(this, "Código não encontrado",
                        "O código " + cod + " não existe no estoque da Farmácia Central no MV Soul.", "Entendi");
                    PedidoEmbalagens.Remover(cod);
                    if (row.Index >= 0 && (row.Tag is int) && (int)row.Tag == cod) DescartarLinha(row);
                }
            }
            catch (Exception ex)
            {
                // MV indisponível: mantém cadastrado com "—" (o nome chega noutro momento)
                Logger.Log("Embalagens: nome ao vivo indisponível p/ " + cod + ": " + ex.Message);
                if (row.Index >= 0) SetNomeLinha(row, null);
            }
            finally { if (tenho) MvGate.Sair(); }
        }
    }
}
