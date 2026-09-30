using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Módulo "Justificativas": as justificativas de uso de antimicrobiano emitidas no PEP.
    //
    // No MV cada uma é um documento clínico do template 501. O conteúdo preenchido pelo
    // médico (medicamento, posologia, o texto) vive no editor Flash e não sai pela API; o
    // que sai — e é o que a farmácia precisa para controlar — é a EMISSÃO: quando, para
    // qual paciente, em que setor/leito, por qual médico e se já foi assinada.
    // READ-ONLY.
    public class JustificativasView : UserControl, IComandosDeTela
    {
        private readonly MainWindow _host;
        private StyledComboBox cmbPeriodo;
        private PrimaryButton btnAtualizar;
        private TextBox txtBusca;
        private StatusBanner banner;
        private GridPro grid;
        private Label lblVazio;
        private bool _renderPendente;                       // atualização chegou com o mouse sobre a grade
        private DateTime _adiadoDesde = DateTime.MinValue;  // desde quando o redraw está adiado

        // ESTADO COMPARTILHADO: sobrevive à troca de aba (o shell destrói a view a cada
        // visita) e é apagado no logout, porque tem nome de paciente.
        private static readonly List<Justificativa> _itens = new List<Justificativa>();
        private static bool _busy;
        private static DateTime _atualizadoEm;
        private static string _aviso;
        private static bool _atingiuTeto;
        private static int _dias = 7;   // período em vigor; o monitor usa o último escolhido
        private bool _restaurando;      // suprime o handler do combo ao reabrir a aba
        private readonly Dictionary<int, bool> _faixaDaLinha = new Dictionary<int, bool>();

        public JustificativasView(MainWindow host)
        {
            _host = host;
            this.Dock = DockStyle.Fill;
            this.BackColor = Theme.ContentBg;
            // Sem Padding próprio: o _content do shell já dá a moldura padrão de todos os
            // módulos — padding aqui somava ao dele e deixava esta tela deslocada das demais.

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            // Coluna única Percent: sem isso a coluna cresce até o preferido dos cartões
            // e o conteúdo pode estourar o viewport à direita.
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 3;
            // AutoSize, não altura fixa: com 100px fixos o conteúdo cabia no meu DPI e era
            // cortado embaixo num DPI maior, porque a fonte cresce mais que a linha.
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            root.Controls.Add(BuildBarra(), 0, 0);
            root.Controls.Add(BuildBanner(), 0, 1);
            root.Controls.Add(BuildResultado(), 0, 2);

            this.Controls.Add(root);
            this.Load += new EventHandler(OnLoad);
        }

        private Control BuildBarra()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(14));
            card.Padding = new Padding(Theme.S(20), Theme.S(14), Theme.S(20), Theme.S(16));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 6;
            tl.RowCount = 2;
            // Quem ESTICA é a coluna da BUSCA. Com uma coluna vazia sobrando no fim, ao
            // apertar a janela ela ia a zero e o TableLayoutPanel cortava a última coluna
            // Absolute — justamente o botão. Agora quem cede é a busca.
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(170))); // período
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(14)));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // busca (elástica)
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(14)));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(164))); // Atualizar
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tl.Controls.Add(Micro("PERÍODO"), 0, 0);
            tl.Controls.Add(Micro("BUSCAR"), 2, 0);

            cmbPeriodo = new StyledComboBox();
            cmbPeriodo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbPeriodo.Margin = new Padding(0, Theme.S(3), 0, Theme.S(3));
            cmbPeriodo.Items.AddRange(new object[] {
                new OpcaoItem("Hoje", "0"), new OpcaoItem("Últimos 7 dias", "7"),
                new OpcaoItem("Últimos 15 dias", "15"), new OpcaoItem("Últimos 30 dias", "30") });
            cmbPeriodo.SelectedIndex = 1;
            cmbPeriodo.SelectedIndexChanged += new EventHandler(delegate { if (!_busy && !_restaurando) Consultar(); });
            tl.Controls.Add(cmbPeriodo, 0, 1);

            tl.Controls.Add(BuildBusca(), 2, 1);

            btnAtualizar = new PrimaryButton();
            btnAtualizar.Text = "Atualizar";
            btnAtualizar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnAtualizar.Height = Theme.S(40);
            btnAtualizar.Margin = new Padding(0, 0, Theme.S(4), 0);   // nao encostar na borda da celula
            btnAtualizar.Click += new EventHandler(delegate { Consultar(); });
            tl.Controls.Add(btnAtualizar, 4, 1);

            card.Controls.Add(tl);
            return card;
        }

        private Label Micro(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft;
            l.BackColor = Theme.CardBg; l.AutoSize = true; l.Margin = new Padding(2, 0, 0, Theme.S(5));
            return l;
        }

        // Mesmo desenho da busca da aba Solicitações: FieldHost com a lupa à esquerda e o
        // campo sem borda própria. Filtra enquanto se digita, sobre o que já está em memória.
        private Control BuildBusca()
        {
            var hostBusca = new FieldHost();
            hostBusca.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            // Elástico com teto: encolhe se a janela apertar (protegendo o botão de ser
            // cortado) e para de crescer em 420.
            hostBusca.MaximumSize = new Size(Theme.S(420), 0);
            hostBusca.Height = Theme.S(34);
            hostBusca.Margin = new Padding(0, Theme.S(3), 0, Theme.S(3));   // centra contra o botão de 40px
            hostBusca.Padding = new Padding(Theme.S(10), Theme.S(7), Theme.S(8), Theme.S(6));

            var lupa = new Label();
            lupa.Text = "";                       // glifo "Search" (Segoe MDL2 Assets)
            lupa.Font = Theme.Icon; lupa.ForeColor = Theme.TextSoft; lupa.BackColor = Color.White;
            lupa.Dock = DockStyle.Left; lupa.Width = Theme.S(24);
            lupa.TextAlign = ContentAlignment.MiddleLeft;

            txtBusca = new TextBox();
            txtBusca.BorderStyle = BorderStyle.None;
            txtBusca.Font = Theme.Body; txtBusca.ForeColor = Theme.TextDark; txtBusca.BackColor = Color.White;
            txtBusca.Dock = DockStyle.Fill;
            var espera = new System.Windows.Forms.Timer();
            espera.Interval = 180;
            espera.Tick += new EventHandler(delegate { espera.Stop(); RenderGrid(); });
            txtBusca.TextChanged += new EventHandler(delegate { espera.Stop(); espera.Start(); });
            this.Disposed += new EventHandler(delegate { espera.Dispose(); });
            txtBusca.KeyDown += new KeyEventHandler(delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Escape && txtBusca.Text.Length > 0)
                { txtBusca.Text = ""; ke.SuppressKeyPress = true; }
            });
            // Placeholder nativo (some ao digitar): EM_SETCUEBANNER; wParam=1 mantém com foco.
            txtBusca.HandleCreated += new EventHandler(delegate
            {
                SendMessage(txtBusca.Handle, 0x1501, (IntPtr)1, "Buscar paciente, setor ou médico");
            });

            hostBusca.Controls.Add(txtBusca);
            hostBusca.Controls.Add(lupa);
            return hostBusca;
        }

        private Control BuildBanner()
        {
            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, Theme.S(14));
            return banner;
        }

        private Control BuildResultado()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));

            // Sem título — o cartão é a grade, direto (a pedido).
            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1; tl.RowCount = 1;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var host = new Panel();
            host.Dock = DockStyle.Fill; host.BackColor = Theme.CardBg;

            grid = NovaGrid(); grid.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill; lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            lblVazio.Text = "Carregando as justificativas...";

            host.Controls.Add(grid);
            host.Controls.Add(lblVazio);
            tl.Controls.Add(host, 0, 0);

            card.Controls.Add(tl);
            return card;
        }

        private GridPro NovaGrid()
        {
            var g = new GridPro();
            g.Dock = DockStyle.Fill;
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;
            g.DefaultCellStyle.Padding = new Padding(Theme.S(10), 0, Theme.S(8), 0);
            // Rede de segurança: se a janela apertar além do que os mínimos das colunas
            // cabem, rola de lado em vez de amputar coluna — e o botão de abrir fica
            // congelado no início, então a ação nunca sai da vista.
            g.ScrollBars = ScrollBars.Both;

            // Saiu de cima da grade: aplica a atualização que ficou pendente (ver RenderAutomatico).
            g.MouseLeave += new EventHandler(delegate
            {
                if (!_renderPendente) return;
                _renderPendente = false; _adiadoDesde = DateTime.MinValue;
                RenderGrid();
            });

            // Larguras com folga p/ 96 DPI (monitor da farmácia): lá a fonte rende ~8%
            // mais larga que em 125%, e "29/08/2026 14:35" (data), o atendimento de 7
            // dígitos e "Sem assinatura" batiam exatamente na borda — o atendimento
            // chegava a aparecer cortado. O padding desta grade é 10+8 (ver acima).
            g.Columns.Add(Col("data", "Emitida em", 148, false, DataGridViewContentAlignment.MiddleCenter));
            var pac = Col("paciente", "Paciente", 300, true, DataGridViewContentAlignment.MiddleLeft);
            pac.MinimumWidth = Theme.S(170); g.Columns.Add(pac);
            // Com as abreviações (UTI/PAM), o setor precisa de bem menos: a sobra vai
            // para paciente e médico, que são os textos que realmente truncavam.
            var loc = Col("local", "Setor", 120, true, DataGridViewContentAlignment.MiddleLeft);
            loc.MinimumWidth = Theme.S(90); loc.FillWeight = 30; g.Columns.Add(loc);
            // O médico aparece na própria linha: a busca casa por ele, e o resultado precisa
            // mostrar por que a linha casou (antes ele vivia só no tooltip do paciente).
            var med = Col("medico", "Médico", 220, true, DataGridViewContentAlignment.MiddleLeft);
            med.MinimumWidth = Theme.S(130); med.FillWeight = 90;
            med.DefaultCellStyle.ForeColor = Theme.TextSoft;
            g.Columns.Add(med);
            g.Columns.Add(Col("atend", "Atend.", 92, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("situacao", "Situação", 138, false, DataGridViewContentAlignment.MiddleCenter));

            // Botão de abrir a justificativa (mesmo padrão da aba Solicitações: glifo do
            // Segoe MDL2 numa coluna estreita, sem texto).
            var abrir = new DataGridViewButtonColumn();
            abrir.Name = "abrir"; abrir.HeaderText = ""; abrir.Width = Theme.S(46);
            abrir.Text = ""; abrir.UseColumnTextForButtonValue = true;   // impressora
            abrir.SortMode = DataGridViewColumnSortMode.NotSortable;
            abrir.FlatStyle = FlatStyle.Flat;
            abrir.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            abrir.DefaultCellStyle.Font = Theme.Icon;
            abrir.DefaultCellStyle.Padding = new Padding(Theme.S(4), Theme.S(3), Theme.S(4), Theme.S(3));
            abrir.DefaultCellStyle.ForeColor = Theme.Primary;
            abrir.DefaultCellStyle.SelectionForeColor = Theme.Primary;
            abrir.DefaultCellStyle.SelectionBackColor = Theme.SelBg;   // sem "furo" claro na linha selecionada
            g.Columns.Add(abrir);

            // Última coluna, a pedido (antes ficava colada à data via DisplayIndex e
            // congelada). Os valores de Rows.Add seguem a ordem FÍSICA das colunas.

            g.Columns["atend"].ToolTipText = "Atendimento — a mesma chave da aba Antimicrobianos";
            g.Columns["situacao"].ToolTipText = "Assinada = o médico assinou; o PDF abre direto aqui."
                + " Sem assinatura = documento pronto mas não assinado; nesse caso o PDF só sai pelo PEP.";
            g.Columns["abrir"].ToolTipText = "Abre o PDF da justificativa no navegador";

            g.CellFormatting += new DataGridViewCellFormattingEventHandler(Grid_Format);
            g.CellContentClick += new DataGridViewCellEventHandler(Grid_Click);
            return g;
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
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            return c;
        }

        private void Grid_Format(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var j = grid.Rows[e.RowIndex].Tag as Justificativa;
            if (j == null) return;

            bool faixa;
            if (_faixaDaLinha.TryGetValue(e.RowIndex, out faixa) && faixa)
            { e.CellStyle.BackColor = Theme.GridZebra; e.CellStyle.SelectionBackColor = Theme.SelBg; }

            string col = grid.Columns[e.ColumnIndex].Name;
            if (col == "paciente") e.CellStyle.Font = Theme.BodyBold;
            else if (col == "atend" || col == "medico") e.CellStyle.ForeColor = Theme.TextSoft;

            // Rascunho que o médico não assinou: para a farmácia, justificativa não concluída.
            if (j.Pendente && col == "situacao")
            {
                e.CellStyle.BackColor = Theme.WarningBg;
                e.CellStyle.SelectionBackColor = Theme.WarningBg;
                e.CellStyle.ForeColor = Theme.Warning;
                e.CellStyle.SelectionForeColor = Theme.Warning;
                e.CellStyle.Font = Theme.BodyBold;
            }
        }

        // Abrir a justificativa: baixa o PDF do MV e entrega ao navegador.
        //
        // Não há rota REST para o documento renderizado — o PDF sai do jasperServlet,
        // o mesmo que o Flash aciona no "imprimir" do PEP. Ver PepClient.
        // BaixarJustificativaPdfAsync. Documento sem assinatura não rende folha, e é
        // isso que explicamos aqui em vez de deixar o clique sem resposta.
        private async void Grid_Click(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "abrir") return;
            var j = grid.Rows[e.RowIndex].Tag as Justificativa;
            if (j == null) return;
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }

            string quem = NomeProprio(j.Paciente);
            // Busy, não Info: é uma espera de verdade — a grade fica cinza enquanto baixa.
            banner.SetState(StatusKind.Busy, "Buscando a justificativa de " + quem + " no MV...");
            grid.Enabled = false;
            try
            {
                byte[] pdf = await PepClient.BaixarJustificativaPdfAsync(_host.Session, j.Id);

                // Assinada o servlet resolve sozinho. Sem assinatura ele exige o
                // cdEditorRegistro, que vive do lado do editor — PepAmf vai atrás.
                if (pdf == null)
                {
                    banner.SetState(StatusKind.Busy, "Localizando a justificativa de " + quem + " no MV...");
                    int registro = await PepAmf.ResolverRegistroAsync(
                        _host.Session, j.PacienteId, j.Id, j.Atendimento);
                    if (registro > 0)
                        pdf = await PepClient.BaixarJustificativaPdfAsync(_host.Session, j.Id, registro);
                }

                if (pdf == null)
                {
                    // Sobra o caso do antimicrobiano que saiu da prescrição vigente: o item
                    // de onde o registro vem deixa de ser alcançável por fora do PEP.
                    Navegador.Abrir(AppConfig.Host + "/mvpep/");
                    banner.SetState(StatusKind.Warning, "Esta justificativa é de um antimicrobiano que não "
                        + "está mais na prescrição vigente — nesse caso o MV só imprime pelo próprio PEP, "
                        + "que abri no navegador. " + quem + ", atendimento " + j.Atendimento + ".");
                    return;
                }

                string url = PdfEfemero.Publicar(pdf, "justificativa-" + j.Id + ".pdf");
                Navegador.Abrir(url);
                banner.SetState(StatusKind.Success, "Justificativa de " + quem + " aberta — atendimento "
                    + j.Atendimento + ", emitida em " + j.Data.ToString("dd/MM/yyyy HH:mm") + ".");
            }
            catch (Exception ex)
            {
                banner.SetState(StatusKind.Error, "Não foi possível abrir a justificativa: " + ex.Message);
            }
            finally { grid.Enabled = true; }
        }

        private void OnLoad(object sender, EventArgs e)
        {
            _viva = this;
            this.Disposed += new EventHandler(delegate { if (_viva == this) _viva = null; });

            if (_host.Session == null)
            {
                if (_host.Usuario == "PREVIEW") { CarregarExemplo(); return; }
                banner.SetState(StatusKind.Warning, "Faça login para ver as justificativas.");
                lblVazio.Text = "Faça login para ver as justificativas.";
                return;
            }

            // Reabre no mesmo período de antes (o monitor seguiu buscando por ele) e mostra
            // a foto na hora; só busca se ainda não houver nada em memória.
            int i = IndiceDoPeriodo(_dias);
            if (i >= 0 && i != cmbPeriodo.SelectedIndex) { _restaurando = true; cmbPeriodo.SelectedIndex = i; _restaurando = false; }

            IniciarMonitor(_host);
            if (_itens.Count > 0 && _atualizadoEm.Date == DateTime.Today) { RenderGrid(); MostrarBanner(); }
            else if (!_busy) Consultar();
        }

        private int IndiceDoPeriodo(int dias)
        {
            for (int i = 0; i < cmbPeriodo.Items.Count; i++)
            {
                var op = cmbPeriodo.Items[i] as OpcaoItem;
                int d;
                if (op != null && int.TryParse(op.Valor, out d) && d == dias) return i;
            }
            return -1;
        }

        private void Consultar() { Consultar(false); }

        private int DiasSelecionados()
        {
            var op = cmbPeriodo.SelectedItem as OpcaoItem;
            int d;
            return (op != null && int.TryParse(op.Valor, out d)) ? d : 7;
        }

        private async void Consultar(bool automatico)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            if (_busy)
            {
                // Já tem varredura em curso (em geral a automática): o clique não pode
                // morrer mudo, senão o botão parece quebrado.
                banner.SetState(StatusKind.Busy, "Atualização em andamento — a lista se renova em instantes.");
                return;
            }
            _busy = true;
            // Atualização automática não desmonta a tela: apagar a lista e escrever
            // "Carregando..." por cima de quem está lendo seria pior que não atualizar.
            // Na automática só o botão avisa: travar a busca desabilitaria o campo no meio
            // da digitação de quem está usando a tela. A busca só filtra o que já está em
            // memória, então não há corrida com a consulta.
            Travar(automatico ? "Atualizando..." : "Buscando...", automatico);
            if (!automatico)
            {
                banner.SetState(StatusKind.Busy, "Lendo as justificativas no MV Soul...");
                grid.Visible = false; lblVazio.Visible = true;
                lblVazio.Text = "Carregando as justificativas...";
            }

            int sessao = _sessaoId;
            try
            {
                _dias = DiasSelecionados();
                await VarrerAsync(_host.Session, _dias, sessao);
                if (IsDisposed || Disposing || sessao != _sessaoId) return;
                RenderGrid();
                MostrarBanner();
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                banner.SetState(StatusKind.Error, "Erro ao ler as justificativas: " + ex.Message);
                lblVazio.Text = "Falha na busca. Tente “Atualizar”.";
            }
            finally
            {
                _busy = false;
                if (!IsDisposed && !Disposing) Destravar();
            }
        }

        // "leve" (atualização automática): libera só a BUSCA, que filtra o que já está em
        // memória. O período continua travado mesmo assim — ele dispara consulta, e trocá-lo
        // no meio de uma atualização seria engolido pelo _busy, deixando a lista de um
        // período sob o rótulo de outro.
        // ESTÁTICA: precisa rodar com a aba fechada, quando não existe instância desta view.
        private static async Task VarrerAsync(Session sessao_, int dias, int sessao)
        {
            // O PEP é outra sessão, fora do portão do Morphis: não disputa com as outras abas.
            var v = await PepClient.ListarJustificativasAsync(
                sessao_, DateTime.Today.AddDays(-dias), DateTime.Today);
            if (sessao != _sessaoId) return;

            _itens.Clear(); _itens.AddRange(v.Itens);
            _atualizadoEm = DateTime.Now;
            _aviso = v.Aviso;
            _atingiuTeto = v.AtingiuTeto;
        }

        private void MostrarBanner()
        {
            string periodo = _dias == 0 ? "hoje" : "últimos " + _dias + " dias";
            if (!string.IsNullOrEmpty(_aviso)) banner.SetState(StatusKind.Error, _aviso);
            else if (_atingiuTeto)
                banner.SetState(StatusKind.Warning, "Há mais justificativas no período do que consegui trazer. "
                    + "Escolha um período menor.");
            else if (_itens.Count == 0)
                banner.SetState(StatusKind.Info, "Nenhuma justificativa emitida (" + periodo + ").");
            else
                banner.SetState(StatusKind.Success, "Atualizado às " + _atualizadoEm.ToString("HH:mm:ss") + " (hoje).");
        }

        // ===== MONITOR EM SEGUNDO PLANO =====
        // Mesma razão da aba Antimicrobianos: o shell destrói a view ao trocar de módulo, então
        // o timer tem que ser estático para o dado continuar fresco. Aqui é barato — 1,8 s no
        // PEP, sem passar pelo portão do Morphis.
        public const int IntervaloMonitorMs = 180000;   // 3 minutos

        private static System.Windows.Forms.Timer _monitor;
        private static MainWindow _monitorHost;
        private static int _sessaoId;
        private static JustificativasView _viva;

        public static void IniciarMonitor(MainWindow host)
        {
            _monitorHost = host;
            if (_monitor == null)
            {
                _monitor = new System.Windows.Forms.Timer();
                _monitor.Interval = IntervaloMonitorMs;
                _monitor.Tick += new EventHandler(MonitorTick);
            }
            if (_monitor.Enabled) return;
            _monitor.Start();
            Logger.Log("Justificativas: monitor em segundo plano ligado (a cada "
                + (IntervaloMonitorMs / 1000) + "s).");
        }

        public static void PararMonitor()
        {
            bool estava = _monitor != null && _monitor.Enabled;
            if (_monitor != null) _monitor.Stop();
            _monitorHost = null;
            _sessaoId++;
            _itens.Clear();                 // tem nome de paciente: não sobrevive ao logout
            _atualizadoEm = DateTime.MinValue;
            _aviso = null; _atingiuTeto = false;
            if (estava) Logger.Log("Justificativas: monitor em segundo plano desligado.");
        }

        private static async void MonitorTick(object sender, EventArgs e)
        {
            if (_monitorHost == null || _monitorHost.Session == null) return;
            if (_busy) return;
            if (_monitorHost.WindowState == FormWindowState.Minimized) return;

            _monitor.Stop();
            int sessao = _sessaoId;
            _busy = true;
            // Sinal de vida na tela (se ela estiver aberta): o botão vira "Atualizando..."
            // durante a varredura de fundo — sem isso a grade se redesenhava "sozinha" e o
            // clique em Atualizar nesse intervalo parecia engolido.
            var vAntes = _viva;
            if (vAntes != null && !vAntes.IsDisposed && !vAntes.Disposing)
                vAntes.Travar("Atualizando...", true);
            try
            {
                await VarrerAsync(_monitorHost.Session, _dias, sessao);
                if (sessao != _sessaoId) return;
                var v = _viva;
                if (v != null && !v.IsDisposed && !v.Disposing) { v.RenderAutomatico(); v.MostrarBanner(); }
            }
            catch (SessaoExpiradaException)
            {
                Logger.Log("Justificativas: sessão expirada — monitor parado.");
                if (sessao == _sessaoId) PararMonitor();
                return;
            }
            catch (Exception ex) { Logger.Log("Justificativas: falha no monitor: " + ex.Message); }
            finally
            {
                _busy = false;
                var vFim = _viva;
                if (vFim != null && !vFim.IsDisposed && !vFim.Disposing) vFim.Destravar();
                if (sessao == _sessaoId && _monitor != null && _monitorHost != null) _monitor.Start();
            }
        }

        private void Travar(string texto, bool leve)
        {
            btnAtualizar.Enabled = false; btnAtualizar.Text = texto;
            cmbPeriodo.Enabled = false;
            if (leve) return;
            txtBusca.Enabled = false;
        }
        private void Destravar()
        { btnAtualizar.Enabled = true; btnAtualizar.Text = "Atualizar"; txtBusca.Enabled = true; cmbPeriodo.Enabled = true; }

        // Atalhos globais (F5 / Ctrl+F) roteados pela MainWindow para a tela ativa.
        public void ComandoAtualizar() { Consultar(); }   // Consultar já respeita o _busy
        public void ComandoFocarBusca()
        {
            if (txtBusca != null && txtBusca.Enabled) { txtBusca.Focus(); txtBusca.SelectAll(); }
        }

        // Caminho do MONITOR: não redesenhar com o MOUSE EM CIMA da grade. O clique no
        // botão de abrir é resolvido pela linha sob o cursor, e uma justificativa nova
        // chegando entre o apontar e o clicar deslocaria tudo um índice — abriria o PDF
        // de OUTRO paciente. Fica pendente e desenha ao sair da grade (com teto de tempo,
        // para o mouse esquecido ali não congelar a tela para sempre).
        private void RenderAutomatico()
        {
            if (grid.Visible && MouseSobreAGrade())
            {
                if (!_renderPendente) { _renderPendente = true; _adiadoDesde = DateTime.Now; }
                if ((DateTime.Now - _adiadoDesde).TotalMinutes < 2) return;
            }
            _renderPendente = false; _adiadoDesde = DateTime.MinValue;
            RenderGrid();
        }

        private bool MouseSobreAGrade()
        {
            try
            {
                return grid.IsHandleCreated
                    && grid.ClientRectangle.Contains(grid.PointToClient(Cursor.Position));
            }
            catch { return false; }
        }

        // Mais recentes em cima; a zebra alterna por DIA, que é como a farmácia lê a lista.
        private void RenderGrid()
        {
            string busca = Normalizar(txtBusca != null ? txtBusca.Text : "");
            var mostrar = new List<Justificativa>();
            foreach (var j in _itens)
                if (busca.Length == 0
                    || Normalizar(j.Paciente).Contains(busca)
                    || Normalizar(j.Local).Contains(busca)
                    || Normalizar(j.Medico).Contains(busca)
                    || j.Atendimento.ToString().Contains(busca)) mostrar.Add(j);

            mostrar.Sort(delegate(Justificativa a, Justificativa b) { return b.Data.CompareTo(a.Data); });

            // A atualização automática redesenha a grade inteira; sem guardar o topo, a
            // lista saltaria para o começo de 3 em 3 minutos sob os olhos de quem lê.
            int topo = 0;
            try { if (grid.Rows.Count > 0 && grid.FirstDisplayedScrollingRowIndex >= 0) topo = grid.FirstDisplayedScrollingRowIndex; }
            catch { topo = 0; }

            // Seleção guardada por CHAVE, nunca por índice: uma justificativa nova no topo
            // desloca todos os índices, e a seleção cairia em outro paciente.
            int idSelecionado = 0;
            if (grid.SelectedRows.Count > 0)
            {
                var jSel = grid.SelectedRows[0].Tag as Justificativa;
                if (jSel != null) idSelecionado = jSel.Id;
            }

            grid.SuspendLayout();
            grid.Rows.Clear();
            _faixaDaLinha.Clear();
            bool faixa = false;
            DateTime diaAnterior = DateTime.MinValue;
            foreach (var j in mostrar)
            {
                if (j.Data.Date != diaAnterior) { faixa = !faixa; diaAnterior = j.Data.Date; }

                int r = grid.Rows.Add(
                    j.Data == DateTime.MinValue ? "—" : j.Data.ToString("dd/MM/yyyy HH:mm"),
                    NomeProprio(j.Paciente),
                    SetorCurto(j.Local),
                    NomeProprio(j.Medico),
                    j.Atendimento > 0 ? j.Atendimento.ToString() : "",
                    j.SituacaoTexto,
                    "");
                var linha = grid.Rows[r];
                linha.Tag = j;
                _faixaDaLinha[r] = faixa;
                // O setor abreviado guarda o nome completo no tooltip (a busca continua
                // enxergando o original, que vem do Tag, não da célula).
                if (SetorCurto(j.Local) != NomeProprio(j.Local))
                    linha.Cells["local"].ToolTipText = NomeProprio(j.Local);
                // Nome completo (sem elipse) e leito no tooltip do paciente; o conselho, no
                // tooltip do médico — a linha mostra só o nome, que é o que se procura.
                linha.Cells["paciente"].ToolTipText = j.Paciente ?? "";
                if (!string.IsNullOrEmpty(j.Leito)) linha.Cells["paciente"].ToolTipText += "\nLeito " + j.Leito;
                if (!string.IsNullOrEmpty(j.Medico)) linha.Cells["medico"].ToolTipText = NomeProprio(j.Medico)
                    + (string.IsNullOrEmpty(j.Conselho) ? "" : " (" + j.Conselho + ")");
                // Completa o texto da situação (que pode vir truncado na coluna) em vez de
                // substituí-lo pelo CID.
                if (!string.IsNullOrEmpty(j.Cid))
                    linha.Cells["situacao"].ToolTipText = j.SituacaoTexto + "\nCID: " + j.Cid;
            }
            grid.ResumeLayout();
            try { if (topo > 0 && topo < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = topo; }
            catch { }

            // Repovoar faz o WinForms "selecionar" a linha 0 sozinho: limpa, e devolve a
            // seleção à MESMA justificativa de antes, se ela ainda estiver na lista.
            grid.LimparSelecao();
            if (idSelecionado != 0)
            {
                for (int i = 0; i < grid.Rows.Count; i++)
                {
                    var jr = grid.Rows[i].Tag as Justificativa;
                    if (jr != null && jr.Id == idSelecionado) { grid.Rows[i].Selected = true; break; }
                }
            }

            grid.Visible = mostrar.Count > 0;
            lblVazio.Visible = mostrar.Count == 0;
            if (mostrar.Count == 0)
                lblVazio.Text = _itens.Count > 0 ? "Nada encontrado para essa busca." : "Nenhuma justificativa para os filtros escolhidos.";
        }

        private void CarregarExemplo()
        {
            _itens.Clear();
            // Atendimentos de 7 dígitos de propósito: é a largura real dos números do MV,
            // e é ela que o laço de screenshot confere na coluna Atend.
            _itens.Add(Ex(1, DateTime.Today.AddHours(14), "SIGNED", "MARIA EXEMPLO DA SILVA", "UTI - LADO A", "UTI - BOX 10", 5300121, "JULIA EXEMPLO ARAUJO", "CRM 11860"));
            _itens.Add(Ex(2, DateTime.Today.AddHours(10).AddMinutes(24), "SIGNED", "JOAO EXEMPLO PEREIRA", "3º ANDAR", "ENF 315 - 315A", 5300102, "GUSTAVO EXEMPLO IANAZE", "CRM 5820"));
            _itens.Add(Ex(3, DateTime.Today.AddHours(8).AddMinutes(34), "OPENED", "ANA EXEMPLO SOUZA", "", 5300093, "FILIPE EXEMPLO CRISTINO", "CRM 15309", "EMERGENCY"));
            _itens.Add(Ex(4, DateTime.Today.AddDays(-1).AddHours(21), "CLOSED", "CARLOS EXEMPLO LIMA", "ALA PEDIATRICA", "ENF PED - D3", 5300084, "LARA EXEMPLO PASSIANOTO", "CRM 16679"));
            _itens.Add(Ex(5, DateTime.Today.AddDays(-1).AddHours(15), "SIGNED", "PEDRO EXEMPLO ROCHA", "4º ANDAR", "ENF 402 - 402B", 5300075, "MARIA EXEMPLO MANTOLVANI", "CRM 11461"));
            _atualizadoEm = DateTime.Now;
            RenderGrid();
            banner.SetState(StatusKind.Info, "Pré-visualização: pacientes fictícios, sem consultar o MV.");
        }

        private static Justificativa Ex(int id, DateTime data, string sit, string pac, string setor, string leito,
            int atend, string medico, string crm)
        {
            return new Justificativa { Id = id, Data = data, Situacao = sit, Paciente = pac, Setor = setor,
                Leito = leito, Atendimento = atend, Medico = medico, Conselho = crm, TipoAtendimento = "INPATIENT" };
        }
        private static Justificativa Ex(int id, DateTime data, string sit, string pac, string setor,
            int atend, string medico, string crm, string tipo)
        {
            return new Justificativa { Id = id, Data = data, Situacao = sit, Paciente = pac, Setor = setor,
                Leito = "", Atendimento = atend, Medico = medico, Conselho = crm, TipoAtendimento = tipo };
        }

        // Abreviações de leitura rápida, SÓ na exibição (mesma regra da aba Solicitações;
        // o nome completo fica no tooltip da célula e a busca lê o dado original do Tag):
        // "UTI - UNIDADE DE TERAPIA INTENSIVA..." vira "UTI"; "PRONTO SOCORRO"/"PRONTO
        // ATENDIMENTO" viram "PAM", que é como a equipe chama.
        private static string SetorCurto(string local)
        {
            string s = (local ?? "").Trim();
            if (s.StartsWith("UTI", StringComparison.OrdinalIgnoreCase)) return "UTI";
            if (s.StartsWith("PRONTO", StringComparison.OrdinalIgnoreCase)) return "PAM";
            return NomeProprio(local);
        }

        // Mesma regra da aba Antimicrobianos: o MV grava tudo em caixa alta.
        private static string NomeProprio(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string[] miudas = { "de", "da", "do", "das", "dos", "e", "di", "du" };
            string[] siglas = { "UTI", "CTI", "PS", "UPA", "CC", "RN", "SUS", "AMB", "HD", "CME", "SR", "ENF", "BOX", "CRM" };
            var sb = new System.Text.StringBuilder(s.Length);
            bool primeira = true;
            foreach (string bruto in s.Trim().Split(' '))
            {
                string p = bruto.Trim();
                if (p.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                string cima = p.ToUpperInvariant();
                if (Array.IndexOf(siglas, cima) >= 0) { sb.Append(cima); primeira = false; continue; }
                if (!System.Text.RegularExpressions.Regex.IsMatch(p, "[A-Za-zÀ-ú]")) { sb.Append(p); continue; }
                string b = p.ToLowerInvariant();
                if (!primeira && Array.IndexOf(miudas, b) >= 0) { sb.Append(b); continue; }
                sb.Append(char.ToUpperInvariant(b[0])).Append(b.Substring(1));
                primeira = false;
            }
            return sb.ToString();
        }

        private static string Normalizar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var d = s.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(d.Length);
            foreach (char c in d)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                    != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().ToUpperInvariant().Trim();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }
}
