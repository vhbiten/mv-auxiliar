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
    public class ConsultarKitsView : UserControl
    {
        private readonly MainWindow _host;
        private PrimaryButton btnConsultar;
        private PrimaryButton btnPdf;
        private StatusBanner banner;
        private DataGridView grid;
        private Label lblVazio;
        private Label lblResumo;
        private string _reportUrl;

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
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); // cartão de ação
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
            card.Margin = new Padding(0, 0, 0, 14);
            card.Padding = new Padding(20, 16, 20, 16);

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 3;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titulo = new Label();
            titulo.Text = "Kits em estoque"; titulo.Font = Theme.H3; titulo.ForeColor = Theme.TextDark;
            titulo.BackColor = Theme.CardBg; titulo.AutoSize = true;
            titulo.Margin = new Padding(0, 0, 0, 4);
            tl.Controls.Add(titulo, 0, 0);
            tl.SetColumnSpan(titulo, 3);

            var sub = new Label();
            sub.Text = "Todos os kits em estoque na Farmácia Central.";
            sub.Font = Theme.Body; sub.ForeColor = Theme.TextSoft;
            sub.BackColor = Theme.CardBg; sub.AutoSize = true;
            sub.Margin = new Padding(0, 0, 0, 0);
            tl.Controls.Add(sub, 0, 1);

            btnConsultar = new PrimaryButton();
            btnConsultar.Text = "Atualizar consulta";
            btnConsultar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnConsultar.Height = 40;
            btnConsultar.Click += new EventHandler(OnConsultar);
            tl.Controls.Add(btnConsultar, 1, 1);

            btnPdf = new PrimaryButton();
            btnPdf.Text = "Abrir o PDF";
            btnPdf.Ghost = true;
            btnPdf.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnPdf.Height = 40;
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
            banner.Margin = new Padding(0, 0, 0, 14);
            return banner;
        }

        private Control BuildResultCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(18, 14, 18, 16);

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 2;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var titulo = new Label();
            titulo.Text = "Resultado"; titulo.Font = Theme.H3; titulo.ForeColor = Theme.TextDark;
            titulo.BackColor = Theme.CardBg; titulo.AutoSize = true;
            titulo.Margin = new Padding(0, 0, 0, 10);
            tl.Controls.Add(titulo, 0, 0);

            lblResumo = new Label();
            lblResumo.AutoSize = true; lblResumo.Font = Theme.BodyBold; lblResumo.ForeColor = Theme.TextSoft;
            lblResumo.BackColor = Theme.CardBg; lblResumo.Anchor = AnchorStyles.Right;
            lblResumo.Margin = new Padding(0, 2, 0, 0); lblResumo.Text = "";
            tl.Controls.Add(lblResumo, 1, 0);

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

        private DataGridView NovaGrid()
        {
            var g = new DataGridView();
            g.Dock = DockStyle.Fill; g.BackgroundColor = Color.White; g.BorderStyle = BorderStyle.None;
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
            g.ColumnHeadersHeight = 36; g.RowTemplate.Height = 30;
            g.DefaultCellStyle.Padding = new Padding(8, 0, 6, 0);
            g.DefaultCellStyle.SelectionBackColor = Theme.Light;
            g.DefaultCellStyle.SelectionForeColor = Theme.TextDark;
            g.DefaultCellStyle.ForeColor = Theme.TextDark;

            g.Columns.Add(Col("kit", "Kit", 300, true, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("barras", "Cód. de barras", 150, false, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("codkit", "Cód. kit", 100, false, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("qtd", "Qtd", 50, false, DataGridViewContentAlignment.MiddleRight));
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
            c.Name = name; c.HeaderText = ""; c.Width = w;
            c.Text = glyph; c.UseColumnTextForButtonValue = true;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.FlatStyle = FlatStyle.Flat;
            c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            c.DefaultCellStyle.Font = Theme.Icon;
            c.DefaultCellStyle.Padding = new Padding(4, 3, 4, 3);
            c.DefaultCellStyle.ForeColor = Theme.Primary;
            c.DefaultCellStyle.SelectionForeColor = Theme.Primary;
            c.DefaultCellStyle.BackColor = Color.White;
            c.DefaultCellStyle.SelectionBackColor = Theme.GridZebra;
            return c;
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int w, bool fill, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = w;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            return c;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            // ao abrir a aba, já consulta (se houver sessão)
            if (_host.Session != null) OnConsultar(this, EventArgs.Empty);
            else banner.SetState(StatusKind.Warning, "Faça login para consultar os kits em estoque.");
        }

        private async void OnConsultar(object sender, EventArgs e)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }

            btnConsultar.Enabled = false; btnConsultar.Text = "Consultando...";
            btnPdf.Enabled = false; _reportUrl = null;
            grid.Rows.Clear(); grid.Visible = false; lblVazio.Visible = true;
            lblVazio.Text = "Consultando os kits no servidor..."; lblResumo.Text = "";
            banner.SetState(StatusKind.Busy, "Gerando o relatório de kits (produto " + Produto + ") e lendo os dados...");

            try
            {
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
                    hs.SelectionBackColor = Theme.InfoBg; hs.SelectionForeColor = Theme.TextDark;
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
                lblVazio.Text = "Nenhum kit em estoque para o produto " + Produto + ".";
                grid.ClearSelection();
                lblResumo.Text = res.TotalUnidades + " kits  ·  " + res.Kits.Count + " tipos";

                if (tem)
                    banner.SetState(StatusKind.Success,
                        res.TotalUnidades + " kits em estoque na Farmácia Central.");
                else
                    banner.SetState(StatusKind.Warning, "Nenhum kit em estoque encontrado para o produto " + Produto + ".");
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
                if (!IsDisposed && !Disposing) { btnConsultar.Enabled = true; btnConsultar.Text = "Atualizar consulta"; }
            }
        }

        private void OnPdf(object sender, EventArgs e)
        {
            if (_reportUrl == null) return;
            try { Navegador.Abrir(_reportUrl); }   // abre o PDF numa aba (Cent Browser -> Edge -> padrão)
            catch (Exception ex)
            {
                banner.SetState(StatusKind.Warning, "Não consegui abrir o PDF no navegador: " + ex.Message + "  URL: " + _reportUrl);
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
            catch (Exception ex) { banner.SetState(StatusKind.Warning, "Não consegui copiar o código: " + ex.Message); }
        }

        // Reimprime a etiqueta do kit pelo código de barras: gera o ZPL no servidor e envia ao LAS.
        private async void OnReimprimir(string barras)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            banner.SetState(StatusKind.Busy, "Gerando a etiqueta do kit (código de barras " + barras + ")...");
            try
            {
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
        }
    }
}
