using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Janela do F9 da aba Atender — lotes do produto e produtos do estoque. Faz o papel da
    // "lista de valores" do MV. Com busca (produtos): abre com o foco no filtro, o atendente
    // digita parte do nome (ou do código) e escolhe com Enter ou duplo clique. Sem busca
    // (lotes, poucas linhas): só a lista, setas e Enter. Linha bloqueada (lote vencido)
    // aparece riscada e não pode ser escolhida: a janela diz por quê.
    public class ListaEscolhaDialog : MolduraDialog
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public class Coluna
        {
            public string Titulo;
            public int Largura;          // px a 96 DPI; ignorada quando Preenche
            public bool Direita;
            public bool Preenche;
            public Coluna(string titulo, int largura, bool direita, bool preenche)
            {
                Titulo = titulo; Largura = largura; Direita = direita; Preenche = preenche;
            }
        }

        private readonly string _titulo;
        private readonly string _subtitulo;
        private readonly string _dicaBusca;          // null = sem campo de busca
        private readonly Coluna[] _colunas;
        private readonly List<string[]> _linhas;
        private readonly List<string> _bloqueio;     // motivo por linha (null = pode escolher)

        private GridPro grid;
        private TextBox txtBusca;
        private Label lblRodape;
        private System.Windows.Forms.Timer _espera;
        private static readonly Font FonteBloqueada = new Font(Theme.Body, FontStyle.Strikeout);

        // Índice (em linhas) da escolhida; -1 = fechou sem escolher.
        public int Escolhido = -1;

        public ListaEscolhaDialog(string titulo, string subtitulo, Coluna[] colunas, List<string[]> linhas,
            List<string> bloqueio, string dicaBusca, Size tamanho) : base(tamanho)
        {
            _titulo = titulo; _subtitulo = subtitulo; _colunas = colunas; _linhas = linhas;
            _bloqueio = bloqueio; _dicaBusca = dicaBusca;

            BuildUI();
            Preencher();
            this.MinimumSize = new Size(S(420), S(ComBusca ? 260 : 180));
            this.KeyDown += new KeyEventHandler(OnTecla);
            this.FormClosed += new FormClosedEventHandler(delegate
            {
                if (_espera != null) { _espera.Dispose(); _espera = null; }
            });
        }

        private bool ComBusca { get { return _dicaBusca != null; } }

        private void BuildUI()
        {
            var pnlBody = new Panel();
            pnlBody.Dock = DockStyle.Fill;
            pnlBody.BackColor = Theme.CardBg;
            pnlBody.Padding = new Padding(S(24), S(2), S(24), S(16));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowCount = 3;
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // busca
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // grade
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // rodapé (contagem / aviso)

            if (ComBusca)
            {
                var hostBusca = new FieldHost();
                hostBusca.Dock = DockStyle.Fill;
                hostBusca.Height = S(36);
                hostBusca.Margin = new Padding(0, 0, 0, S(10));
                hostBusca.Padding = new Padding(S(10), S(8), S(8), S(6));

                var lupa = new Label();
                lupa.Text = "";                  // glifo "Search" (Segoe MDL2 Assets)
                lupa.Font = Theme.Icon; lupa.ForeColor = Theme.TextSoft; lupa.BackColor = Color.White;
                lupa.Dock = DockStyle.Left; lupa.Width = S(30);   // o texto não encosta na lupa
                lupa.TextAlign = ContentAlignment.MiddleLeft;

                txtBusca = new TextBox();
                txtBusca.BorderStyle = BorderStyle.None;
                txtBusca.Font = Theme.Input; txtBusca.ForeColor = Theme.TextDark; txtBusca.BackColor = Color.White;
                txtBusca.Dock = DockStyle.Fill;
                _espera = new System.Windows.Forms.Timer();
                _espera.Interval = 120;
                _espera.Tick += new EventHandler(delegate { _espera.Stop(); AplicarFiltro(); });
                txtBusca.TextChanged += new EventHandler(delegate { _espera.Stop(); _espera.Start(); });
                txtBusca.KeyDown += new KeyEventHandler(OnTeclaBusca);
                txtBusca.HandleCreated += new EventHandler(delegate
                {
                    SendMessage(txtBusca.Handle, 0x1501, (IntPtr)1, _dicaBusca.Length > 0 ? _dicaBusca : "Digite para filtrar");
                });
                hostBusca.Controls.Add(txtBusca);
                hostBusca.Controls.Add(lupa);
                tl.Controls.Add(hostBusca, 0, 0);
            }

            grid = new GridPro();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.AllowUserToResizeColumns = false;
            grid.AllowUserToOrderColumns = false;
            grid.ShowCellToolTips = true;
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;   // título em uma linha
            AtenderView.Compactar(grid);
            foreach (var c in _colunas)
            {
                // Mesmos recuos da grade da aba (número não encosta no texto ao lado). As de
                // largura fixa ficam nela (peso 0); a que preenche leva o resto.
                var col = AtenderView.NovaColuna(c.Titulo, c.Preenche ? 1 : 0, c.Direita);
                col.MinimumWidth = Math.Max(col.MinimumWidth, S(c.Preenche ? 140 : c.Largura));
                grid.Columns.Add(col);
            }
            AtenderView.RepartirSempre(grid);
            grid.CellFormatting += new DataGridViewCellFormattingEventHandler(OnFormatar);
            grid.CellDoubleClick += new DataGridViewCellEventHandler(delegate(object s, DataGridViewCellEventArgs ev)
            {
                if (ev.RowIndex >= 0) Escolher(grid.Rows[ev.RowIndex]);
            });
            if (ComBusca)
                grid.KeyPress += new KeyPressEventHandler(delegate(object s, KeyPressEventArgs ke)
                {
                    // Digitou com a grade em foco: o texto vai para a busca (como na lista do MV).
                    if (char.IsLetterOrDigit(ke.KeyChar) || ke.KeyChar == ' ' || char.IsPunctuation(ke.KeyChar))
                    {
                        txtBusca.Focus();
                        txtBusca.AppendText(ke.KeyChar.ToString());
                        ke.Handled = true;
                    }
                });
            tl.Controls.Add(grid, 0, 1);

            lblRodape = new Label();
            lblRodape.AutoSize = true;
            lblRodape.Font = Theme.Small; lblRodape.ForeColor = Theme.Error; lblRodape.BackColor = Theme.CardBg;
            lblRodape.Margin = new Padding(0, S(8), 0, 0);
            tl.Controls.Add(lblRodape, 0, 2);

            pnlBody.Controls.Add(tl);
            this.Controls.Add(pnlBody);
            this.Controls.Add(Cabecalho(_titulo, _subtitulo));   // Top por cima do Fill
        }

        private void Preencher()
        {
            grid.SuspendLayout();
            for (int i = 0; i < _linhas.Count; i++)
            {
                int r = grid.Rows.Add(_linhas[i]);
                grid.Rows[r].Tag = i;
                string motivo = Motivo(i);
                if (motivo != null)
                    foreach (DataGridViewCell c in grid.Rows[r].Cells) c.ToolTipText = motivo;
            }
            grid.ResumeLayout();
            AtualizarRodape(null);
        }

        private string Motivo(int i)
        {
            return _bloqueio != null && i < _bloqueio.Count ? _bloqueio[i] : null;
        }

        private void OnFormatar(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            object tag = grid.Rows[e.RowIndex].Tag;
            if (!(tag is int) || Motivo((int)tag) == null) return;
            e.CellStyle.ForeColor = Theme.TextSoft;
            e.CellStyle.SelectionForeColor = Theme.TextSoft;
            e.CellStyle.Font = FonteBloqueada;
        }

        private void AplicarFiltro()
        {
            if (!ComBusca) return;
            string busca = txtBusca.Text;
            grid.SuspendLayout();
            try { grid.CurrentCell = null; } catch { }
            foreach (DataGridViewRow row in grid.Rows)
            {
                int i = (int)row.Tag;
                row.Visible = BuscaSolicitacao.CorrespondeTexto(string.Join(" ", _linhas[i]), busca);
            }
            grid.ResumeLayout();
            var primeira = PrimeiraVisivel();
            if (primeira != null) { try { grid.CurrentCell = primeira.Cells[0]; primeira.Selected = true; } catch { } }
            AtualizarRodape(null);
        }

        // A primeira linha visível que PODE ser escolhida (o lote vencido no topo da lista não
        // fica selecionado à toa); sem nenhuma, a primeira visível.
        private DataGridViewRow PrimeiraVisivel()
        {
            DataGridViewRow qualquer = null;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (!row.Visible) continue;
                if (qualquer == null) qualquer = row;
                if (row.Tag is int && Motivo((int)row.Tag) == null) return row;
            }
            return qualquer;
        }

        // Sem legenda (pedido do usuário, 30/10/2026): o rodapé só aparece para dizer por que a
        // linha não pode ser escolhida (lote vencido).
        private void AtualizarRodape(string aviso)
        {
            if (aviso == null) { lblRodape.Visible = false; return; }
            lblRodape.Text = aviso;
            if (lblRodape.Visible) return;
            lblRodape.Visible = true;
            if (!ComBusca && IsHandleCreated)
            {
                // A lista de lotes tem a altura exata das linhas: cresce para o aviso não esconder a última.
                int mais = lblRodape.PreferredHeight + lblRodape.Margin.Vertical;
                this.Height += mais;
                this.Top -= mais / 2;
            }
        }

        private void Escolher(DataGridViewRow row)
        {
            if (row == null || !row.Visible || !(row.Tag is int)) return;
            int i = (int)row.Tag;
            string motivo = Motivo(i);
            if (motivo != null) { AtualizarRodape(motivo + " — não pode ser escolhido."); return; }
            Escolhido = i;
            this.DialogResult = DialogResult.OK;
            Close();
        }

        private void OnTeclaBusca(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.PageDown)
            {
                e.SuppressKeyPress = true;
                grid.Focus();
                if (grid.CurrentRow == null) { var p = PrimeiraVisivel(); if (p != null) grid.CurrentCell = p.Cells[0]; }
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _espera.Stop(); AplicarFiltro();
                Escolher(grid.CurrentRow != null && grid.CurrentRow.Visible ? grid.CurrentRow : PrimeiraVisivel());
            }
            else if (e.KeyCode == Keys.Escape && txtBusca.Text.Length > 0)
            {
                e.SuppressKeyPress = true;
                txtBusca.Text = "";
            }
        }

        private void OnTecla(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); return; }
            if (e.KeyCode == Keys.Enter && grid.Focused) { e.SuppressKeyPress = true; Escolher(grid.CurrentRow); }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (ComBusca) return;   // a de produtos muda de tamanho com o filtro: fica como está
            // Lotes (poucas linhas): a janela encolhe até a última linha, sem área vazia embaixo.
            // A conta parte do TOPO da grade, não da altura dela: durante o OnLoad o layout ainda
            // reservava a faixa do rodapé escondido (a grade ficava 23 px maior que as linhas).
            Control tl = grid.Parent, corpo = tl.Parent;
            int precisa = grid.ColumnHeadersHeight + grid.Rows.Count * grid.RowTemplate.Height + S(2);
            int cliente = corpo.Top + tl.Top + grid.Top + precisa + corpo.Padding.Bottom + this.Padding.Bottom;
            if (cliente >= this.ClientSize.Height) return;
            int antes = this.Height;
            this.ClientSize = new Size(this.ClientSize.Width, cliente);
            this.Top += (antes - this.Height) / 2;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var p = PrimeiraVisivel();
            if (p != null) { try { grid.CurrentCell = p.Cells[0]; p.Selected = true; } catch { } }
            if (ComBusca) txtBusca.Focus(); else grid.Focus();
        }
    }
}
