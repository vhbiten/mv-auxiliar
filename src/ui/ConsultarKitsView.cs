using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Módulo "Consultar kits": via um produto comum a todos os kits (4992 - agulha 40x12),
    // gera o relatório R_KIT_PROD e mostra, por tipo de kit, o nome + quantidade em estoque +
    // os códigos de barras de cada unidade. Botão "Abrir o PDF" abre o relatório no navegador.
    // READ-ONLY — não produz nem altera nada.
    public class ConsultarKitsView : UserControl, IComandosDeTela
    {
        private readonly MainWindow _host;
        private PrimaryButton btnConsultar;
        private PrimaryButton btnPdf;
        private StatusBanner banner;
        private GridPro grid;
        private Label lblVazio;
        private string _reportUrl;
        private bool _consultando;    // consulta em andamento — segura o F5 e cliques repetidos
        private bool _reimprimindo;   // trava contra cliques repetidos no botão da linha

        private const int Estoque = 5;       // Farmácia Central
        private const int Produto = 4992;    // agulha 40x12 (presente em todos os kits)

        public ConsultarKitsView(MainWindow host)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;
            this.BackColor = Theme.ContentBg;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            root.RowCount = 3;
            // A coluna única é Percent: sem isso o TableLayoutPanel deixa a coluna crescer
            // até o "preferido" dos cartões e o conteúdo estoura o viewport à direita.
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            // AutoSize, não altura fixa: a fonte cresce com o DPI e uma altura em pixel
            // fixo cortava o conteúdo do cartão nas escalas maiores.
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // cartão de ação
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // resultados

            root.Controls.Add(BuildActionCard(), 0, 0);
            root.Controls.Add(BuildBanner(), 0, 1);
            root.Controls.Add(BuildResultCard(), 0, 2);

            this.Controls.Add(root);

            banner.SetState(StatusKind.Info,
                "Consulta os kits em estoque pelo produto " + Produto + " (Agulha 40x12), presente em todos os kits.");

            this.Load += new EventHandler(OnLoad);
        }

        private Control BuildActionCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(14));
            card.Padding = new Padding(Theme.S(20), Theme.S(16), Theme.S(20), Theme.S(16));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 3;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(200)));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(200)));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titulo = new Label();
            titulo.Text = "Kits em estoque"; titulo.Font = Theme.H3; titulo.ForeColor = Theme.TextDark;
            titulo.BackColor = Theme.CardBg; titulo.AutoSize = true;
            titulo.Margin = new Padding(0, 0, 0, Theme.S(4));
            tl.Controls.Add(titulo, 0, 0);
            tl.SetColumnSpan(titulo, 3);

            var sub = new Label();
            sub.Text = "Todos os kits em estoque na Farmácia Central.";
            sub.Font = Theme.Body; sub.ForeColor = Theme.TextSoft;
            sub.BackColor = Theme.CardBg;
            // AutoSize NÃO: com o preferido do texto, a coluna Percent não comprime e o
            // cartão estoura na janela mínima; com Fill + reticências o texto cede.
            sub.AutoSize = false;
            sub.AutoEllipsis = true;
            sub.Dock = DockStyle.Fill;
            sub.TextAlign = ContentAlignment.MiddleLeft;
            sub.Margin = new Padding(0, 0, Theme.S(8), 0);
            tl.Controls.Add(sub, 0, 1);

            btnConsultar = new PrimaryButton();
            btnConsultar.Text = "Atualizar consulta";
            btnConsultar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnConsultar.Height = Theme.S(40);
            btnConsultar.Click += new EventHandler(OnConsultar);
            tl.Controls.Add(btnConsultar, 1, 1);

            btnPdf = new PrimaryButton();
            btnPdf.Text = "Abrir o PDF";
            btnPdf.Ghost = true;
            btnPdf.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnPdf.Height = Theme.S(40);
            btnPdf.Enabled = false;
            btnPdf.Click += new EventHandler(OnPdf);
            tl.Controls.Add(btnPdf, 2, 1);

            card.Controls.Add(tl);
            return card;
        }

        private Control BuildBanner()
        {
            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, Theme.S(14));
            return banner;
        }

        private Control BuildResultCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 2;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var host = new Panel();
            host.Dock = DockStyle.Fill; host.BackColor = Theme.CardBg;

            grid = NovaGrid(); grid.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill; lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            lblVazio.Text = "Carregando a consulta de kits...";

            host.Controls.Add(grid);
            host.Controls.Add(lblVazio);
            tl.Controls.Add(host, 0, 1);
            tl.SetColumnSpan(host, 2);

            card.Controls.Add(tl);
            return card;
        }

        private GridPro NovaGrid()
        {
            // GridPro já traz double buffer, cabeçalho claro, hover e seleção padrão;
            // aqui ficam só as escolhas próprias desta tela.
            var g = new GridPro();
            g.Dock = DockStyle.Fill;
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;
            // Zebra nas linhas de unidade: num tipo com muitas unidades o olho perdia a
            // linha ao cruzar do código de barras até o botão. As linhas-cabeçalho não
            // mudam: o estilo de LINHA (InfoBg) tem precedência sobre o alternado.
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridZebra;

            g.Columns.Add(Col("kit", "Kit", 300, true, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("barras", "Cód. de barras", 150, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("codkit", "Cód. kit", 100, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("qtd", "Qtd", 50, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(BtnCol("copiar", "", 46));      // glifo "Copy" (Segoe MDL2 Assets)
            g.Columns.Add(BtnCol("reimprimir", "", 46));  // glifo "Print"
            g.CellContentClick += new DataGridViewCellEventHandler(Grid_CellContentClick);
            return g;
        }

        // Coluna de botão só com ícone (glifo do Segoe MDL2 Assets); a dica de texto
        // (tooltip) é posta por linha, só nas unidades — as linhas-cabeçalho não têm botão.
        private DataGridViewButtonColumn BtnCol(string name, string glyph, int w)
        {
            var c = new DataGridViewButtonColumn();
            c.Name = name; c.HeaderText = ""; c.Width = Theme.S(w);
            c.Text = glyph; c.UseColumnTextForButtonValue = true;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.FlatStyle = FlatStyle.Flat;
            c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            c.DefaultCellStyle.Font = Theme.Icon;
            c.DefaultCellStyle.Padding = new Padding(Theme.S(4), 3, Theme.S(4), 3);
            c.DefaultCellStyle.ForeColor = Theme.Primary;
            c.DefaultCellStyle.SelectionForeColor = Theme.Primary;
            // mesma cor de seleção das demais células: cor diferente aqui abria dois
            // "buracos" claros na faixa da linha selecionada
            c.DefaultCellStyle.SelectionBackColor = Theme.SelBg;
            return c;
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int w, bool fill, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = Theme.S(w);
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            // Cabeçalho acompanha a célula: coluna centralizada com título à esquerda
            // continuava parecendo torta.
            if (align == DataGridViewContentAlignment.MiddleCenter)
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            // A coluna elástica não encolhe até sumir: se faltar espaço de verdade,
            // a grade mostra a barra horizontal em vez de cortar os botões de ação.
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.MinimumWidth = Theme.S(160); }
            return c;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            // ao abrir a aba, já consulta (se houver sessão)
            if (_host.Session != null) OnConsultar(this, EventArgs.Empty);
            else
            {
                banner.SetState(StatusKind.Warning, "Faça login para consultar os kits em estoque.");
                // sem sessão nada está carregando — o "Carregando..." do construtor não pode ficar
                lblVazio.Text = "Faça login para ver os kits em estoque.";
            }
        }

        private async void OnConsultar(object sender, EventArgs e)
        {
            if (_consultando || _reimprimindo) return;   // uma conversa com o MV por vez
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }

            _consultando = true;
            btnConsultar.Enabled = false; btnConsultar.Text = "Consultando...";
            btnPdf.Enabled = false; _reportUrl = null;
            grid.Rows.Clear(); grid.Visible = false; lblVazio.Visible = true;
            lblVazio.Text = "Consultando os kits no servidor...";
            banner.SetState(StatusKind.Busy, "Gerando o relatório de kits (produto " + Produto + ") e lendo os dados...");

            bool tenho = false;
            try
            {
                // Portão único: o monitor de solicitações também conversa com o MV em segundo
                // plano, e o servidor não aceita duas conversas na mesma sessão.
                tenho = await MvGate.EntrarAsync("consultar-kits", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                {
                    banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente de novo em instantes.");
                    lblVazio.Text = "Servidor ocupado. Clique em “Atualizar consulta”.";
                    return;
                }

                var res = await MorphisClient.ConsultarKitsCompletoAsync(_host.Session, Estoque, Produto);
                if (IsDisposed || Disposing) return;

                _reportUrl = res.ReportUrl;
                btnPdf.Enabled = (_reportUrl != null);

                foreach (var kit in res.Kits)
                {
                    int h = grid.Rows.Add(kit.CdKit + " - " + kit.Nome, "", "", kit.Quantidade);
                    var hr = grid.Rows[h];
                    var hs = hr.DefaultCellStyle;
                    hs.BackColor = Theme.InfoBg; hs.Font = Theme.BodyBold; hs.ForeColor = Theme.TextDark;
                    // seleção visível também aqui: pintada igual ao fundo, a seleção "sumia"
                    // ao clicar no nome do kit ou ao passar por ele com a seta
                    hs.SelectionBackColor = Theme.SelBg; hs.SelectionForeColor = Theme.TextDark;
                    // linha-cabeçalho do tipo: sem botões (troca as células de botão por texto vazio)
                    hr.Cells["copiar"] = new DataGridViewTextBoxCell();
                    hr.Cells["reimprimir"] = new DataGridViewTextBoxCell();
                    hr.Cells["copiar"].Style.BackColor = Theme.InfoBg;
                    hr.Cells["reimprimir"].Style.BackColor = Theme.InfoBg;
                    // unidades: cada uma com seus botões de copiar / reimprimir (só ícone + tooltip)
                    foreach (var u in kit.Unidades)
                    {
                        int r = grid.Rows.Add("", u.CodBarras, u.CodKit, "");
                        grid.Rows[r].Cells["copiar"].ToolTipText = "Copiar o código de barras";
                        grid.Rows[r].Cells["reimprimir"].ToolTipText = "Reimprimir a etiqueta";
                    }
                }

                bool tem = res.Kits.Count > 0;
                grid.Visible = tem; lblVazio.Visible = !tem;
                lblVazio.Text = "Nenhum kit em estoque na Farmácia Central.";
                grid.LimparSelecao();

                if (tem)
                    banner.SetState(StatusKind.Success,
                        "Atualizado às " + DateTime.Now.ToString("HH:mm:ss") + " (hoje).");
                else
                    banner.SetState(StatusKind.Warning, "Nenhum kit em estoque na Farmácia Central.");
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();   // volta para o login
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                banner.SetState(StatusKind.Error, "Erro ao consultar os kits: " + ex.Message);
                lblVazio.Text = "Falha na consulta. Tente “Atualizar consulta”.";
            }
            finally
            {
                if (tenho) MvGate.Sair();
                _consultando = false;
                if (!IsDisposed && !Disposing) { btnConsultar.Enabled = true; btnConsultar.Text = "Atualizar consulta"; }
            }
        }

        private void OnPdf(object sender, EventArgs e)
        {
            if (_reportUrl == null) return;
            try { Navegador.Abrir(_reportUrl); }   // abre o PDF numa aba (Cent Browser -> Edge -> padrão)
            catch (Exception ex)
            {
                banner.SetState(StatusKind.Warning, "Não foi possível abrir o PDF no navegador: " + ex.Message + "  URL: " + _reportUrl);
            }
        }

        // Clique nos botões "Copiar barras" / "Reimprimir etiq." de uma unidade.
        private void Grid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col != "copiar" && col != "reimprimir") return;
            var cel = grid.Rows[e.RowIndex].Cells["barras"];
            string barras = (cel != null && cel.Value != null) ? cel.Value.ToString() : "";
            if (barras.Length == 0) return;   // linha-cabeçalho do tipo (sem código de barras)

            if (col == "copiar") CopiarBarras(barras);
            else OnReimprimir(barras);
        }

        private void CopiarBarras(string barras)
        {
            try { Clipboard.SetText(barras); banner.SetState(StatusKind.Info, "Código de barras copiado: " + barras); }
            catch (Exception ex) { banner.SetState(StatusKind.Warning, "Não foi possível copiar o código: " + ex.Message); }
        }

        // Reimprime a etiqueta do kit pelo código de barras: gera o ZPL no servidor e envia ao LAS.
        private async void OnReimprimir(string barras)
        {
            // trava de reentrância: sem ela, dois cliques esperavam o portão em fila
            // e saíam DUAS etiquetas físicas na Zebra; a consulta também segura a vez
            if (_reimprimindo || _consultando) return;
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            _reimprimindo = true;
            banner.SetState(StatusKind.Busy, "Gerando a etiqueta do kit (código de barras " + barras + ")...");
            bool tenho = false;
            try
            {
                tenho = await MvGate.EntrarAsync("reimprimir-etiqueta", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                { banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente de novo em instantes."); return; }

                var etq = await MorphisClient.GerarReimpressaoAsync(_host.Session, barras);
                if (IsDisposed || Disposing) return;

                var imp = await LasClient.DescobrirImpressoraAsync();
                if (IsDisposed || Disposing) return;
                if (imp == null)
                {
                    banner.SetState(StatusKind.Warning,
                        "Etiqueta gerada, mas não imprimiu: " + LasClient.ExplicarFalha());
                    return;
                }
                await LasClient.EnviarZplAsync(imp, etq.Zpl, etq.PrinterId, etq.Copies);
                if (IsDisposed || Disposing) return;
                banner.SetState(StatusKind.Success, "Etiqueta do kit " + barras + " reenviada à impressora.");
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();   // volta para o login
                return;
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Error, "Falha ao reimprimir a etiqueta: " + ex.Message);
            }
            finally { if (tenho) MvGate.Sair(); _reimprimindo = false; }
        }

        // ===== comandos globais (F5 / Ctrl+F), roteados pela MainWindow =====

        public void ComandoAtualizar()
        {
            OnConsultar(this, EventArgs.Empty);   // as travas de reentrância seguram o F5 repetido
        }

        public void ComandoFocarBusca()
        {
            // esta tela não tem campo de busca
        }
    }
}
