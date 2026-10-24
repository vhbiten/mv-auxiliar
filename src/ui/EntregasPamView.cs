using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Módulo "Urgência (PAM)": os atendimentos do Pronto Atendimento e a situação da prescrição
    // de cada paciente — sem prescrição, em elaboração (médico escrevendo), Prescrição Pendente
    // (pedido na farmácia ainda não atendido), Prescrição Atendida — e quem já teve alta. Serve
    // para o atendente saber, antes de descer, se ainda vai sair prescrição de alguém. SÓ LÊ
    // (ver Core.PainelPam); não há regra de "aguardar/descer": a tela só mostra a informação.
    //
    // A linha do paciente só RESUME; um clique nela (ou Enter/Espaço/→) abre o acordeão com os
    // documentos. Cada item é um pedido à farmácia — com a prescrição médica que o gerou na
    // MESMA linha (casados pelo nº da prescrição, ver PainelPam.Itens) — ou uma prescrição que
    // não gerou pedido, descrita pelo que ela tem (exames, cuidados, suspensão de item:
    // PainelPam.Conteudo). Na ponta do item, os ícones abrem no navegador (Cent Browser) a
    // SOLICITAÇÃO (R_SOLSAI_PRO do MV) e a PRESCRIÇÃO MÉDICA (folha oficial do PEP).
    //
    // Viva em segundo plano: um monitor estático (ver MONITOR) relê o PAM a cada minuto desde a
    // 1ª visita até o logout, com a aba aberta ou não; a tela só desenha o que ele leu.
    //
    // Fluidez: a grade é montada num modelo (LinhaGrade) uma vez por atualização; a atualização
    // automática só troca o texto das células quando a lista não mudou (sem piscar, sem perder
    // rolagem, seleção e hover), e o acordeão insere/remove só os itens do paciente, com a
    // altura animada.
    public class EntregasPamView : UserControl, IComandosDeTela
    {
        private const string GlifoAberto = "";        // ChevronDown
        private const string GlifoFechado = "";       // ChevronRight
        private const string GlifoSolicitacao = "";   // prancheta (o mesmo do grupo Solicitações)
        private const string MarcaPrescricao = "Rx";        // só marca a célula: o ícone é desenhado (DesenharFolhaRx)
        private static readonly Font FonteRx = new Font("Segoe UI", 6.25f, FontStyle.Bold);
        private static readonly Font FonteItem = new Font("Segoe UI", 9f, FontStyle.Regular);
        private static readonly Color HoverItem = ColorTranslator.FromHtml("#E8F0F7");    // item sob o mouse (o HoverBg some no fundo do item)
        private static readonly Color RealceIcone = ColorTranslator.FromHtml("#D6E6F4");  // botão do ícone sob o mouse

        private const int AnimacaoMs = 160;     // abrir/fechar o acordeão
        private const int AlturaMinima = 2;     // menor altura de linha que o DataGridView aceita

        private readonly MainWindow _host;
        private StatusBanner banner;
        private GridPro grid;
        private Label lblVazio;
        private bool _abrindo;
        private bool _carregou;     // já mostrou uma leitura
        private List<LinhaPam> _linhas = new List<LinhaPam>();
        // Acordeões abertos, por atendimento: sobrevivem à atualização de 1 min e à troca de
        // aba (estático, apagado no logout).
        private static readonly HashSet<string> _abertos = new HashSet<string>();

        // Ícone sob o mouse (para o realce do botão) e animação do acordeão em curso.
        private int _hoverLinha = -1, _hoverColuna = -1;
        private Timer _anim;
        private List<DataGridViewRow> _animLinhas;
        private bool _animAbrindo;
        private DateTime _animInicio;
        private DataGridViewRow _animPai;

        // Uma linha da grade, calculada UMA vez por atualização. CellFormatting e CellPainting
        // rodam a cada repintura e só leem daqui (antes refaziam o casamento pedido↔prescrição
        // e ordenações a cada célula pintada).
        private class LinhaGrade
        {
            public LinhaPam Linha;          // o paciente (no item, o dono)
            public ItemPam Item;            // null = linha do paciente
            public List<ItemPam> Itens;     // itens do acordeão (só na linha do paciente)
            public string Chave;            // estável entre atualizações: restaura seleção e rolagem
            public object[] Valores;
            public string[] Dicas;          // tooltip por coluna (null/"" = sem)
            public Color CorPrescricao;
            public bool Cinza;              // alta: a linha toda em cinza
            public bool SituacaoDestaque;   // "Médico prescrevendo"
            public bool UltimoItem;         // último item do acordeão: a guia termina no meio
        }

        private static readonly string[] Colunas = {
            "abre", "paciente", "entrada", "permanencia", "prescricao", "situacao", "sol", "presc" };
        private const int CAbre = 0, CPaciente = 1, CEntrada = 2, CPermanencia = 3, CPrescricao = 4,
                          CSituacao = 5, CSol = 6, CPresc = 7;

        public EntregasPamView(MainWindow host)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;
            this.BackColor = Theme.ContentBg;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // grade

            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, Theme.S(14));
            root.Controls.Add(banner, 0, 0);
            root.Controls.Add(BuildResultCard(), 0, 1);

            this.Controls.Add(root);
            this.Load += new EventHandler(OnLoad);
            this.Disposed += new EventHandler(delegate
            {
                if (_viva == this) _viva = null;   // o monitor segue sozinho; só para de desenhar aqui
                if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; }
            });
        }

        private Control BuildResultCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));

            var host = new Panel();
            host.Dock = DockStyle.Fill; host.BackColor = Theme.CardBg;

            grid = NovaGrid(); grid.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill; lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            lblVazio.Text = "Carregando os atendimentos do PAM...";

            host.Controls.Add(grid);
            host.Controls.Add(lblVazio);
            card.Controls.Add(host);
            return card;
        }

        private GridPro NovaGrid()
        {
            var g = new GridPro();
            g.Dock = DockStyle.Fill;
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;
            g.ShowCellToolTips = true;
            // Cabeçalho em uma linha só ("Último pedido" quebrava em duas e a linha engordava).
            g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            // Recuo menor no cabeçalho (6 -> 4 px): os títulos em negrito são mais largos que os
            // dados das colunas de hora/tempo e eram eles que cortavam.
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(Theme.S(4), 0, Theme.S(4), 0);

            g.Columns.Add(ColIcone("abre", "", 36, Theme.TextSoft));   // seta do acordeão (30 cortava o glifo)

            // Três colunas de texto dividem a sobra (o nome com a maior parte); hora, permanência
            // e ícones têm largura fixa. Larguras MEDIDAS (TextRenderer, Segoe UI 10, 96 DPI) para
            // o texto mais largo de cada coluna + recuo. Soma dos mínimos ~962: sem rolagem
            // horizontal numa tela 1366 maximizada, com folga, mesmo com a barra vertical (~1029).
            var paciente = Col("paciente", "Paciente", 300, true, DataGridViewContentAlignment.MiddleLeft);
            paciente.FillWeight = 55; paciente.MinimumWidth = Theme.S(260);   // nome inteiro na maioria; longo corta com "..." e vai no tooltip
            g.Columns.Add(paciente);
            g.Columns.Add(Estreita(Col("entrada", "Entrada", 96, false, DataGridViewContentAlignment.MiddleCenter)));    // "ontem 23:40" = 88
            var permanencia = Estreita(Col("permanencia", "Permanência", 108, false, DataGridViewContentAlignment.MiddleCenter));   // cabeçalho = 96
            permanencia.HeaderCell.ToolTipText = "Tempo desde a entrada no PAM (até a alta, para quem já saiu)";
            g.Columns.Add(permanencia);
            var prescricao = Col("prescricao", "Prescrição", 200, true, DataGridViewContentAlignment.MiddleCenter);
            prescricao.FillWeight = 25; prescricao.MinimumWidth = Theme.S(196);   // "Medicamento sem pedido" negrito = 184
            g.Columns.Add(prescricao);
            var situacao = Col("situacao", "Situação", 180, true, DataGridViewContentAlignment.MiddleLeft);
            situacao.FillWeight = 20; situacao.MinimumWidth = Theme.S(160);   // "Médico prescrevendo" = 140; o motivo da alta vai no tooltip
            g.Columns.Add(situacao);

            // Os dois documentos na ponta, com título curto para dizer qual ícone é qual.
            var sol = ColIcone("sol", "Sol.", 46, Theme.Primary);
            sol.HeaderCell.ToolTipText = "Solicitação: abre o comprovante no navegador";
            g.Columns.Add(sol);
            var presc = ColIcone("presc", "Presc.", 60, Theme.Primary);   // 52 cortava o cabeçalho
            presc.HeaderCell.ToolTipText = "Prescrição médica: abre a folha do PEP no navegador";
            g.Columns.Add(presc);

            g.CellFormatting += new DataGridViewCellFormattingEventHandler(Grid_Format);
            g.CellPainting += new DataGridViewCellPaintingEventHandler(Grid_Pintar);
            g.CellMouseClick += new DataGridViewCellMouseEventHandler(Grid_CellMouseClick);
            g.CellMouseEnter += new DataGridViewCellEventHandler(Grid_CellMouseEnter);
            g.CellMouseLeave += new DataGridViewCellEventHandler(Grid_CellMouseLeave);
            return g;
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int w, bool fill, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = Theme.S(w);
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            c.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            if (align == DataGridViewContentAlignment.MiddleCenter)
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            return c;
        }

        // Coluna centralizada de hora/número: recuo 4+4 em vez de 6+6 (o texto já fica no meio).
        private static DataGridViewTextBoxColumn Estreita(DataGridViewTextBoxColumn c)
        {
            c.DefaultCellStyle.Padding = new Padding(Theme.S(4), 0, Theme.S(4), 0);
            return c;
        }

        // Coluna de glifo (Segoe MDL2): célula de texto em vez de botão, para poder ficar em
        // branco quando não há o que abrir e acompanhar a seleção da linha.
        private DataGridViewTextBoxColumn ColIcone(string name, string header, int w, Color cor)
        {
            var c = Col(name, header, w, false, DataGridViewContentAlignment.MiddleCenter);
            c.DefaultCellStyle.Font = Theme.Icon;
            c.DefaultCellStyle.ForeColor = cor;
            c.DefaultCellStyle.SelectionForeColor = cor;
            c.DefaultCellStyle.Padding = new Padding(0);
            // "Presc." inteiro numa coluna estreita. Padding(0) é o "não definido" do estilo e
            // herdaria os 6+6 px do cabeçalho padrão — por isso 1 px.
            c.HeaderCell.Style.Padding = new Padding(1, 0, 1, 0);
            return c;
        }

        // ===== MODELO DAS LINHAS =====
        private LinhaGrade LinhaDoPaciente(LinhaPam l, DateTime agora)
        {
            var a = l.At;
            var g = new LinhaGrade();
            g.Linha = l;
            g.Itens = PainelPam.Itens(l);
            g.Chave = a.Atendimento;
            g.Cinza = !a.EmAtendimento;
            var estado = l.Estado;
            bool aberto = g.Itens.Count > 0 && _abertos.Contains(a.Atendimento);
            DateTime ate = a.EmAtendimento ? agora : (a.Saida.HasValue ? a.Saida.Value : agora);

            string rotulo = PainelPam.Rotulo(estado);
            Color cor = Theme.TextSoft;
            switch (estado)
            {
                case EstadoPrescricaoPam.Pendente: cor = Theme.Warning; break;
                case EstadoPrescricaoPam.EmElaboracao: cor = Theme.Primary; break;
                case EstadoPrescricaoPam.Atendida:
                    cor = Theme.Success;
                    // Tudo atendido MAS uma prescrição mais nova com medicamento ainda sem pedido:
                    // o resumo não pode dizer "atendida" em verde com isso escondido no acordeão.
                    var pior = FarmaciaNaPrescricao.Nada;
                    foreach (var it in g.Itens)
                        if (it.Pedido == null && it.Prescricao != null)
                        {
                            var fi = PainelPam.Farmacia(it.Prescricao);
                            if (fi == FarmaciaNaPrescricao.SemPedido) pior = fi;
                            else if (fi == FarmaciaNaPrescricao.AguardandoPedido && pior != FarmaciaNaPrescricao.SemPedido) pior = fi;
                        }
                    if (pior == FarmaciaNaPrescricao.AguardandoPedido) { rotulo = "Aguardando pedido"; cor = CorFarmacia(pior); }
                    else if (pior == FarmaciaNaPrescricao.SemPedido) { rotulo = "Medicamento sem pedido"; cor = CorFarmacia(pior); }
                    break;
                case EstadoPrescricaoPam.SemPedido:
                    // Paciente sem nenhum pedido: o que as prescrições dele têm para a farmácia.
                    var f = PainelPam.Farmacia(l);
                    cor = CorFarmacia(f);
                    if (f == FarmaciaNaPrescricao.Nada) rotulo = "Sem itens da farmácia";
                    else if (f == FarmaciaNaPrescricao.AguardandoPedido) rotulo = "Aguardando pedido";
                    else if (f == FarmaciaNaPrescricao.SemPedido) rotulo = "Medicamento sem pedido";
                    break;
            }
            // PEP fora e nada da leitura anterior: "Sem prescrição" seria uma afirmação falsa.
            if (!l.PepLido && !l.PepHerdado && a.EmAtendimento
                && (estado == EstadoPrescricaoPam.SemPrescricao || estado == EstadoPrescricaoPam.SemPedido))
            {
                rotulo = "PEP sem resposta"; cor = Theme.TextSoft;
            }
            g.CorPrescricao = cor;
            g.SituacaoDestaque = l.MedicoPrescrevendo && estado != EstadoPrescricaoPam.EmElaboracao;

            g.Valores = new object[Colunas.Length];
            g.Valores[CAbre] = g.Itens.Count > 0 ? (aberto ? GlifoAberto : GlifoFechado) : "";
            g.Valores[CPaciente] = a.Paciente ?? "";
            g.Valores[CEntrada] = HoraComDia(a.Entrada, agora);
            g.Valores[CPermanencia] = Duracao(ate - a.Entrada);
            g.Valores[CPrescricao] = rotulo;
            g.Valores[CSituacao] = TextoSituacao(l);
            g.Valores[CSol] = ""; g.Valores[CPresc] = "";   // documentos só nos itens do acordeão

            g.Dicas = new string[Colunas.Length];
            if (g.Itens.Count > 0) g.Dicas[CAbre] = aberto ? "Fechar (clique na linha)" : "Ver os pedidos e prescrições (clique na linha)";
            if (a.Entrada.Date != agora.Date) g.Dicas[CEntrada] = "Entrada em " + a.Entrada.ToString("dd/MM 'às' HH:mm");
            // O nº de pedidos saiu da grade (coluna removida a pedido): fica no tooltip da prescrição.
            var dicaPresc = new List<string>();
            if (l.Pedidos.Count > 0)
                dicaPresc.Add(l.Pedidos.Count + (l.Pedidos.Count == 1 ? " pedido" : " pedidos") + " à farmácia"
                    + (l.PedidosPendentes > 0 ? " (" + l.PedidosPendentes + " pendente" + (l.PedidosPendentes == 1 ? ")" : "s)") : ""));
            if (l.PepHerdado && a.EmAtendimento) dicaPresc.Add("PEP sem resposta nesta atualização: prescrições da leitura anterior.");
            else if (!l.PepLido && a.EmAtendimento) dicaPresc.Add("PEP sem resposta nesta atualização.");
            if (dicaPresc.Count > 0) g.Dicas[CPrescricao] = string.Join("\n", dicaPresc.ToArray());
            var dica = new List<string>();
            if (!a.EmAtendimento && Motivo(a.MotivoAlta).Length > 0) dica.Add("Alta " + Motivo(a.MotivoAlta));
            if (!string.IsNullOrEmpty(a.Medico)) dica.Add("Médico: " + a.Medico);
            if (!string.IsNullOrEmpty(a.Destino)) dica.Add(a.Destino);
            if (dica.Count > 0) g.Dicas[CSituacao] = string.Join("\n", dica.ToArray());
            return g;
        }

        // Item do acordeão: "Prescrição (nº 123456)" com os dois ícones quando o pedido veio da
        // prescrição; "Material (nº 123457)" só com o da solicitação; e a prescrição que não
        // gerou pedido, pelo que ela tem ("Exames laboratoriais", "Suspensão de metoprolol"),
        // só com o da prescrição. A coluna Prescrição mostra a situação do pedido ou "Sem itens
        // da farmácia"; Permanência e Situação ficam em branco (são do paciente).
        private LinhaGrade LinhaDoItem(ItemPam f, bool ultimo, DateTime agora)
        {
            var g = new LinhaGrade();
            g.Linha = f.Pai; g.Item = f; g.UltimoItem = ultimo;
            g.Chave = f.Pai.At.Atendimento + (f.Pedido != null ? "|S" + f.Pedido.Numero : "|P" + f.Prescricao.Id);
            g.CorPrescricao = CorItem(f);
            g.Valores = new object[Colunas.Length];
            for (int c = 0; c < Colunas.Length; c++) g.Valores[c] = "";
            g.Valores[CPaciente] = RotuloItem(f);
            g.Valores[CEntrada] = f.Quando > DateTime.MinValue ? HoraCurta(f.Quando, agora) : "";
            g.Valores[CPrescricao] = SituacaoItem(f);
            if (f.Pedido != null) g.Valores[CSol] = GlifoSolicitacao;
            if (f.Prescricao != null) g.Valores[CPresc] = MarcaPrescricao;
            g.Dicas = new string[Colunas.Length];
            g.Dicas[CPaciente] = DicaItem(f);
            if (f.Quando > DateTime.MinValue)   // a coluna é "Entrada" do paciente; no item, a hora é outra
                g.Dicas[CEntrada] = (f.Pedido != null ? "Pedido às " : "Prescrição assinada às ") + f.Quando.ToString("dd/MM HH:mm");
            if (f.Pedido != null) g.Dicas[CSol] = "Abrir a solicitação " + f.Pedido.Numero;
            if (f.Prescricao != null)
            {
                string nr = f.Prescricao.CdPreMed > 0 ? " " + f.Prescricao.CdPreMed : "";
                g.Dicas[CPresc] = "Abrir a prescrição médica" + nr + " (assinada às " + f.Prescricao.Hora.ToString("HH:mm") + ")";
            }
            return g;
        }

        private List<LinhaGrade> Montar()
        {
            var res = new List<LinhaGrade>();
            DateTime agora = DateTime.Now;
            foreach (var l in _linhas)
            {
                var p = LinhaDoPaciente(l, agora);
                res.Add(p);
                if (p.Itens.Count == 0 || !_abertos.Contains(p.Chave)) continue;
                for (int i = 0; i < p.Itens.Count; i++) res.Add(LinhaDoItem(p.Itens[i], i == p.Itens.Count - 1, agora));
            }
            return res;
        }

        // Valores, tooltips, altura e recuo de uma linha da grade.
        private void Aplicar(DataGridViewRow row, LinhaGrade g)
        {
            row.Tag = g;
            for (int c = 0; c < Colunas.Length; c++)
            {
                var cel = row.Cells[c];
                if (!object.Equals(cel.Value, g.Valores[c])) cel.Value = g.Valores[c];
                string d = g.Dicas[c] ?? "";
                if (cel.ToolTipText != d) cel.ToolTipText = d;
            }
            if (g.Item != null)
            {
                // Recuo do item: o texto começa logo depois da guia que sai da seta do paciente.
                var pad = new Padding(Theme.S(8), 0, Theme.S(6), 0);
                if (row.Cells[CPaciente].Style.Padding != pad) row.Cells[CPaciente].Style.Padding = pad;
            }
        }

        private int AlturaPaciente { get { return grid.RowTemplate.Height; } }
        private int AlturaItem { get { return Theme.S(30); } }

        // ===== GRADE: montagem e atualização =====
        // Mesma lista (mesmas chaves, na mesma ordem) = atualização no lugar: só o texto que
        // mudou é trocado (a Permanência anda a cada minuto). Lista diferente = remonta, guardando
        // a rolagem e a linha selecionada.
        private void Render()
        {
            TerminarAnimacao();
            var modelo = Montar();
            bool mesma = modelo.Count == grid.Rows.Count;
            for (int i = 0; mesma && i < modelo.Count; i++)
            {
                var atual = grid.Rows[i].Tag as LinhaGrade;
                if (atual == null || atual.Chave != modelo[i].Chave) mesma = false;
            }

            if (mesma)
            {
                for (int i = 0; i < modelo.Count; i++) Aplicar(grid.Rows[i], modelo[i]);
                grid.Invalidate();   // cores e negrito saem do modelo novo no CellFormatting
            }
            else Remontar(modelo);

            bool tem = _linhas.Count > 0;
            lblVazio.Text = "Nenhum paciente em atendimento no PAM agora.";
            grid.Visible = tem; lblVazio.Visible = !tem;
            if (tem) RecalcularMouse();
        }

        private void Remontar(List<LinhaGrade> modelo)
        {
            string topo = null, selecionado = null;
            try
            {
                int i0 = grid.Rows.Count > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
                if (i0 >= 0 && i0 < grid.Rows.Count) topo = ChaveDe(grid.Rows[i0]);
                if (grid.CurrentRow != null && grid.CurrentRow.Selected) selecionado = ChaveDe(grid.CurrentRow);
            }
            catch { }

            grid.SuspendLayout();
            grid.Rows.Clear();
            _hoverLinha = -1; _hoverColuna = -1;
            foreach (var g in modelo)
            {
                int r = grid.Rows.Add(g.Valores);
                var row = grid.Rows[r];
                if (g.Item != null) row.Height = AlturaItem;
                Aplicar(row, g);
            }
            grid.LimparSelecao();
            grid.ResumeLayout();

            if (modelo.Count == 0) return;
            try
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    string k = ChaveDe(row);
                    if (k == null) continue;
                    if (selecionado != null && k == selecionado) grid.CurrentCell = row.Cells[CPaciente];
                    if (topo != null && k == topo) grid.FirstDisplayedScrollingRowIndex = row.Index;
                }
            }
            catch { }
        }

        private static string ChaveDe(DataGridViewRow row)
        {
            var g = row.Tag as LinhaGrade;
            return g != null ? g.Chave : null;
        }

        // ===== ACORDEÃO =====
        // Abre/fecha só os itens deste paciente: insere (ou remove) as linhas logo abaixo dele
        // e anima a altura de 0 até a cheia em ~160 ms (ease-out), sem remontar a grade.
        // Recebe a LINHA, não o índice: terminar uma animação de fechar em curso remove linhas
        // e deslocaria o índice de quem está abaixo.
        private void Alternar(DataGridViewRow pai)
        {
            TerminarAnimacao();
            if (pai == null || pai.Index < 0) return;
            int indicePaciente = pai.Index;
            var g = pai.Tag as LinhaGrade;
            if (g == null || g.Item != null || g.Itens.Count == 0) return;

            bool abrir = !_abertos.Contains(g.Chave);
            if (abrir) _abertos.Add(g.Chave); else _abertos.Remove(g.Chave);
            g.Valores[CAbre] = abrir ? GlifoAberto : GlifoFechado;
            g.Dicas[CAbre] = abrir ? "Fechar (clique na linha)" : "Ver os pedidos e prescrições (clique na linha)";
            pai.Cells[CAbre].Value = g.Valores[CAbre];
            pai.Cells[CAbre].ToolTipText = g.Dicas[CAbre];

            var linhas = new List<DataGridViewRow>();
            if (abrir)
            {
                DateTime agora = DateTime.Now;
                for (int i = 0; i < g.Itens.Count; i++)
                {
                    var item = LinhaDoItem(g.Itens[i], i == g.Itens.Count - 1, agora);
                    int r = indicePaciente + 1 + i;
                    grid.Rows.Insert(r, item.Valores);
                    var row = grid.Rows[r];
                    row.MinimumHeight = AlturaMinima;
                    row.Height = AlturaMinima;
                    Aplicar(row, item);
                    linhas.Add(row);
                }
            }
            else
            {
                for (int r = indicePaciente + 1; r < grid.Rows.Count; r++)
                {
                    var f = grid.Rows[r].Tag as LinhaGrade;
                    if (f == null || f.Item == null) break;
                    grid.Rows[r].MinimumHeight = AlturaMinima;
                    linhas.Add(grid.Rows[r]);
                }
            }
            if (linhas.Count == 0) return;

            _animLinhas = linhas; _animAbrindo = abrir; _animPai = pai; _animInicio = DateTime.Now;
            if (_anim == null)
            {
                _anim = new Timer();
                _anim.Interval = 15;   // ~60 quadros/s (o Timer do WinForms tem resolução de ~15 ms)
                _anim.Tick += new EventHandler(AnimacaoTick);
            }
            _anim.Start();
        }

        private void AnimacaoTick(object sender, EventArgs e)
        {
            if (_animLinhas == null || IsDisposed) { if (_anim != null) _anim.Stop(); return; }
            double t = (DateTime.Now - _animInicio).TotalMilliseconds / AnimacaoMs;
            if (t >= 1) { TerminarAnimacao(); return; }
            double k = 1 - Math.Pow(1 - t, 3);                  // ease-out cúbico
            double fracao = _animAbrindo ? k : 1 - k;
            int h = Math.Max(AlturaMinima, (int)Math.Round(AlturaItem * fracao));
            foreach (var row in _animLinhas)
                if (row.Index >= 0 && row.Height != h) row.Height = h;
        }

        // Leva a animação em curso ao fim na hora (outro clique, atualização automática).
        private void TerminarAnimacao()
        {
            if (_anim != null) _anim.Stop();
            if (_animLinhas == null) return;
            var linhas = _animLinhas; _animLinhas = null;
            if (_animAbrindo)
            {
                foreach (var row in linhas) if (row.Index >= 0) row.Height = AlturaItem;
                MostrarItens(_animPai, linhas[linhas.Count - 1]);
            }
            else
            {
                _hoverLinha = -1; _hoverColuna = -1;
                for (int i = linhas.Count - 1; i >= 0; i--)
                    if (linhas[i].Index >= 0) grid.Rows.Remove(linhas[i]);
            }
            _animPai = null;
            RecalcularMouse();
        }

        // Acordeão aberto no pé da lista: rola só o necessário para o último item aparecer,
        // sem tirar o paciente da tela.
        private void MostrarItens(DataGridViewRow pai, DataGridViewRow ultimo)
        {
            try
            {
                if (pai == null || pai.Index < 0 || ultimo.Index < 0) return;
                int primeiro = grid.FirstDisplayedScrollingRowIndex;
                int visiveis = grid.DisplayedRowCount(false);
                if (primeiro < 0 || visiveis <= 0) return;
                if (ultimo.Index < primeiro + visiveis) return;
                int novo = Math.Min(pai.Index, ultimo.Index - visiveis + 1);
                if (novo > primeiro) grid.FirstDisplayedScrollingRowIndex = novo;
            }
            catch { }
        }

        // Linha do paciente a que a linha r pertence (ela mesma, se for de paciente).
        private int IndicePaciente(int r)
        {
            while (r > 0)
            {
                var g = grid.Rows[r].Tag as LinhaGrade;
                if (g == null || g.Item == null) break;
                r--;
            }
            return r;
        }

        // Teclado: Enter/Espaço abrem e fecham; → abre; ← fecha (num item, volta ao paciente).
        // Num item, Enter abre a prescrição (ou a solicitação, se for só ela) e Ctrl+Enter a
        // solicitação.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (grid != null && grid.Focused && grid.CurrentRow != null)
            {
                var atual = grid.CurrentRow;
                var g = atual.Tag as LinhaGrade;
                if (g != null)
                {
                    bool aberto = _abertos.Contains(g.Linha.At.Atendimento);
                    bool paciente = g.Item == null;
                    if (!paciente && keyData == (Keys.Control | Keys.Enter) && g.Item.Pedido != null)
                    {
                        AbrirSolicitacao(g.Item.Pedido, g.Linha); return true;
                    }
                    if (!paciente && keyData == Keys.Enter)
                    {
                        if (g.Item.Prescricao != null) AbrirPrescricao(g.Item.Prescricao, g.Linha);
                        else if (g.Item.Pedido != null) AbrirSolicitacao(g.Item.Pedido, g.Linha);
                        return true;
                    }
                    if ((keyData == Keys.Enter || keyData == Keys.Space) && paciente && g.Itens.Count > 0)
                    {
                        Alternar(atual); return true;
                    }
                    if (keyData == Keys.Right && paciente && !aberto && g.Itens.Count > 0)
                    {
                        Alternar(atual); return true;
                    }
                    if (keyData == Keys.Left && aberto)
                    {
                        var pai = grid.Rows[IndicePaciente(atual.Index)];
                        if (pai != atual) { TerminarAnimacao(); if (pai.Index >= 0) grid.CurrentCell = pai.Cells[CPaciente]; }
                        Alternar(pai); return true;
                    }
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ===== APARÊNCIA =====
        private void Grid_Format(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count || e.ColumnIndex < 0) return;
            var g = grid.Rows[e.RowIndex].Tag as LinhaGrade;
            if (g == null) return;
            string col = Colunas[e.ColumnIndex];
            bool icone = col == "sol" || col == "presc" || col == "abre";

            if (g.Item != null)
            {
                // Item do acordeão: fundo levemente tingido, fonte um pouco menor (marca o nível);
                // o rótulo em cor de texto (é o que se lê), o resto em cor auxiliar e a situação
                // do pedido em cor.
                e.CellStyle.BackColor = Theme.ContentBg;
                e.CellStyle.SelectionBackColor = Theme.SelBg;
                if (icone) return;
                e.CellStyle.Font = FonteItem;
                Color cor = col == "paciente" ? Theme.TextDark : (col == "prescricao" ? g.CorPrescricao : Theme.TextSoft);
                e.CellStyle.ForeColor = cor;
                // No azul da seleção o texto auxiliar fica abaixo do contraste AA: escurece (a
                // situação do pedido mantém a cor, que é a informação).
                e.CellStyle.SelectionForeColor = col == "prescricao" ? cor : Theme.TextDark;
                return;
            }

            if (icone) return;
            if (g.Cinza)
            {
                e.CellStyle.ForeColor = Theme.TextSoft;
                e.CellStyle.SelectionForeColor = Theme.TextSoft;
                return;
            }
            if (col == "prescricao")
            {
                e.CellStyle.ForeColor = g.CorPrescricao;
                e.CellStyle.SelectionForeColor = g.CorPrescricao;
                e.CellStyle.Font = Theme.BodyBold;
            }
            else if (col == "situacao" && g.SituacaoDestaque)
            {
                e.CellStyle.ForeColor = Theme.Primary;
                e.CellStyle.SelectionForeColor = Theme.Primary;
            }
        }

        private static Color CorFarmacia(FarmaciaNaPrescricao f)
        {
            if (f == FarmaciaNaPrescricao.SemPedido) return Theme.Warning;
            if (f == FarmaciaNaPrescricao.AguardandoPedido) return Theme.Primary;
            return Theme.TextSoft;
        }

        private static Color CorItem(ItemPam f)
        {
            if (f.Pedido == null) return CorFarmacia(PainelPam.Farmacia(f.Prescricao));
            if (f.Pedido.Situacao == "S") return Theme.Success;
            if (f.Pedido.Situacao == "P") return Theme.Warning;
            return Theme.Primary;
        }

        // Os itens do acordeão são pintados aqui por inteiro: hover próprio (o HoverBg do
        // GridPro some no fundo tingido do item), a guia que liga o item à seta do paciente e
        // os dois ícones como botões (realce arredondado sob o mouse). Linha de paciente segue
        // a pintura normal do GridPro.
        private void Grid_Pintar(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var g = grid.Rows[e.RowIndex].Tag as LinhaGrade;
            if (g == null || g.Item == null) return;
            string col = Colunas[e.ColumnIndex];

            bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
            Color fundo = sel ? e.CellStyle.SelectionBackColor
                        : (e.RowIndex == grid.LinhaHover ? HoverItem : e.CellStyle.BackColor);
            using (var b = new SolidBrush(fundo))
                e.Graphics.FillRectangle(b, e.CellBounds);
            using (var p = new Pen(Theme.GridLine))
                e.Graphics.DrawLine(p, e.CellBounds.Left, e.CellBounds.Bottom - 1,
                                       e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);

            bool sol = col == "sol" && g.Item.Pedido != null;
            bool presc = col == "presc" && g.Item.Prescricao != null;
            if (col == "abre") PintarGuia(e.Graphics, e.CellBounds, g.UltimoItem);
            else if (sol || presc)
            {
                if (e.RowIndex == _hoverLinha && e.ColumnIndex == _hoverColuna) PintarRealce(e.Graphics, e.CellBounds);
                bool cabe = e.CellBounds.Height >= Theme.S(18);   // linha abrindo/fechando: sem ícone cortado
                if (cabe && sol)
                    TextRenderer.DrawText(e.Graphics, GlifoSolicitacao, Theme.Icon, e.CellBounds, Theme.Primary,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                else if (cabe) DesenharFolhaRx(e.Graphics, e.CellBounds, Theme.Primary);
            }
            else e.Paint(e.ClipBounds, DataGridViewPaintParts.ContentForeground);
            e.Handled = true;
        }

        // ├─ / └─ na coluna da seta: o item fica visualmente pendurado no paciente.
        private static void PintarGuia(Graphics g, Rectangle cel, bool ultimo)
        {
            int x = cel.X + cel.Width / 2;
            int meio = cel.Y + cel.Height / 2;
            using (var pen = new Pen(Theme.BorderSoft))
            {
                g.DrawLine(pen, x, cel.Y, x, ultimo ? meio : cel.Bottom - 1);
                g.DrawLine(pen, x, meio, cel.Right - 1, meio);
            }
        }

        // Fundo arredondado do ícone sob o mouse: diz que é um botão.
        private static void PintarRealce(Graphics g, Rectangle cel)
        {
            int w = Math.Min(cel.Width - Theme.S(8), Theme.S(32)), h = Math.Min(cel.Height - Theme.S(4), Theme.S(24));
            if (w <= 0 || h <= 0) return;
            var r = new Rectangle(cel.X + (cel.Width - w) / 2, cel.Y + (cel.Height - h) / 2, w, h);
            int raio = Theme.S(5);
            var antes = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            using (var b = new SolidBrush(RealceIcone))
            {
                path.AddArc(r.X, r.Y, raio * 2, raio * 2, 180, 90);
                path.AddArc(r.Right - raio * 2, r.Y, raio * 2, raio * 2, 270, 90);
                path.AddArc(r.Right - raio * 2, r.Bottom - raio * 2, raio * 2, raio * 2, 0, 90);
                path.AddArc(r.X, r.Bottom - raio * 2, raio * 2, raio * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
            g.SmoothingMode = antes;
        }

        // Ícone da prescrição médica: folha com a ponta dobrada e "Rx" dentro, no traço de 1 px
        // dos glifos vizinhos (o glifo de documento do Segoe MDL2 saía um quadrado vazio).
        private static void DesenharFolhaRx(Graphics g, Rectangle cel, Color cor)
        {
            int w = Theme.S(14), h = Theme.S(16), d = Theme.S(4);
            if (cel.Height < h + 2) return;   // linha ainda abrindo (animação): sem ícone cortado
            int x = cel.X + (cel.Width - w) / 2, y = cel.Y + (cel.Height - h) / 2 - 1;
            var antes = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(cor, Math.Max(1f, Theme.Escala)))
            {
                pen.LineJoin = LineJoin.Round;
                // coordenada inteira = centro do pixel no GDI+: o traço reto sai nítido
                float x0 = x, y0 = y, x1 = x0 + w, y1 = y0 + h, xd = x1 - d;
                g.DrawPolygon(pen, new PointF[] {
                    new PointF(x0, y0), new PointF(xd, y0), new PointF(x1, y0 + d),
                    new PointF(x1, y1), new PointF(x0, y1) });
                g.DrawLines(pen, new PointF[] { new PointF(xd, y0), new PointF(xd, y0 + d), new PointF(x1, y0 + d) });
            }
            g.SmoothingMode = antes;
            var area = new Rectangle(x + 1, y + Theme.S(4), w - 1, h - Theme.S(4));   // por dentro do traço
            TextRenderer.DrawText(g, "Rx", FonteRx, area, cor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        // ===== MOUSE: acordeão e documentos =====
        // Clique simples em QUALQUER ponto da linha do paciente abre/fecha (antes só a seta
        // pequena ou o duplo clique). No item, só os ícones fazem algo.
        private void Grid_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var g = grid.Rows[e.RowIndex].Tag as LinhaGrade;
            if (g == null) return;
            if (g.Item == null)
            {
                if (g.Itens.Count > 0) Alternar(grid.Rows[e.RowIndex]);
                return;
            }
            string col = Colunas[e.ColumnIndex];
            if (col == "sol" && g.Item.Pedido != null) AbrirSolicitacao(g.Item.Pedido, g.Linha);
            else if (col == "presc" && g.Item.Prescricao != null) AbrirPrescricao(g.Item.Prescricao, g.Linha);
        }

        // Mãozinha e realce só onde há o que clicar: a linha de paciente com itens e os ícones.
        private void Grid_CellMouseEnter(object sender, DataGridViewCellEventArgs e)
        {
            Apontar(e.RowIndex, e.ColumnIndex);
        }

        private void Grid_CellMouseLeave(object sender, DataGridViewCellEventArgs e)
        {
            Apontar(-1, -1);
        }

        private void Apontar(int linha, int coluna)
        {
            Cursor c = Cursors.Default;
            int hl = -1, hc = -1;
            if (linha >= 0 && coluna >= 0 && linha < grid.Rows.Count)
            {
                var g = grid.Rows[linha].Tag as LinhaGrade;
                string col = Colunas[coluna];
                if (g != null && g.Item == null && g.Itens.Count > 0) c = Cursors.Hand;
                else if (g != null && g.Item != null
                    && ((col == "sol" && g.Item.Pedido != null) || (col == "presc" && g.Item.Prescricao != null)))
                {
                    c = Cursors.Hand; hl = linha; hc = coluna;
                }
            }
            if (_abrindo) c = Cursors.AppStarting;
            if (grid.Cursor != c) grid.Cursor = c;
            MudarRealce(hl, hc);
        }

        // Linhas entraram/saíram embaixo do mouse parado (acordeão, atualização): recalcula
        // hover da linha, realce do ícone e cursor pelo ponto em que o mouse está agora.
        private void RecalcularMouse()
        {
            if (grid == null || grid.IsDisposed || !grid.IsHandleCreated) return;
            grid.RecalcularHover();
            var pt = grid.PointToClient(Cursor.Position);
            if (!grid.ClientRectangle.Contains(pt)) { Apontar(-1, -1); return; }
            var hit = grid.HitTest(pt.X, pt.Y);
            Apontar(hit.RowIndex, hit.ColumnIndex);
        }

        private void MudarRealce(int linha, int coluna)
        {
            if (linha == _hoverLinha && coluna == _hoverColuna) return;
            int l0 = _hoverLinha, c0 = _hoverColuna;
            _hoverLinha = linha; _hoverColuna = coluna;
            if (l0 >= 0 && l0 < grid.Rows.Count && c0 >= 0) grid.InvalidateCell(c0, l0);
            if (linha >= 0 && coluna >= 0) grid.InvalidateCell(coluna, linha);
        }

        private void Ocupado(bool sim)
        {
            _abrindo = sim;
            if (grid == null || grid.IsDisposed) return;
            if (sim) grid.Cursor = Cursors.AppStarting;
            else RecalcularMouse();   // volta a mãozinha se o mouse ainda está no ícone
        }

        // Abre o comprovante da solicitação como o próprio MV faz: gera o R_SOLSAI_PRO no
        // servidor e abre a URL no navegador. Não dá baixa.
        private async void AbrirSolicitacao(Solicitacao s, LinhaPam l)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Warning, "Faça login para abrir a solicitação."); return; }
            if (_abrindo) return;
            Ocupado(true);
            banner.SetState(StatusKind.Busy, "Gerando o PDF da solicitação " + s.Numero + "...");
            bool tenho = false;
            try
            {
                // Gerar o PDF abre outro form no MV: pega a vez no portão.
                tenho = await MvGate.EntrarAsync("pam-solicitacao", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho) { banner.SetState(StatusKind.Warning, "O MV está respondendo outra consulta. Tente de novo em instantes."); return; }
                string url = await MorphisClient.GerarPdfSolicitacaoAsync(_host.Session, s.Numero);
                if (IsDisposed || Disposing) return;
                Navegador.Abrir(url);
                banner.SetState(StatusKind.Success, "Solicitação " + s.Numero + " de " + Nome(l) + " aberta no navegador.");
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing) banner.SetState(StatusKind.Error, "Não foi possível gerar o PDF da solicitação: " + ex.Message);
            }
            finally { if (tenho) MvGate.Sair(); if (!IsDisposed) Ocupado(false); }
        }

        // Abre a folha oficial da prescrição médica (PEP): baixa o PDF e entrega ao navegador por
        // um endereço local de vida curta (PdfEfemero) — nada fica gravado em disco.
        private async void AbrirPrescricao(PrescricaoPep p, LinhaPam l)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Warning, "Faça login para abrir a prescrição."); return; }
            if (_abrindo) return;
            Ocupado(true);
            banner.SetState(StatusKind.Busy, "Gerando a prescrição médica de " + Nome(l) + "...");
            try
            {
                if (!await PepClient.EntrarAsync(_host.Session))
                {
                    if (!IsDisposed) banner.SetState(StatusKind.Error, "O PEP não respondeu. Tente de novo em instantes.");
                    return;
                }
                byte[] pdf = await PepClient.BaixarDocumentoPdfAsync(_host.Session, p.Id);
                if (IsDisposed || Disposing) return;
                if (pdf == null)
                {
                    banner.SetState(StatusKind.Warning, "O MV não gerou a folha desta prescrição (só prescrição médica assinada gera PDF).");
                    return;
                }
                string url = PdfEfemero.Publicar(pdf, "prescricao-" + p.Id + ".pdf");
                Navegador.Abrir(url);
                banner.SetState(StatusKind.Success, "Prescrição médica de " + Nome(l) + " (assinada às "
                    + p.Hora.ToString("HH:mm") + ") aberta no navegador.");
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing) banner.SetState(StatusKind.Error, "Não foi possível abrir a prescrição: " + ex.Message);
            }
            finally { if (!IsDisposed) Ocupado(false); }
        }

        private static string Nome(LinhaPam l)
        {
            return string.IsNullOrEmpty(l.At.Paciente) ? "atendimento " + l.At.Atendimento : l.At.Paciente;
        }

        // ===== CICLO DE ATUALIZAÇÃO =====
        // A tela só DESENHA o que o monitor de fundo leu (ver abaixo). Voltando para a aba com
        // uma leitura em memória, a lista aparece na hora; na 1ª visita da sessão, dispara a
        // leitura e espera a vez no portão do MV.
        private void OnLoad(object sender, EventArgs e)
        {
            if (_host.Session == null)
            {
                if (_host.Usuario == "PREVIEW") { CarregarExemplo(); return; }
                banner.SetState(StatusKind.Warning, "Faça login para ver os atendimentos do PAM.");
                lblVazio.Text = "Faça login para ver os atendimentos do PAM.";
                return;
            }
            _viva = this;
            IniciarMonitor(_host);   // a partir da 1ª visita, atualiza sozinho até o logout
            if (_memQuando > DateTime.MinValue) Absorver();
            else LerAgora(false);
        }

        public void ComandoAtualizar() { LerAgora(true); }
        public void ComandoFocarBusca() { }

        // F5 e 1ª visita: a MESMA leitura do monitor, na hora, esperando a vez no portão como as
        // outras telas (60 s). O tique de fundo recomeça a contar a partir daqui.
        private async void LerAgora(bool pedidoDoUsuario)
        {
            if (IsDisposed || Disposing) return;
            if (_host.Session == null) { banner.SetState(StatusKind.Warning, "Faça login para ver os atendimentos do PAM."); return; }
            if (_varrendo)
            {
                // Já tem leitura em curso (em geral a de fundo): ela cai na grade ao terminar.
                if (pedidoDoUsuario && !_abrindo) banner.SetState(StatusKind.Busy, "Atualização em andamento — a lista se renova em instantes.");
                return;
            }
            IniciarMonitor(_host);   // idempotente: religa se a sessão expirou em segundo plano
            if (!_abrindo) banner.SetState(StatusKind.Busy, "Atualizando os atendimentos do PAM...");
            int sessao = _sessaoId;
            try
            {
                bool ok = await VarrerAsync(_host.Session, sessao, 60000);
                if (sessao != _sessaoId) return;
                if (!ok)
                {
                    Rearmar(NovaTentativaMs);
                    if (IsDisposed || Disposing) return;
                    if (!_abrindo) banner.SetState(StatusKind.Warning, "O MV está respondendo outra consulta. Tentando de novo em instantes...");
                    if (!_carregou) lblVazio.Text = "O MV está ocupado com outra consulta. Tentando de novo em instantes...";
                    return;
                }
                Rearmar(IntervaloMs);
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                Logger.Log("Urgência (PAM): erro ao atualizar: " + ex.Message);
                if (sessao == _sessaoId) _memErro = ex.Message;
                if (!IsDisposed && !Disposing) MostrarBanner();
            }
        }

        // Uma leitura terminou (do monitor ou do F5): desenha e atualiza o banner.
        private void Absorver()
        {
            _linhas = _memLinhas;
            _carregou = true;
            Render();
            MostrarBanner();
        }

        private void MostrarBanner()
        {
            if (_abrindo) return;   // a mensagem de uma ação (documento aberto) não é apagada no mesmo instante
            if (_memErro != null)
            {
                string ultima = _memQuando > DateTime.MinValue ? " A lista é a das " + _memQuando.ToString("HH:mm") + "." : "";
                banner.SetState(StatusKind.Error, "Erro ao consultar o PAM: " + _memErro + ultima);
                if (!_carregou) lblVazio.Text = "Não foi possível consultar o PAM. Tente de novo com F5.";
                return;
            }
            if (_memQuando == DateTime.MinValue) return;
            string quando = "Atualizado às " + _memQuando.ToString("HH:mm:ss") + " · atualiza sozinho a cada minuto.";
            if (_memAviso != null) banner.SetState(StatusKind.Warning, quando + " " + _memAviso);
            else banner.SetState(StatusKind.Info, quando);
        }

        // ===== MONITOR EM SEGUNDO PLANO =====
        // Como o das Solicitações: liga na PRIMEIRA visita à aba e vive enquanto durar a SESSÃO,
        // não enquanto a aba está aberta — o shell destrói a view ao trocar de módulo, então um
        // timer de instância morreria junto. A cada minuto relê o PAM; o resultado fica em
        // memória (estático) e, com a aba na tela, cai direto na grade. Quem volta para a aba
        // encontra pronto o que mudou na ausência.
        //
        // Custo por ciclo: os pedidos de hoje saem da foto que o monitor das Solicitações tira
        // a cada 30 s (ligado junto, como faz a aba Antimicrobianos); no portão do MV sobra o
        // CON_ATE de hoje (os dias anteriores têm cache de 5 min). O PEP é outra sessão. O tique
        // desiste SEM enfileirar se o portão estiver ocupado — nunca atrapalha quem está
        // produzindo kit ou enviando pedido — e tenta de novo em 8 s.
        public const int IntervaloMs = 60000;
        private const int NovaTentativaMs = 8000;
        private const int IdadeMaxFotoSeg = 90;     // foto das Solicitações mais velha que isso: busca aqui

        private static Timer _monitor;
        private static MainWindow _monitorHost;
        private static int _sessaoId;               // muda no logout: descarta leitura órfã
        private static EntregasPamView _viva;       // instância na tela (null = aba fechada)
        private static bool _varrendo;
        private static List<LinhaPam> _memLinhas = new List<LinhaPam>();
        private static DateTime _memQuando = DateTime.MinValue;   // MinValue = nada lido nesta sessão
        private static string _memAviso;            // o PEP não respondeu na última leitura
        private static string _memErro;             // a última leitura falhou (null = ok)

        public static void IniciarMonitor(MainWindow host)
        {
            _monitorHost = host;
            SolicitacoesView.IniciarMonitor(host);   // os pedidos de hoje saem da foto dele
            if (_monitor == null)
            {
                _monitor = new Timer();
                _monitor.Interval = IntervaloMs;
                _monitor.Tick += new EventHandler(MonitorTick);
            }
            if (_monitor.Enabled) return;            // já ligado nesta sessão
            _monitor.Interval = IntervaloMs;
            _monitor.Start();
            Logger.Log("Urgência (PAM): monitor em segundo plano ligado (a cada " + (IntervaloMs / 1000) + "s).");
        }

        // Fim de sessão (logout, expiração, fechamento): invalida a leitura em voo e APAGA o que
        // está em memória — tem nome de paciente e não pode reaparecer no login seguinte.
        public static void PararMonitor()
        {
            bool estava = _monitor != null && _monitor.Enabled;
            if (_monitor != null) _monitor.Stop();
            _monitorHost = null;
            _sessaoId++;
            _memLinhas = new List<LinhaPam>();
            _memQuando = DateTime.MinValue;
            _memAviso = null; _memErro = null;
            _abertos.Clear();
            if (estava) Logger.Log("Urgência (PAM): monitor em segundo plano desligado.");
        }

        // Próximo tique daqui a 'ms' (8 s quando o portão estava ocupado; 1 min no resto).
        private static void Rearmar(int ms)
        {
            if (_monitor == null || _monitorHost == null) return;
            _monitor.Stop();
            _monitor.Interval = ms;
            _monitor.Start();
        }

        private static async void MonitorTick(object sender, EventArgs e)
        {
            if (_monitorHost == null || _monitorHost.Session == null) return;
            if (_varrendo) return;                                          // F5 em curso
            if (_monitorHost.WindowState == FormWindowState.Minimized) return;
            if (ProduzirView.OcupadoNoMv) return;                           // preview de kit aberto

            int sessao = _sessaoId;
            _monitor.Stop();   // o intervalo conta do FIM da leitura
            int proximo = IntervaloMs;
            try
            {
                if (!await VarrerAsync(_monitorHost.Session, sessao, 0)) proximo = NovaTentativaMs;
            }
            catch (SessaoExpiradaException)
            {
                // Em segundo plano não se arranca o usuário do que ele está fazendo: só para o
                // monitor. A próxima ação dele encontra a sessão morta e vai ao login.
                Logger.Log("Urgência (PAM): sessão expirada — monitor parado.");
                if (sessao == _sessaoId) PararMonitor();
                return;
            }
            catch (Exception ex)
            {
                Logger.Log("Urgência (PAM): falha no monitor: " + ex.Message);
                if (sessao == _sessaoId)
                {
                    _memErro = ex.Message;
                    var v = _viva;
                    if (v != null && !v.IsDisposed && !v.Disposing) v.MostrarBanner();
                }
            }
            finally
            {
                // Só rearma se ainda for a mesma sessão (senão o logout já parou de propósito).
                if (sessao == _sessaoId) Rearmar(proximo);
            }
        }

        // Uma leitura completa: forms (atendimentos + pedidos) pelo portão do MV, depois o PEP
        // fora dele. ESTÁTICA: roda com a aba fechada; o resultado vai para a memória e, se a
        // aba estiver na tela, para a grade. esperaMs = 0 desiste na hora se o portão estiver
        // ocupado (tique de fundo); > 0 espera a vez (F5, 1ª visita).
        // false = não conseguiu a vez no portão.
        private static async Task<bool> VarrerAsync(Session s, int sessao, int esperaMs)
        {
            _varrendo = true;
            try
            {
                bool tenho = esperaMs > 0
                    ? await MvGate.EntrarAsync("urgencia-pam", esperaMs)
                    : (!MvGate.Ocupado && MvGate.Entrar("monitor-urgencia-pam"));
                if (!tenho) return false;
                PainelPamResult res;
                try
                {
                    var foto = SolicitacoesView.FotoDeHoje(PainelPam.EstoqueFarmacia, IdadeMaxFotoSeg);
                    res = await PainelPam.LerMvAsync(s, foto);
                }
                finally { MvGate.Sair(); }
                if (sessao != _sessaoId) return true;   // logout no meio: descarta tudo
                await PainelPam.CompletarPepAsync(s, res);
                if (sessao != _sessaoId) return true;

                int semPep = PainelPam.HerdarPrescricoes(res, _memLinhas);
                _memLinhas = res.Linhas;
                _memQuando = res.Quando;
                _memErro = null;
                _memAviso = res.AvisoPep == null ? null
                    : "O PEP não respondeu" + (semPep > 0
                        ? " (" + semPep + " paciente(s) sem dado de prescrição)."
                        : ": mostrando as prescrições da leitura anterior.");
                var v = _viva;
                if (v != null && !v.IsDisposed && !v.Disposing) v.Absorver();
                return true;
            }
            finally { _varrendo = false; }
        }

        // ===== TEXTOS =====
        private static string RotuloItem(ItemPam f)
        {
            if (f.Pedido == null) return PainelPam.Conteudo(f.Prescricao) ?? "Prescrição sem pedido à farmácia";
            string origem = f.Pedido.OrigemLabel ?? "";
            if (origem.Length == 0 || origem == "—") origem = "Pedido";
            return origem + " (nº " + f.Pedido.Numero + ")";                    // Prescrição, Material, Transferência
        }

        private static string SituacaoItem(ItemPam f)
        {
            if (f.Pedido != null) return f.Pedido.SituacaoLabel;
            switch (PainelPam.Farmacia(f.Prescricao))
            {
                case FarmaciaNaPrescricao.Nada: return "Sem itens da farmácia";
                case FarmaciaNaPrescricao.AguardandoPedido: return "Aguardando pedido";
                case FarmaciaNaPrescricao.SemPedido: return "Sem pedido";
                default: return "Assinada";
            }
        }

        // Tooltip do item: os números e o que foi prescrito, com o suspenso marcado.
        private static string DicaItem(ItemPam f)
        {
            var linhas = new List<string>();
            if (f.Pedido != null) linhas.Add("Solicitação nº " + f.Pedido.Numero);
            var p = f.Prescricao;
            if (p != null)
            {
                linhas.Add("Prescrição" + (p.CdPreMed > 0 ? " nº " + p.CdPreMed : "") + " assinada às " + p.Hora.ToString("HH:mm"));
                if (p.Itens != null)
                    for (int i = 0; i < p.Itens.Count; i++)
                    {
                        if (i == 12) { linhas.Add("• e mais " + (p.Itens.Count - 12) + " itens"); break; }
                        linhas.Add("• " + p.Itens[i].Nome + (p.Itens[i].Suspenso ? " (suspenso)" : ""));
                    }
            }
            return string.Join("\n", linhas.ToArray());
        }

        private static string Duracao(TimeSpan t)
        {
            if (t.TotalMinutes < 0) t = TimeSpan.Zero;
            int min = (int)t.TotalMinutes;
            if (min < 60) return min + " min";
            return (min / 60) + "h" + (min % 60).ToString("00");
        }

        private static string HoraCurta(DateTime d, DateTime agora)
        {
            return d.Date == agora.Date ? d.ToString("HH:mm") : d.ToString("dd/MM HH:mm");
        }

        // Hoje só com a hora; ontem "ontem 23:40" (só a hora parecia hoje); antes disso, a data.
        private static string HoraComDia(DateTime d, DateTime agora)
        {
            if (d.Date == agora.Date) return d.ToString("HH:mm");
            if (d.Date == agora.Date.AddDays(-1)) return "ontem " + d.ToString("HH:mm");
            return d.ToString("dd/MM HH:mm");
        }

        private static string TextoSituacao(LinhaPam l)
        {
            var a = l.At;
            if (!a.EmAtendimento)
                return ("Alta " + (a.Saida.HasValue ? a.Saida.Value.ToString("HH:mm") : "")).Trim();   // motivo no tooltip
            // "prescrevendo" só quando a coluna Prescrição mostra outro estado (pendente/atendida):
            // com "Em elaboração" lá, repetir aqui é ruído.
            if (l.MedicoPrescrevendo && l.Estado != EstadoPrescricaoPam.EmElaboracao) return "Médico prescrevendo";
            return "Em atendimento";
        }

        // "ALTA APOS MEDICACAO" -> "após medicação"; "ENCAMINHADO A INTERNACAO" -> "internação".
        private static string Motivo(string m)
        {
            string s = PainelPam.SemAcento(m);
            if (s.Length == 0) return "";
            if (s.Contains("INTERNA")) return "internação";
            if (s.Contains("MEDICA")) return "após medicação";
            if (s.Contains("CONSULTA")) return "após consulta";
            if (s.Contains("OBITO")) return "óbito";
            if (s.Contains("TRANSF")) return "transferência";
            return m.Trim().ToLowerInvariant();
        }

        // ===== PRÉ-VISUALIZAÇÃO (--pam-preview): pacientes fictícios, sem tocar o MV =====
        private void CarregarExemplo()
        {
            DateTime agora = DateTime.Now;
            _linhas = new List<LinhaPam>();
            // acordeões abertos, para a pré-visualização mostrar os tipos de item
            var um = Ex("MARIA EXEMPLO APARECIDA DOS SANTOS OLIVEIRA", agora.AddMinutes(-78), null, "", "MEDICO EXEMPLO A",
                new[] { "S|PRE|-60", "S|AVU|-41", "P|PRE|-6" }, new[] { -61, -50, -7 },
                new[] { "MDN:DIPIRONA SODICA 1G/2ML AMP;MDN:ONDANSETRONA 4MG/2ML AMP", "MAV:METOPROLOL 1MG/1ML  AMP 5ML:S",
                        "MDN:TRAMADOL 50MG/ML AMP C/ 1ML" }, true);
            _linhas.Add(um); _abertos.Add(um.At.Atendimento);
            var sete = Ex("PACIENTE EXEMPLO SETE", agora.AddMinutes(-25), null, "", "MEDICO EXEMPLO B",
                new string[0], new[] { -15 }, new[] { "EXL:HEMOGRAMA;EXL:PCR - PROTEINA C REATIVA, QUANTITATIVA" }, false);
            _linhas.Add(sete); _abertos.Add(sete.At.Atendimento);
            _linhas.Add(Ex("PACIENTE EXEMPLO DOIS DA SILVA", agora.AddMinutes(-41), null, "", "MEDICO EXEMPLO B",
                new[] { "S|PRE|-30" }, new[] { -31 }, null, true));
            var tres = Ex("PACIENTE EXEMPLO TRES", agora.AddMinutes(-52), null, "", "MEDICO EXEMPLO A",
                new[] { "S|PRE|-38", "S|AVU|-25" }, new[] { -39, -20 },
                new[] { "MDN:CETOPROFENO 100MG FA EV", "PEN:RETIRAR SONDA VESICAL DE DEMORA - SVD;PEN:IRRIGACAO VESICAL;EXL:UREIA SERICA" }, false);
            _linhas.Add(tres); _abertos.Add(tres.At.Atendimento);
            _linhas.Add(Ex("PACIENTE EXEMPLO QUATRO", agora.AddMinutes(-9), null, "", "MEDICO EXEMPLO B",
                new string[0], new int[0], null, true));
            _linhas.Add(Ex("PACIENTE EXEMPLO CINCO", agora.AddMinutes(-4), null, "", "",
                new string[0], new int[0], null, false));
            _linhas.Add(Ex("PACIENTE EXEMPLO SEIS", agora.AddMinutes(-130), agora.AddMinutes(-12), "ALTA APOS MEDICACAO", "MEDICO EXEMPLO A",
                new[] { "S|PRE|-100" }, new[] { -101 }, null, false));
            PainelPam.Ordenar(_linhas);
            Render();
            banner.SetState(StatusKind.Info, "Pré-visualização: pacientes fictícios, sem consultar o MV.");
        }

        // pedidos: "situação|origem|minutos relativos" (ex.: "P|PRE|-6"); assinadas: minutos relativos;
        // itens de cada assinada: "ESQUEMA:NOME[:S]" separados por ";" (S = suspenso).
        private static LinhaPam Ex(string nome, DateTime entrada, DateTime? alta, string motivo,
            string medico, string[] pedidos, int[] assinadas, string[] itens, bool prescrevendo)
        {
            var l = new LinhaPam();
            l.At = new AtendimentoPam();
            l.At.Atendimento = (562700 + Math.Abs(nome.GetHashCode() % 99)).ToString();
            l.At.Paciente = nome; l.At.Entrada = entrada; l.At.Medico = medico;
            l.At.Alta = alta; l.At.AltaMedica = alta; l.At.MotivoAlta = motivo; l.At.Destino = "SALA DE EMERGENCIA";
            long n = 482900;
            foreach (var p in pedidos)
            {
                var partes = p.Split('|');
                DateTime h = DateTime.Now.AddMinutes(int.Parse(partes[2]));
                var s = new Solicitacao();
                s.Numero = n++; s.Estoque = 5; s.Situacao = partes[0]; s.Origem = partes[1]; s.TpSol = "P";
                s.Data = h.ToString("dd/MM/yyyy"); s.Hora = h.ToString("HH:mm"); s.Setor = "PRONTO ATENDIMENTO";
                // pedido de prescrição carrega o nº da prescrição; as fictícias não têm nº lido da
                // folha, então casam pela hora (o caminho de reserva do PainelPam.Itens)
                if (partes[1] == "PRE") s.CdPreMed = (1368000 + n).ToString();
                l.Pedidos.Add(s);
            }
            long id = 3428600;
            for (int i = 0; i < assinadas.Length; i++)
            {
                int m = assinadas[i];
                var pr = new PrescricaoPep();
                pr.Id = id++; pr.Status = "SIGNED"; pr.Modelo = "PRESCRICAO MEDICA";
                pr.Criada = DateTime.Now.AddMinutes(m - 1); pr.Fechada = DateTime.Now.AddMinutes(m);
                if (itens != null && i < itens.Length)
                {
                    pr.Itens = new List<ItemPrescrito>();
                    foreach (string spec in itens[i].Split(';'))
                    {
                        var partes = spec.Split(':');
                        var it = new ItemPrescrito();
                        it.Esquema = partes[0]; it.Nome = partes[1];
                        it.Medicamento = it.Esquema.StartsWith("M");   // fictício: MDN/MAV são medicamento
                        it.Suspenso = partes.Length > 2 && partes[2] == "S";
                        pr.Itens.Add(it);
                    }
                }
                l.Prescricoes.Add(pr);
            }
            if (prescrevendo)
            {
                var pr = new PrescricaoPep();
                pr.Id = id++; pr.Status = "OPENED"; pr.Modelo = "PRESCRICAO MEDICA"; pr.Criada = DateTime.Now.AddMinutes(-2);
                l.Prescricoes.Add(pr);
            }
            l.PepLido = true;
            return l;
        }
    }
}
