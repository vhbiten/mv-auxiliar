using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Item do combo de dia (nome + número 1=Segunda..7=Domingo).
    public class DiaItem
    {
        public string Nome; public int Num;
        public DiaItem(string nome, int num) { Nome = nome; Num = num; }
        public override string ToString() { return Nome; }
    }

    // Módulo "Conferência de Lotes": escolhe dia + período e gera o PDF de conferência.
    // Os códigos de cada dia/período já vêm embutidos no app (ContagemListas).
    // Config fixa: estoque 5 (Farmácia Central), sem saldo zerado, ordem por descrição.
    // Somente leitura — não altera estoque.
    public class ConferenciaView : UserControl
    {
        private readonly MainWindow _host;
        private StyledComboBox cmbDia;
        private StyledComboBox cmbPeriodo;
        private PrimaryButton btnGerar;
        private StatusBanner banner;
        private Label lblResumo;
        private Label lblFaltam;
        private DataGridView grid;

        private bool _ready;

        private const int Estoque = 5;

        public ConferenciaView(MainWindow host)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;   // herda o DPI da janela
            this.BackColor = Theme.ContentBg;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 152));  // cartão de entrada
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // lista do que será conferido

            root.Controls.Add(BuildInputCard(), 0, 0);
            root.Controls.Add(BuildBanner(), 0, 1);
            root.Controls.Add(BuildListaCard(), 0, 2);

            this.Controls.Add(root);

            // Liga os eventos de mudança; a seleção/resumo inicial é feita no Load,
            // pois SelectedItem só é resolvido após o handle do combo ser criado.
            cmbDia.SelectedIndexChanged += new EventHandler(OnSelecaoMudou);
            cmbPeriodo.SelectedIndexChanged += new EventHandler(OnSelecaoMudou);
            this.Load += new EventHandler(OnLoad);
        }

        private void OnLoad(object sender, EventArgs e)
        {
            // pré-seleciona o dia de hoje (1=Segunda..7=Domingo)
            int hoje = (int)DateTime.Now.DayOfWeek;       // 0=Domingo..6=Sábado
            int diaNum = hoje == 0 ? 7 : hoje;
            for (int i = 0; i < cmbDia.Items.Count; i++)
                if (((DiaItem)cmbDia.Items[i]).Num == diaNum) { cmbDia.SelectedIndex = i; break; }

            _ready = true;
            AtualizarResumo(true);
        }

        private Control BuildInputCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, 14);
            card.Padding = new Padding(20, 16, 20, 18);

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 4;
            tl.RowCount = 3;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titulo = CardTitle("1.  Selecione o dia e o período");
            titulo.Margin = new Padding(0, 0, 0, 12);
            tl.Controls.Add(titulo, 0, 0);
            tl.SetColumnSpan(titulo, 4);

            tl.Controls.Add(MicroLabel("DIA DA SEMANA"), 0, 1);
            tl.Controls.Add(MicroLabel("PERÍODO"), 1, 1);

            cmbDia = new StyledComboBox();
            cmbDia.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbDia.Margin = new Padding(0, 0, 14, 0);
            cmbDia.DataSource = new List<DiaItem> {
                new DiaItem("Segunda-feira", 1), new DiaItem("Terça-feira", 2), new DiaItem("Quarta-feira", 3),
                new DiaItem("Quinta-feira", 4), new DiaItem("Sexta-feira", 5), new DiaItem("Sábado", 6), new DiaItem("Domingo", 7)
            };
            tl.Controls.Add(cmbDia, 0, 2);

            cmbPeriodo = new StyledComboBox();
            cmbPeriodo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbPeriodo.Margin = new Padding(0, 0, 14, 0);
            cmbPeriodo.DataSource = new List<string> { "Diurno", "Noturno" };
            tl.Controls.Add(cmbPeriodo, 1, 2);

            btnGerar = new PrimaryButton();
            btnGerar.Text = "Gerar conferência (PDF)";
            btnGerar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnGerar.Height = 40;
            btnGerar.Click += new EventHandler(OnGerar);
            tl.Controls.Add(btnGerar, 2, 2);

            card.Controls.Add(tl);
            return card;
        }

        private Control BuildBanner()
        {
            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, 14);
            return banner;
        }

        // Cartão com a LISTA do que será conferido (código + nome) e, à direita,
        // o bloco "Como funciona". Nomes vêm do ProdutoNomes (semente + aprendidos).
        private Control BuildListaCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, 0);
            card.Padding = new Padding(22, 16, 22, 18);

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 2;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // lista
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));  // como funciona
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var titulo = CardTitle("2.  O que será conferido");
            titulo.Margin = new Padding(0, 0, 0, 10);
            tl.Controls.Add(titulo, 0, 0);

            lblResumo = new Label();
            lblResumo.AutoSize = true;
            lblResumo.Font = Theme.BodyBold;
            lblResumo.ForeColor = Theme.TextSoft;
            lblResumo.BackColor = Theme.CardBg;
            lblResumo.Anchor = AnchorStyles.Right;
            lblResumo.Margin = new Padding(0, 2, 0, 0);
            lblResumo.Text = "";
            tl.Controls.Add(lblResumo, 1, 0);

            grid = NovaGrid();
            tl.Controls.Add(grid, 0, 1);

            // bloco "como funciona" (direita)
            var info = new FlowLayoutPanel();
            info.BackColor = Theme.CardBg;
            info.Dock = DockStyle.Fill;
            info.FlowDirection = FlowDirection.TopDown;
            info.WrapContents = false;
            info.Padding = new Padding(20, 2, 0, 0);

            var infoTitle = new Label();
            infoTitle.Text = "Como funciona";
            infoTitle.Font = Theme.H3;
            infoTitle.ForeColor = Theme.TextDark;
            infoTitle.BackColor = Theme.CardBg;
            infoTitle.AutoSize = true;
            infoTitle.Margin = new Padding(0, 0, 0, 8);
            info.Controls.Add(infoTitle);

            string[] bullets = {
                "Estoque: Farmácia Central (5)",
                "Não mostra saldos zerados",
                "Ordenado por descrição do produto"
            };
            foreach (var b in bullets)
            {
                var l = new Label();
                l.Text = "•  " + b;
                l.Font = Theme.Body;
                l.ForeColor = Theme.TextMuted;
                l.BackColor = Theme.CardBg;
                l.AutoSize = true;
                l.MaximumSize = new Size(270, 0);
                l.Margin = new Padding(0, 0, 0, 6);
                info.Controls.Add(l);
            }

            lblFaltam = new Label();
            lblFaltam.Font = Theme.Small;
            lblFaltam.ForeColor = Theme.TextSoft;
            lblFaltam.BackColor = Theme.CardBg;
            lblFaltam.AutoSize = true;
            lblFaltam.MaximumSize = new Size(270, 0);
            lblFaltam.Margin = new Padding(0, 10, 0, 0);
            lblFaltam.Visible = false;
            info.Controls.Add(lblFaltam);

            tl.Controls.Add(info, 1, 1);

            card.Controls.Add(tl);
            return card;
        }

        private DataGridView NovaGrid()
        {
            var g = new DataGridView();
            g.Dock = DockStyle.Fill;
            g.BackgroundColor = Color.White; g.BorderStyle = BorderStyle.None;
            g.RowHeadersVisible = false; g.AllowUserToAddRows = false; g.AllowUserToResizeRows = false;
            g.AllowUserToResizeColumns = false; g.ReadOnly = true;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
            g.AllowUserToOrderColumns = false; g.EnableHeadersVisualStyles = false;
            g.GridColor = Theme.BorderSoft; g.Font = Theme.Body;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Primary;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            g.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 34; g.RowTemplate.Height = 27;
            g.DefaultCellStyle.Padding = new Padding(8, 0, 6, 0);
            g.DefaultCellStyle.SelectionBackColor = Theme.Light;
            g.DefaultCellStyle.SelectionForeColor = Theme.TextDark;
            g.DefaultCellStyle.ForeColor = Theme.TextDark;
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridZebra;

            var cCod = new DataGridViewTextBoxColumn();
            cCod.Name = "codigo"; cCod.HeaderText = "Código"; cCod.Width = 100;
            cCod.SortMode = DataGridViewColumnSortMode.NotSortable;
            g.Columns.Add(cCod);

            var cNome = new DataGridViewTextBoxColumn();
            cNome.Name = "produto"; cNome.HeaderText = "Produto";
            cNome.SortMode = DataGridViewColumnSortMode.NotSortable;
            cNome.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            g.Columns.Add(cNome);

            return g;
        }

        private Label CardTitle(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.H3; l.ForeColor = Theme.TextDark;
            l.BackColor = Theme.CardBg; l.AutoSize = true;
            return l;
        }

        private Label MicroLabel(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft;
            l.BackColor = Theme.CardBg; l.AutoSize = true;
            l.Margin = new Padding(2, 0, 0, 5);
            return l;
        }

        private void OnSelecaoMudou(object sender, EventArgs e) { AtualizarResumo(true); }

        // Preenche a lista (código + nome) do dia/período e habilita/desabilita o botão.
        // tocarBanner=false preserva a mensagem atual (ex.: o sucesso logo após gerar).
        private void AtualizarResumo(bool tocarBanner)
        {
            if (!_ready || cmbDia == null || cmbPeriodo == null) return;
            var dia = cmbDia.SelectedItem as DiaItem;
            string periodo = cmbPeriodo.SelectedItem as string;
            if (dia == null || periodo == null) return;

            int[] codigos = ContagemListas.Get(dia.Num, periodo);
            int n = codigos.Length, semNome = 0;

            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (int cd in codigos)
            {
                string nome = ProdutoNomes.Get(cd);
                int r = grid.Rows.Add(cd.ToString(), nome != null ? nome : "—");
                if (nome == null)
                {
                    semNome++;
                    grid.Rows[r].Cells["produto"].Style.ForeColor = Theme.TextSoft;
                }
            }
            grid.ClearSelection();
            grid.ResumeLayout();

            lblResumo.Text = n > 0 ? n + " produtos" : "";
            lblFaltam.Visible = semNome > 0;
            if (semNome > 0)
                lblFaltam.Text = semNome + " produto(s) ainda sem nome (—). Os nomes são aprendidos automaticamente ao gerar a conferência na rede do hospital.";

            if (n > 0)
            {
                btnGerar.Enabled = true;
                if (tocarBanner)
                    banner.SetState(StatusKind.Info,
                        "Lista de " + n + " produtos para " + dia.Nome + " / " + periodo + ". Clique em “Gerar conferência (PDF)”.");
            }
            else
            {
                btnGerar.Enabled = false;
                if (tocarBanner)
                    banner.SetState(StatusKind.Warning,
                        "Não há lista de produtos cadastrada para " + dia.Nome + " / " + periodo + ".");
            }
        }

        private async void OnGerar(object sender, EventArgs e)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            var dia = cmbDia.SelectedItem as DiaItem;
            string periodo = cmbPeriodo.SelectedItem as string;
            if (dia == null || periodo == null) return;

            var codigos = new List<int>(ContagemListas.Get(dia.Num, periodo));
            if (codigos.Count == 0)
            {
                banner.SetState(StatusKind.Warning, "Não há lista de produtos para " + dia.Nome + " / " + periodo + ".");
                return;
            }

            btnGerar.Enabled = false; btnGerar.Text = "Gerando...";
            banner.SetState(StatusKind.Busy, "Gerando o relatório de " + dia.Nome + " / " + periodo + " (" + codigos.Count + " produtos)...");
            try
            {
                string url = await MorphisClient.GerarConferenciaAsync(_host.Session, Estoque, codigos);
                if (IsDisposed || Disposing) return;
                Navegador.Abrir(url);   // abre o PDF numa aba (Cent Browser -> Edge -> padrão)
                banner.SetState(StatusKind.Success,
                    "Conferência de " + dia.Nome + " / " + periodo + " (" + codigos.Count + " produtos) gerada e aberta no navegador.");

                // Aprende os nomes de produto com o PDF gerado (melhor esforço) e
                // atualiza a lista na tela sem mexer na mensagem de sucesso.
                try
                {
                    int novos = await MorphisClient.AprenderNomesConferenciaAsync(_host.Session, url);
                    if (IsDisposed || Disposing) return;
                    if (novos > 0) AtualizarResumo(false);
                }
                catch (Exception exN) { Logger.Log("Aprender nomes da conferência: " + exN.Message); }
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();   // volta para o login
                return;
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing) banner.SetState(StatusKind.Error, "Erro ao gerar a conferência: " + ex.Message);
            }
            finally
            {
                if (!IsDisposed && !Disposing) { btnGerar.Enabled = true; btnGerar.Text = "Gerar conferência (PDF)"; }
            }
        }
    }
}
