using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Módulo "Atender" (Solicitações > Atender): o atendente bipa ou digita o nº da solicitação
    // e os itens pedidos já vêm na grade, na ordem da solicitação impressa — ele só escolhe o
    // lote (F9) e digita a quantidade. A última linha da grade fica sempre vazia: selecionada,
    // o F9 abre os produtos do estoque com filtro pelo nome, para incluir item fora do pedido
    // ou outro lote do mesmo produto. Del exclui a linha. Desenho inspirado na tela "Solicitação
    // de Produtos" do MV (cabeçalho em campos, grade "Produtos", botões embaixo).
    // Decisões do usuário (28/10/2026): quantidade e lote NÃO são sugeridos; a diferença fica
    // parcial; todo tipo de pedido; todos os logins; saldo do lote só na janela de lotes.
    //
    // ETAPA 1: só LÊ do MV — cabeçalho e itens (C_SOLSAI_PRO), lotes (R_CONF_LOTE) e produtos do
    // estoque (Posição de Estoque). O F12 confere e mostra o resumo; a gravação (M_BAIXASOL,
    // protocolo em memória/reference_mv_atender_solicitacao) entra na etapa 2.
    public class AtenderView : UserControl, IComandosDeTela
    {
        private const int CProduto = 0, CDescricao = 1, CUnidade = 2, CSolicitado = 3, CJaAtendido = 4,
                          CLote = 5, CValidade = 6, CAtender = 7;

        private const string TextoLinhaVazia = "Incluir produto — F9 na coluna Produto";
        private static readonly Size TamanhoLotes = new Size(540, 320);      // janelas do F9, a 96 DPI
        private static readonly Size TamanhoProdutos = new Size(740, 500);

        // Tela compacta (auditoria de 30/10/2026: letra grande obrigava a varrer a tela): letra 9 e
        // linhas de 24 px — em 1366x768 a grade mostra ~15 linhas (eram 8).
        public static readonly Font FonteGrade = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        public static readonly Font FonteGradeTitulo = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font FonteLinhaVazia = new Font("Segoe UI", 9.5f, FontStyle.Italic);
        private static readonly Font FonteRotulo = new Font("Segoe UI", 8f, FontStyle.Bold);
        private static readonly Font FonteNumero = new Font("Segoe UI", 11f, FontStyle.Bold);
        private static readonly Font FonteSelo = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font FonteDica = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        public const int AlturaLinha = 28, AlturaTitulo = 30;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private readonly MainWindow _host;
        private readonly bool _preview;
        private AvisosMv avisos;              // notificações no canto inferior direito (como no MV)
        private Label lblDica;    // rodapé: o que fazer na célula atual
        private CarregandoGrade carregando;   // véu com spinner sobre a grade enquanto a solicitação abre
        private AvisoMv _avisoInatividade;
        private TextBox txtNumero;
        private PrimaryButton btnAbrir;
        private Label lblNumero;
        private Selo seloTipo, seloSituacao, seloUrgente;
        private Label vEstoque, vSetor, vPaciente, vPrestador, vSolicitante;
        private readonly ToolTip _dicaCampos = new ToolTip();
        private GridPro grid;
        private IconButton btnExcluir, btnLimpar;
        private PrimaryButton btnFechar;

        private SolicitacaoAtender _sol;
        private readonly List<LinhaAtender> _linhas = new List<LinhaAtender>();
        private readonly Dictionary<int, LotesProduto> _lotes = new Dictionary<int, LotesProduto>();
        private bool _ocupado;                          // lendo a solicitação/lotes no MV
        private bool _buscando;                         // F9 esperando lotes ou produtos
        private int _carga;                             // descarta resposta de uma solicitação anterior
        private Task<List<SaldoProduto>> _produtos;     // lista do F9 de produtos (lida em segundo plano)
        private int _produtosEstoque;
        private bool _montando;                         // suprime eventos da grade ao montar
        private bool _focoNaGrade;                      // o atendente estava na grade (para o Alt+Tab)
        private int _ultimaLinha = -1, _ultimaColuna = -1;

        // Reserva no MV ("Confirmar produtos" ao abrir; ver ReservaAtendimento). Liberada = foi
        // solta por inatividade: a grade continua editável e o próximo toque reserva de novo.
        private enum EstadoReserva { Nenhuma, Reservada, Ocupada, Recusada, Liberada }
        private EstadoReserva _reserva;
        private bool _reservando;
        private bool _liberandoInativa;
        private Selo seloReserva;
        private readonly Timer _relogioReserva = new Timer();
        private DateTime _ultimoToque = DateTime.Now;
        private bool _avisouInatividade;

        public AtenderView(MainWindow host)
        {
            _host = host;
            _preview = host.Session == null && host.Usuario == "PREVIEW";
            this.AutoScaleMode = AutoScaleMode.Inherit;
            this.BackColor = Theme.ContentBg;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            // Sem banner no topo: avisos no canto inferior direito e instruções na barra de dica
            // do rodapé, como no MV.
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // cabeçalho
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // produtos
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // dica + botões

            root.Controls.Add(BuildCabecalho(), 0, 0);
            root.Controls.Add(BuildProdutos(), 0, 1);
            root.Controls.Add(BuildRodape(), 0, 2);
            this.Controls.Add(root);
            avisos = new AvisosMv(this);

            this.Load += new EventHandler(OnLoad);
            _host.VoltouAoPrimeiroPlano += new EventHandler(OnVoltouAoApp);
            _relogioReserva.Interval = 5000;
            _relogioReserva.Tick += new EventHandler(OnRelogioReserva);
            _relogioReserva.Start();
            MostrarCabecalho();
            AtualizarBotoes();
        }

        // Saiu da aba (outro módulo, Sair, sessão expirada): a reserva não fica presa.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _relogioReserva.Stop();
                _relogioReserva.Dispose();
                _host.VoltouAoPrimeiroPlano -= new EventHandler(OnVoltouAoApp);
                _dicaCampos.Dispose();
                // Só a reserva DESTA tela (uma aba nova descartada antes de abrir não solta nada).
                if (_sol != null && _reserva == EstadoReserva.Reservada) LiberarEmSegundoPlano(_host.Session, "saiu da aba Atender", _sol.Numero);
            }
            base.Dispose(disposing);
        }

        // Pergunta antes de sair da aba com lotes digitados (o ShellView consulta ao trocar de
        // módulo e no Sair).
        public bool PodeSair()
        {
            if (_sol == null || !TemDigitacao()) return true;
            return PerguntaMv.Perguntar(this, "Sair da solicitação " + _sol.Numero + "?",
                "Os lotes e quantidades digitados serão descartados e a solicitação será liberada no MV.", "Sair", "Voltar", false);
        }

        // ===== RESERVA NO MV =====
        // Libera fora do fluxo da tela (borracha, saída da aba): entra pelo MvGate (sem conseguir
        // em 1 min, libera assim mesmo — pior é a solicitação presa) e não depende da aba
        // continuar aberta. Sem rede, a liberação fica pendente e o ReservaAtendimento repete.
        private static async void LiberarEmSegundoPlano(Session s, string motivo, long numero)
        {
            var atual = ReservaAtendimento.Atual;
            if (atual == null || atual.Numero != numero) return;
            bool tenho = await MvGate.EntrarAsync("reserva-liberar", 60000);
            try { await ReservaAtendimento.LiberarAsync(s, motivo, numero); }
            catch (Exception ex) { Logger.Log("Atender: liberar (" + motivo + ") falhou: " + ex.Message); }
            finally { if (tenho) MvGate.Sair(); }
        }

        // Resultado do "Confirmar produtos": reservada = segue editável; ocupada ou recusada =
        // fica só para consulta, com o motivo.
        private bool AplicarReserva(TentativaReserva t)
        {
            _reservando = false;
            if (t.Resultado == ResultadoReserva.Reservada)
            {
                _reserva = EstadoReserva.Reservada;
                _ultimoToque = DateTime.Now; _avisouInatividade = false;
                MostrarReserva();
                GarantirLinhaVazia();
                grid.Invalidate();
                AtualizarBotoes();
                return true;
            }
            if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
            if (t.Resultado == ResultadoReserva.OcupadaPorOutro)
            {
                _reserva = EstadoReserva.Ocupada;
                // Notificação no canto (não trava a tela): fica até fechar no ✕.
                avisos.SetState(StatusKind.Warning, "A solicitação " + _sol.Numero + " está sendo atendida neste momento por outro usuário " +
                    "(o MV não informa quem). Ela fica aberta aqui só para consulta; tente de novo daqui a pouco.", "Em atendimento por outro usuário");
            }
            else
            {
                _reserva = EstadoReserva.Recusada;
                avisos.SetState(StatusKind.Warning, "O MV não liberou o atendimento da " + _sol.Numero + ": " + t.Mensagem);
            }
            MostrarReserva();
            Renderizar();
            AtualizarBotoes();
            return false;
        }

        private void MostrarReserva()
        {
            if (_sol == null || _reserva == EstadoReserva.Nenhuma && !_reservando) { seloReserva.Mostrar("", Theme.TextDark, Theme.CardBg); return; }
            const string cadeado = "";   // Lock (Segoe MDL2)
            if (_gravacaoIncerta) seloReserva.Mostrar("Conferir no MV", "", Theme.Warning, Theme.WarningBg);   // Warning (Segoe MDL2)
            // Reservando / reservada: é o fluxo normal da farmácia — sem selo.
            else if (_reservando || _reserva == EstadoReserva.Reservada) seloReserva.Mostrar("", Theme.TextDark, Theme.CardBg);
            else if (_reserva == EstadoReserva.Ocupada) seloReserva.Mostrar("Em atendimento por outro usuário", cadeado, Theme.Error, Theme.ErrorBg);
            else if (_reserva == EstadoReserva.Liberada) seloReserva.Mostrar("Reserva liberada", Theme.Warning, Theme.WarningBg);
            else seloReserva.Mostrar("Sem reserva", Theme.Warning, Theme.WarningBg);
        }

        // O atendente mexeu na aba: zera a contagem de inatividade; se a reserva tinha sido
        // solta, reserva de novo.
        private void Tocar()
        {
            _ultimoToque = DateTime.Now;
            if (_avisouInatividade)
            {
                _avisouInatividade = false;
                avisos.Fechar(_avisoInatividade);   // voltou a mexer: o aviso não vale mais
                _avisoInatividade = null;
            }
            ReservaAtendimento.Tocar();
            if (_reserva == EstadoReserva.Liberada && !_reservando && !_ocupado) ReservarDeNovo();
        }

        private async void ReservarDeNovo()
        {
            if (_sol == null || _reservando) return;
            _reservando = true;
            MostrarReserva();
            avisos.SetState(StatusKind.Busy, "Retomando a solicitação " + _sol.Numero + " no MV...");
            long numero = _sol.Numero;
            int carga = _carga;
            bool tenho = false;
            try
            {
                tenho = await MvGate.EntrarAsync("atender-reservar", 60000);
                if (IsDisposed) return;
                if (!tenho) { _reservando = false; MostrarReserva(); avisos.SetState(StatusKind.Warning, "O MV está ocupado com outra consulta; a reserva é tentada de novo no próximo toque."); return; }
                var t = _preview ? await ReservaAtendimento.ReservarAsync(null, numero) : await ReservaAtendimento.ReservarAsync(_host.Session, numero);
                if (IsDisposed || carga != _carga || _sol == null || _sol.Numero != numero)
                {
                    if (t.Resultado == ResultadoReserva.Reservada) LiberarEmSegundoPlano(_host.Session, "a tela mudou durante a reserva", numero);
                    return;
                }
                AplicarReserva(t);
            }
            catch (SessaoExpiradaException) { if (!IsDisposed) _host.SessaoExpirou(); }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _reservando = false; MostrarReserva();
                avisos.SetState(StatusKind.Error, "Não foi possível reservar a solicitação: " + ex.Message);
            }
            finally
            {
                if (tenho) MvGate.Sair();
                _reservando = false;
                if (!IsDisposed) { MostrarReserva(); AtualizarBotoes(); AtualizarDica(); }
            }
        }

        // Inatividade: o "manter conectado" renova a sessão para sempre, então quem esquece a
        // aba aberta prenderia a solicitação. Avisa e, no prazo, solta (Retornar no MV).
        private async void OnRelogioReserva(object sender, EventArgs e)
        {
            if (IsDisposed || GravacaoEmAndamento || _sol == null || _reserva != EstadoReserva.Reservada || _liberandoInativa || ReservaAtendimento.Atual == null) return;
            double parado = (DateTime.Now - _ultimoToque).TotalSeconds;
            if (parado >= ReservaAtendimento.SegundosInatividade)
            {
                if (_ocupado || _buscando) return;
                _liberandoInativa = true;
                long numero = _sol.Numero;
                bool tenho = await MvGate.EntrarAsync("reserva-inatividade", 60000);
                ResultadoLiberacao res;
                try { res = await ReservaAtendimento.LiberarAsync(_host.Session, Minutos(ReservaAtendimento.SegundosInatividade) + " sem uso"); }
                finally { if (tenho) MvGate.Sair(); _liberandoInativa = false; }
                if (IsDisposed || _sol == null || _sol.Numero != numero) return;
                _reserva = EstadoReserva.Liberada;
                MostrarReserva();
                avisos.Fechar(_avisoInatividade); _avisoInatividade = null; _avisouInatividade = false;
                avisos.SetState(StatusKind.Warning, "A reserva da " + numero + " foi liberada no MV depois de " + Minutos(ReservaAtendimento.SegundosInatividade) +
                    " sem uso, para não prender a solicitação" + (res == ResultadoLiberacao.SemConexao ? " (sem conexão agora; o programa repete sozinho)" : "") +
                    ". Ao voltar a mexer aqui, ela é retomada.");
            }
            else if (parado >= ReservaAtendimento.SegundosAviso && !_avisouInatividade)
            {
                _avisouInatividade = true;
                _avisoInatividade = avisos.SetState(StatusKind.Warning, "Sem uso há " + Minutos(ReservaAtendimento.SegundosAviso) + ": em " +
                    Minutos(ReservaAtendimento.SegundosInatividade - ReservaAtendimento.SegundosAviso) + " a solicitação " + _sol.Numero +
                    " será liberada no MV. Qualquer tecla mantém a reserva.");
            }
        }

        private static string Minutos(int segundos)
        {
            if (segundos < 60) return segundos + " s";
            int m = (int)Math.Round(segundos / 60.0);
            return m + (m == 1 ? " minuto" : " minutos");
        }

        // Alt+Tab de volta (do MV, por ex.): o foco volta para a célula onde o atendente estava,
        // e para a digitação em andamento, se havia. Roda depois da ativação do WinForms (que
        // nem sempre devolvia o foco à grade); fica de fora se ele estava no nº da solicitação
        // ou se uma janela do app (F9, conferência) está aberta.
        private void OnVoltouAoApp(object sender, EventArgs e)
        {
            if (!_focoNaGrade || _sol == null || !this.Visible) return;
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || !this.Visible) return;
                foreach (Form f in Application.OpenForms) if (f.Modal && f.Visible) return;   // F9, conferência, aviso
                if (grid.IsCurrentCellInEditMode && grid.EditingControl != null)
                {
                    if (!grid.EditingControl.Focused) grid.EditingControl.Focus();
                    return;
                }
                if (grid.CurrentCell == null && _ultimaLinha >= 0 && _ultimaLinha < grid.Rows.Count && _ultimaColuna >= 0)
                    try { grid.CurrentCell = grid.Rows[_ultimaLinha].Cells[_ultimaColuna]; } catch { }
                if (!grid.Focused) grid.Focus();
            });
        }

        private void OnLoad(object sender, EventArgs e)
        {
            if (_preview)
            {
                txtNumero.Text = "484416";
                CarregarExemplo();
                return;
            }
            AtualizarDica();
            if (_host.Session == null) { avisos.SetState(StatusKind.Warning, "Faça login para atender solicitações."); return; }
            string recado = ReservaAtendimento.Recado;
            if (!string.IsNullOrEmpty(recado)) { ReservaAtendimento.Recado = null; avisos.SetState(StatusKind.Info, recado); }
            this.BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) txtNumero.Focus(); });
        }

        public void ComandoAtualizar() { if (_sol != null) Abrir(); }
        public void ComandoFocarBusca() { txtNumero.Focus(); txtNumero.SelectAll(); }

        // ===== MONTAGEM DA TELA =====
        private Control BuildCabecalho()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;
            card.Padding = new Padding(Theme.S(14), Theme.S(8), Theme.S(14), Theme.S(2));
            card.Margin = new Padding(0, 0, 0, Theme.S(8));

            // Os 8 campos da tela do MV em 5, numa linha só (aos pares): em 1366x768 duas linhas
            // de campos deixavam a grade com 5 linhas visíveis.
            float[] pesos = { 17f, 22f, 23f, 14f, 24f };
            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.AutoSize = true;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = pesos.Length;
            foreach (float p in pesos) tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, p));
            tl.RowCount = 2;
            for (int i = 0; i < 2; i++) tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Linha 1: à esquerda a solicitação aberta (o nº em destaque e os selos de tipo,
            // situação e urgência); à direita, depois de um divisor, o campo do nº (o leitor de
            // código de barras "digita" e manda Enter), o Abrir e a borracha (limpar a tela).
            var barra = new TableLayoutPanel();
            barra.Dock = DockStyle.Fill; barra.AutoSize = true; barra.BackColor = Theme.CardBg;
            barra.ColumnCount = 6; barra.RowCount = 1;
            barra.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            barra.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            barra.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            barra.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(150)));
            barra.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            barra.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            barra.Margin = new Padding(0, 0, 0, Theme.S(6));

            var info = new FlowLayoutPanel();
            info.AutoSize = true; info.WrapContents = false; info.BackColor = Theme.CardBg;
            info.Anchor = AnchorStyles.Left;
            info.Margin = new Padding(0);
            lblNumero = new Label();
            lblNumero.AutoSize = true; lblNumero.BackColor = Theme.CardBg;
            lblNumero.Margin = new Padding(0, Theme.S(2), Theme.S(4), 0);
            info.Controls.Add(lblNumero);
            seloTipo = new Selo(); seloSituacao = new Selo(); seloReserva = new Selo(); seloUrgente = new Selo();
            foreach (var s in new[] { seloTipo, seloSituacao, seloReserva, seloUrgente })
            {
                s.Font = FonteSelo; s.Height = Theme.S(22);
                s.Margin = new Padding(Theme.S(6), Theme.S(1), 0, 0);
                info.Controls.Add(s);
            }
            barra.Controls.Add(info, 0, 0);

            var divisor = new Panel();
            divisor.BackColor = Theme.BorderSoft;
            divisor.Width = 1; divisor.Height = Theme.S(26);
            divisor.Anchor = AnchorStyles.Left;
            divisor.Margin = new Padding(Theme.S(14), 0, Theme.S(14), 0);
            barra.Controls.Add(divisor, 1, 0);

            var lblNum = new Label();
            lblNum.Text = "Solicitação";
            lblNum.Font = FonteGradeTitulo; lblNum.ForeColor = Theme.TextDark; lblNum.BackColor = Theme.CardBg;
            lblNum.AutoSize = true; lblNum.Anchor = AnchorStyles.Left;
            lblNum.Margin = new Padding(0, 0, Theme.S(8), 0);
            barra.Controls.Add(lblNum, 2, 0);

            var host = new FieldHost();
            host.Dock = DockStyle.Fill; host.Height = Theme.S(30);
            host.Padding = new Padding(Theme.S(8), Theme.S(6), Theme.S(6), Theme.S(4));
            host.Margin = new Padding(0);
            txtNumero = new TextBox();
            txtNumero.BorderStyle = BorderStyle.None;
            txtNumero.Font = new Font("Segoe UI", 10f); txtNumero.ForeColor = Theme.TextDark; txtNumero.BackColor = Color.White;
            txtNumero.Dock = DockStyle.Fill;
            txtNumero.KeyDown += new KeyEventHandler(delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; Abrir(); }
            });
            txtNumero.HandleCreated += new EventHandler(delegate { SendMessage(txtNumero.Handle, 0x1501, (IntPtr)1, "Bipe ou digite o nº"); });
            txtNumero.Enter += new EventHandler(delegate { AtualizarDica(); });
            host.Controls.Add(txtNumero);
            barra.Controls.Add(host, 3, 0);

            btnAbrir = new PrimaryButton();
            btnAbrir.Text = "Abrir";
            btnAbrir.Width = Theme.S(76); btnAbrir.Height = Theme.S(30);
            btnAbrir.Margin = new Padding(Theme.S(6), 0, 0, 0);
            btnAbrir.Click += new EventHandler(delegate { Abrir(); });
            barra.Controls.Add(btnAbrir, 4, 0);

            btnLimpar = new IconButton(IconButton.DesenharBorracha, "Limpar a tela para outra solicitação.");
            btnLimpar.Size = new Size(Theme.S(30), Theme.S(30));
            btnLimpar.Margin = new Padding(Theme.S(6), 0, 0, 0);
            btnLimpar.Click += new EventHandler(delegate { Limpar(true); });
            barra.Controls.Add(btnLimpar, 5, 0);

            tl.Controls.Add(barra, 0, 0);
            tl.SetColumnSpan(barra, pesos.Length);

            tl.Controls.Add(Campo("Estoque", out vEstoque), 0, 1);
            tl.Controls.Add(Campo("Setor / Unidade de Internação", out vSetor), 1, 1);
            tl.Controls.Add(Campo("Paciente · Leito", out vPaciente), 2, 1);
            tl.Controls.Add(Campo("Prestador", out vPrestador), 3, 1);
            var solicitante = Campo("Solicitante", out vSolicitante);
            solicitante.Margin = new Padding(0, 0, 0, Theme.S(4));   // o último termina na borda, alinhado com a borracha
            tl.Controls.Add(solicitante, 4, 1);

            card.Controls.Add(tl);
            return card;
        }

        // Campo só de leitura: legenda em cima e o valor numa faixa cinza (como os campos
        // desabilitados do MV). Texto que não cabe aparece inteiro ao passar o mouse.
        private Control Campo(string legenda, out Label valor)
        {
            var p = new TableLayoutPanel();
            p.Dock = DockStyle.Fill; p.AutoSize = true; p.BackColor = Theme.CardBg;
            p.ColumnCount = 1; p.RowCount = 2;
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(22)));
            p.Margin = new Padding(0, 0, Theme.S(8), Theme.S(4));

            var l = new Label();
            l.Text = legenda; l.AutoSize = true;
            l.Font = FonteRotulo; l.ForeColor = Theme.TextSoft; l.BackColor = Theme.CardBg;
            l.Margin = new Padding(0, 0, 0, Theme.S(2));
            l.Padding = new Padding(Theme.S(5), 0, 0, 0);   // alinhado com o texto do valor (que tem 5 de recuo)
            p.Controls.Add(l, 0, 0);

            valor = new Label();
            valor.Dock = DockStyle.Fill; valor.AutoEllipsis = true;
            valor.Font = FonteGrade; valor.ForeColor = Theme.TextDark; valor.BackColor = Theme.GridHeaderBg;
            valor.TextAlign = ContentAlignment.MiddleLeft;
            valor.Padding = new Padding(Theme.S(5), 0, Theme.S(4), 0);
            valor.Margin = new Padding(0);
            p.Controls.Add(valor, 0, 1);
            return p;
        }

        private Control BuildProdutos()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(14), Theme.S(8), Theme.S(14), Theme.S(8));
            card.Margin = new Padding(0, 0, 0, Theme.S(8));

            grid = new GridPro();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = false;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.AllowUserToResizeColumns = false;
            grid.AllowUserToOrderColumns = false;
            grid.ShowCellToolTips = true;
            grid.SemSelecaoInicial = false;
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;   // título em uma linha
            Compactar(grid);

            // Pesos (px a 96 DPI na largura mínima): todas as colunas dividem a largura da grade
            // nessa proporção — ocupa a tela toda, maximizada ou em janela.
            Col("produto", "Produto", 80, false, true, "9999999");
            Col("descricao", "Descrição", 360, false, false, null).MinimumWidth = Theme.S(200);
            Col("unidade", "Unidade", 100, false, false, "FR C/100ML");
            Col("solicitado", "Solicitado", 88, true, false, "9999");
            var ja = Col("jaatendido", "Já atend.", 84, true, false, "9999");
            ja.Visible = false;   // só aparece em solicitação parcial
            ja.HeaderCell.ToolTipText = "Já atendido em baixa anterior";
            Col("lote", "Lote", 130, false, true, "W00075854").HeaderCell.ToolTipText = "F9 mostra os lotes do produto neste estoque";
            Col("validade", "Validade", 90, false, false, "30/11/2030");
            Col("atender", "Atender", 84, true, true, "9999");
            // A 1ª coluna recua mais: a barra azul da linha atual fica na borda esquerda dela.
            var recuo = new Padding(Theme.S(12), 0, Theme.S(4), 0);
            grid.Columns[CProduto].DefaultCellStyle.Padding = recuo;
            grid.Columns[CProduto].HeaderCell.Style.Padding = recuo;
            // O editor fica DENTRO da moldura da célula atual: o painel de edição ocupa a célula
            // menos o padding, e sem folga em cima e embaixo cobria a borda de cima da moldura
            // (foto do usuário, 30/10/2026). O texto continua centralizado na vertical.
            foreach (DataGridViewColumn c in grid.Columns)
            {
                var p = c.DefaultCellStyle.Padding;
                c.DefaultCellStyle.Padding = new Padding(p.Left, Theme.S(3), p.Right, Theme.S(3));
            }
            RepartirSempre(grid);

            grid.CellFormatting += new DataGridViewCellFormattingEventHandler(OnFormatar);
            grid.CellPainting += new DataGridViewCellPaintingEventHandler(OnPintar);
            grid.CorEditor = Color.White;   // editor branco dentro da moldura: a célula em digitação salta da linha azul
            // Onde o atendente estava, para devolver o foco no Alt+Tab (Enter/Leave só mudam
            // quando o foco anda DENTRO da janela; trocar de programa não mexe neles).
            grid.CellMouseDown += new DataGridViewCellMouseEventHandler(delegate { Tocar(); });
            grid.Enter += new EventHandler(delegate { _focoNaGrade = true; AtualizarDica(); grid.Invalidate(); });
            grid.Leave += new EventHandler(delegate { _focoNaGrade = false; AtualizarDica(); grid.Invalidate(); });
            grid.CurrentCellChanged += new EventHandler(delegate
            {
                if (grid.CurrentCell != null) { _ultimaLinha = grid.CurrentCell.RowIndex; _ultimaColuna = grid.CurrentCell.ColumnIndex; }
                AtualizarDica();
            });
            grid.CellBeginEdit += new DataGridViewCellCancelEventHandler(OnComecarEdicao);
            grid.CellEndEdit += new DataGridViewCellEventHandler(OnFimEdicao);
            grid.CellDoubleClick += new DataGridViewCellEventHandler(delegate(object s, DataGridViewCellEventArgs ev)
            {
                if (ev.RowIndex >= 0) AbrirLista(ev.RowIndex, ev.ColumnIndex, true);
            });

            card.Controls.Add(grid);
            carregando = new CarregandoGrade();
            carregando.Dock = DockStyle.Fill;
            card.Controls.Add(carregando);
            return card;
        }

        // Abrindo a solicitação: a grade fica travada sob o véu com o spinner e a etapa até os
        // lotes chegarem (pedido do usuário, 30/10/2026: antes dava para navegar e digitar na
        // tabela ainda carregando).
        private void Carregando(string texto)
        {
            if (carregando.Visible) { carregando.Texto = texto; return; }
            carregando.Mostrar(grid, texto);
            grid.Enabled = false;
            AtualizarBotoes();   // lixeira e Fechar a Baixa também esperam
        }

        // Fim da carga: solta a grade. Se o foco estava no véu, vai para 'foco' (o nº da
        // solicitação quando a carga não deu em nada; null = quem chamou decide).
        private void TerminouCarga(Control foco)
        {
            if (!carregando.Visible) return;
            bool tinha = carregando.Focused;
            grid.Enabled = true;
            carregando.Esconder();
            AtualizarBotoes();
            if (tinha && foco != null && foco.Enabled) foco.Focus();
        }

        // Letra 9,5 e linhas de 28 px (também nas janelas de lote/produto e na conferência).
        public static void Compactar(DataGridView g)
        {
            g.DefaultCellStyle.Font = FonteGrade;
            g.DefaultCellStyle.Padding = new Padding(Theme.S(4), 0, Theme.S(4), 0);
            g.ColumnHeadersDefaultCellStyle.Font = FonteGradeTitulo;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(Theme.S(4), 0, Theme.S(4), 0);
            g.RowTemplate.Height = Theme.S(AlturaLinha);
            g.ColumnHeadersHeight = Theme.S(AlturaTitulo);
        }

        // Coluna com peso na largura da grade (ver Repartir).
        public class ColunaPeso : DataGridViewTextBoxColumn
        {
            public int Peso;   // 0 = fica no mínimo
            public override object Clone()
            {
                var c = (ColunaPeso)base.Clone();
                c.Peso = Peso;
                return c;
            }
        }

        // Coluna que divide a largura por peso (ver Repartir). O mínimo vem do conteúdo — o título
        // e uma amostra do valor (ex.: "30/11/2030") cabem inteiros —, então em janela estreita
        // quem cede é a Descrição, não "Prod…" e "30/11/20…". Recuos: número (à direita) com 12 px
        // antes da borda; texto com 8 px depois dela — o número de uma coluna não encosta no
        // texto da seguinte. O título usa o mesmo recuo da célula.
        public static ColunaPeso NovaColuna(string titulo, int peso, bool direita) { return NovaColuna(titulo, peso, direita, null); }

        public static ColunaPeso NovaColuna(string titulo, int peso, bool direita, string amostra)
        {
            var c = new ColunaPeso();
            c.HeaderText = titulo;
            c.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            c.Peso = peso;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            var recuo = direita ? new Padding(Theme.S(4), 0, Theme.S(12), 0) : new Padding(Theme.S(8), 0, Theme.S(4), 0);
            int folga = recuo.Horizontal + Theme.S(8);
            int minimo = TextRenderer.MeasureText(titulo, FonteGradeTitulo).Width + folga;
            if (amostra != null) minimo = Math.Max(minimo, TextRenderer.MeasureText(amostra, FonteGrade).Width + folga);
            c.MinimumWidth = Math.Max(Theme.S(40), minimo);
            c.DefaultCellStyle.Padding = recuo;
            c.HeaderCell.Style.Padding = recuo;
            if (direita)
            {
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            return c;
        }

        // Liga a divisão por peso na grade: refaz ao mudar de tamanho, ao mostrar/esconder uma
        // coluna (Já atend.) e quando a barra de rolagem vertical aparece ou some.
        public static void RepartirSempre(DataGridView g)
        {
            g.SizeChanged += new EventHandler(delegate { Repartir(g); });
            g.ColumnStateChanged += new DataGridViewColumnStateChangedEventHandler(delegate(object s, DataGridViewColumnStateChangedEventArgs e)
            {
                if (e.StateChanged == DataGridViewElementStates.Visible) Repartir(g);
            });
            foreach (Control c in g.Controls)
                if (c is VScrollBar) c.VisibleChanged += new EventHandler(delegate { Repartir(g); });
            Repartir(g);
        }

        // Cada coluna fica com a sua parte da largura útil pelo peso, sem descer do mínimo: a que
        // bate no mínimo fica nele e o resto é repartido de novo entre as outras. (O Fill do
        // DataGridView dá o mínimo a todas e só divide a SOBRA pelos pesos: em janela a Descrição
        // ficava com 229 px e Unidade/Validade com folga.)
        private static readonly List<DataGridView> _repartindo = new List<DataGridView>();

        public static void Repartir(DataGridView g)
        {
            if (_repartindo.Contains(g)) return;
            _repartindo.Add(g);
            try
            {
                // Larguras novas podem fazer a barra vertical aparecer ou sumir: refaz (até 3x).
                for (int i = 0; i < 3; i++)
                {
                    bool barra = BarraVertical(g);
                    RepartirUmaVez(g, barra);
                    if (BarraVertical(g) == barra) break;
                }
            }
            finally { _repartindo.Remove(g); }
        }

        private static bool BarraVertical(DataGridView g)
        {
            foreach (Control c in g.Controls) if (c is VScrollBar) return c.Visible;
            return false;
        }

        private static void RepartirUmaVez(DataGridView g, bool barra)
        {
            int livre = g.ClientSize.Width - (barra ? SystemInformation.VerticalScrollBarWidth : 0);
            if (g.RowHeadersVisible) livre -= g.RowHeadersWidth;
            var cols = new List<ColunaPeso>();
            foreach (DataGridViewColumn c in g.Columns)
            {
                if (!c.Visible) continue;
                var p = c as ColunaPeso;
                if (p == null) livre -= c.Width; else cols.Add(p);
            }
            if (cols.Count == 0 || livre <= 0) return;

            var noMinimo = new HashSet<ColunaPeso>();
            int resto; long pesos;
            while (true)
            {
                resto = livre; pesos = 0;
                foreach (var c in cols) if (noMinimo.Contains(c)) resto -= c.MinimumWidth; else pesos += c.Peso;
                bool mudou = false;
                foreach (var c in cols)
                    if (!noMinimo.Contains(c) && (pesos == 0 || resto * c.Peso / pesos < c.MinimumWidth)) { noMinimo.Add(c); mudou = true; }
                if (!mudou) break;
            }

            // O arredondamento (e, se todas ficaram no mínimo, a sobra) vai para a de maior peso.
            ColunaPeso maior = cols[0];
            foreach (var c in cols) if (c.Peso > maior.Peso) maior = c;
            int soma = 0;
            var largura = new Dictionary<ColunaPeso, int>();
            foreach (var c in cols)
            {
                int w = noMinimo.Contains(c) ? c.MinimumWidth : (int)(resto * c.Peso / pesos);
                largura[c] = w; soma += w;
            }
            if (soma < livre) largura[maior] += livre - soma;
            foreach (var c in cols) if (c.Width != largura[c]) c.Width = largura[c];
        }

        private DataGridViewTextBoxColumn Col(string nome, string titulo, int largura, bool direita, bool editavel, string amostra)
        {
            var c = NovaColuna(titulo, largura, direita, amostra);
            c.Name = nome;
            c.ReadOnly = !editavel;
            grid.Columns.Add(c);
            return c;
        }

        // Rodapé numa faixa: lixeira (o ícone da barra do MV) · dica do que fazer na célula atual
        // (como a barra de status do MV) · "Fechar a Baixa". As teclas ficam na dica de cada
        // botão. Sem a posição do cursor ao lado do botão (pedido do usuário, 30/10/2026).
        private Control BuildRodape()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;
            card.Padding = new Padding(Theme.S(14), Theme.S(6), Theme.S(14), Theme.S(6));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.AutoSize = true; tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 3; tl.RowCount = 1;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            btnExcluir = new IconButton(IconButton.DesenharLixeira, "Excluir linha (Del). Item pedido sem linha não é atendido e fica pendente.");
            btnExcluir.Size = new Size(Theme.S(30), Theme.S(30));
            btnExcluir.Margin = new Padding(0);
            btnExcluir.Click += new EventHandler(delegate { ExcluirLinha(); });
            tl.Controls.Add(btnExcluir, 0, 0);

            lblDica = new Label();
            lblDica.Dock = DockStyle.Fill; lblDica.AutoEllipsis = true;
            lblDica.TextAlign = ContentAlignment.MiddleLeft;
            lblDica.Font = FonteDica; lblDica.ForeColor = Theme.TextSoft; lblDica.BackColor = Theme.CardBg;
            lblDica.Margin = new Padding(Theme.S(12), 0, Theme.S(12), 0);
            tl.Controls.Add(lblDica, 1, 0);

            btnFechar = new PrimaryButton();
            btnFechar.Text = "Fechar a Baixa";
            btnFechar.Width = Theme.S(150); btnFechar.Height = Theme.S(30);
            btnFechar.Margin = new Padding(0);
            btnFechar.Click += new EventHandler(delegate { FecharBaixa(); });
            new ToolTip().SetToolTip(btnFechar, "F12 — confere lotes e quantidades e fecha a baixa no MV.");
            tl.Controls.Add(btnFechar, 2, 0);

            card.Controls.Add(tl);
            return card;
        }

        // Barra de dica do rodapé: o que fazer na célula onde o cursor está.
        private void AtualizarDica()
        {
            if (lblDica == null || grid == null) return;
            var cc = grid.CurrentCell;
            bool naGrade = grid.ContainsFocus;
            string dica;
            if (CarregandoAgora) dica = "Aguarde: a tabela libera quando a solicitação terminar de carregar.";
            else if (_sol == null) dica = _preview ? "Pré-visualização: sem consultar o MV." : "Bipe ou digite o nº da solicitação e tecle Enter.";
            else if (_gravacaoIncerta) dica = "Confira a solicitação no MV antes de tentar de novo.";
            else if (!Atendivel()) dica = "Solicitação aberta só para consulta.";
            else if (cc == null || !naGrade) dica = "F12 confere e fecha a baixa.";
            else
            {
                bool vazia = cc.RowIndex >= 0 && cc.RowIndex < _linhas.Count && _linhas[cc.RowIndex].Produto <= 0;
                if (vazia) dica = cc.ColumnIndex == CProduto ? "Produto: F9 mostra os produtos do estoque (ou digite o código)." : "Para incluir um produto, vá à coluna Produto e tecle F9.";
                else if (cc.ColumnIndex == CLote) dica = "Lote: F9 mostra os lotes do produto · Enter vai para Atender.";
                else if (cc.ColumnIndex == CAtender) dica = "Atender: digite a quantidade · Enter vai para a linha de baixo · Shift+Tab volta.";
                else dica = "Enter vai para o Lote · Del exclui a linha · F12 fecha a baixa.";
            }
            lblDica.Text = dica;
        }

        // ===== ABRIR A SOLICITAÇÃO (só leitura) =====
        private async void Abrir()
        {
            long numero;
            if (!long.TryParse((txtNumero.Text ?? "").Trim(), out numero) || numero <= 0)
            {
                avisos.SetState(StatusKind.Warning, "Digite ou bipe o nº da solicitação.");
                txtNumero.Focus(); return;
            }
            if (_ocupado) return;
            if (TemDigitacao() && _sol != null
                && !PerguntaMv.Perguntar(this, "Abrir a solicitação de novo?",
                    "Os lotes e quantidades digitados na solicitação " + _sol.Numero + " serão descartados.", "Descartar", "Voltar", false))
                return;
            avisos.Limpar();   // os avisos da solicitação anterior não valem mais
            if (_preview) { CarregarExemplo(); return; }
            if (_host.Session == null) { avisos.SetState(StatusKind.Warning, "Faça login para atender solicitações."); return; }

            int carga = ++_carga;
            _ocupado = true;
            Limpar(false);
            AtualizarBotoes();
            Carregando("Lendo a solicitação " + numero + " no MV...");
            bool tenho = false;
            try
            {
                tenho = await MvGate.EntrarAsync("atender-ler", 60000);
                if (IsDisposed || carga != _carga) return;
                if (!tenho) { avisos.SetState(StatusKind.Warning, "O MV está respondendo outra consulta. Tente de novo em instantes."); return; }

                // A solicitação anterior sai da reserva antes de qualquer outra coisa.
                if (ReservaAtendimento.Atual != null) await ReservaAtendimento.LiberarAsync(_host.Session, "abriu outra solicitação");
                if (IsDisposed || carga != _carga) return;

                var sol = await MorphisClient.LerSolicitacaoParaAtenderAsync(_host.Session, numero);
                if (IsDisposed || carga != _carga) return;
                if (sol == null)
                {
                    avisos.SetState(StatusKind.Warning, "A solicitação " + numero + " não foi encontrada no MV.");
                    txtNumero.Focus(); txtNumero.SelectAll();
                    return;
                }
                // Nome de quem pediu (a consulta só traz o login). Só para exibir: se falhar, fica o login.
                try { await MorphisClient.LerNomesDaBaixaAsync(_host.Session, sol); }
                catch (SessaoExpiradaException) { throw; }
                catch (Exception ex) { Logger.Log("Atender: nomes da " + numero + " não lidos: " + ex.Message); }
                if (IsDisposed || carga != _carga) return;
                _sol = sol;
                MostrarCabecalho();
                MontarLinhas();
                carregando.Fotografar(grid);   // os itens aparecem sob o véu enquanto o resto carrega
                if (sol.Devolucao)
                {
                    avisos.SetState(StatusKind.Warning, "A " + numero + " é uma devolução (" + sol.TipoDescricao + "): por enquanto, atenda pelo MV.");
                    return;
                }
                if (!sol.Aberta)
                {
                    avisos.SetState(StatusKind.Warning, "A " + numero + " está " + sol.SituacaoDescricao.ToLowerInvariant() + ": não há o que atender.");
                    return;
                }

                // Reserva no MV ("Confirmar produtos"): enquanto estiver aqui, ninguém mais começa
                // a atender esta solicitação. Ocupada por outro = fica só para consulta.
                _reservando = true; MostrarReserva();
                Carregando("Abrindo a solicitação " + numero + " no MV...");
                var reserva = await ReservaAtendimento.ReservarAsync(_host.Session, numero);
                if (IsDisposed || carga != _carga)
                {
                    if (reserva.Resultado == ResultadoReserva.Reservada) LiberarEmSegundoPlano(_host.Session, "a tela mudou durante a reserva", numero);
                    return;
                }
                if (!AplicarReserva(reserva)) return;

                Carregando("Lendo os lotes dos " + sol.Itens.Count + " produtos em " + sol.DsEstoque + "...");
                var codigos = new List<int>();
                foreach (var it in sol.Itens) codigos.Add(it.Produto);
                var lotes = await AtenderSolicitacao.LotesAsync(_host.Session, sol.Estoque, codigos, true);
                if (IsDisposed || carga != _carga) return;
                foreach (var kv in lotes) _lotes[kv.Key] = kv.Value;
                grid.Invalidate();
                TerminouCarga(null);   // grade liberada antes de receber o foco
                IrPara(0, CLote, false);
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed) _host.SessaoExpirou();
                return;
            }
            catch (Exception ex)
            {
                Logger.Log("Atender: erro ao abrir " + numero + ": " + ex.Message);
                if (!IsDisposed && carga == _carga) avisos.SetState(StatusKind.Error, "Não foi possível ler a solicitação: " + ex.Message);
            }
            finally
            {
                if (tenho) MvGate.Sair();
                _reservando = false;
                if (carga == _carga) { _ocupado = false; if (!IsDisposed) { MostrarReserva(); AtualizarBotoes(); TerminouCarga(txtNumero); } }
            }
            if (!IsDisposed && carga == _carga && Atendivel()) PrepararProdutos(_sol.Estoque);
        }

        // Editável só com a reserva no MV (ou solta por inatividade: o próximo toque reserva de novo).
        private bool Atendivel()
        {
            return _sol != null && _sol.Aberta && !_sol.Devolucao &&
                   (_reserva == EstadoReserva.Reservada || _reserva == EstadoReserva.Liberada) && !_gravacaoIncerta;
        }

        // A lista do F9 de produtos (Posição de Estoque, ~3 s) é lida em segundo plano depois
        // que a solicitação abre; fica em cache por alguns minutos para as próximas.
        private void PrepararProdutos(int estoque)
        {
            if (_produtos != null && _produtosEstoque == estoque && !_produtos.IsFaulted) return;
            _produtosEstoque = estoque;
            if (_preview) { _produtos = Task.FromResult(ProdutosExemplo()); return; }
            _produtos = LerProdutosAsync(estoque);
        }

        private async Task<List<SaldoProduto>> LerProdutosAsync(int estoque)
        {
            var c = AtenderSolicitacao.ProdutosEmCache(estoque);
            if (c != null) return c;
            var s = _host.Session;
            if (s == null) throw new Exception("sem sessão");
            bool tenho = await MvGate.EntrarAsync("atender-produtos", 120000);
            if (!tenho) throw new Exception("o MV está ocupado com outra consulta");
            try { return await AtenderSolicitacao.ProdutosAsync(s, estoque); }
            finally { MvGate.Sair(); }
        }

        // Lotes de um produto incluído na hora (os dos itens pedidos vêm todos ao abrir).
        private async Task<LotesProduto> GarantirLotesAsync(int produto)
        {
            LotesProduto lp;
            if (_lotes.TryGetValue(produto, out lp)) return lp;
            if (_preview) { lp = new LotesProduto(); lp.Produto = produto; _lotes[produto] = lp; return lp; }
            var c = AtenderSolicitacao.LotesEmCache(_sol.Estoque, produto);
            if (c != null) { _lotes[produto] = c; return c; }
            bool tenho = await MvGate.EntrarAsync("atender-lotes", 60000);
            if (!tenho) throw new Exception("o MV está ocupado com outra consulta");
            try
            {
                var lido = await AtenderSolicitacao.LotesAsync(_host.Session, _sol.Estoque, new[] { produto }, false);
                lido.TryGetValue(produto, out lp);
            }
            finally { MvGate.Sair(); }
            if (lp == null) { lp = new LotesProduto(); lp.Produto = produto; }
            _lotes[produto] = lp;
            return lp;
        }

        // ===== CABEÇALHO E GRADE =====
        private void MostrarCabecalho()
        {
            var s = _sol;
            vEstoque.Text = s == null ? "" : (s.DsEstoque.Length > 0 ? s.DsEstoque : s.Estoque.ToString());
            vSetor.Text = s == null ? "" : Juntar(s.Setor,
                Normalizar(s.UnidadeInternacao).Length > 0 && Normalizar(s.UnidadeInternacao) != Normalizar(s.Setor) ? s.UnidadeInternacao : "", " / ");
            vPaciente.Text = s == null ? "" : Juntar(s.Paciente, s.Leito, "  ·  ");
            vPrestador.Text = s == null ? "" : s.Prestador;
            // Os dois primeiros nomes de quem pediu; sem o nome, o login. Na dica: nome inteiro,
            // login e quando pediu.
            string nome = s == null ? "" : AtenderSolicitacao.DoisNomes(s.SolicitanteNome);
            vSolicitante.Text = s == null ? "" : (nome.Length > 0 ? nome : s.Solicitante);
            string dica = null;
            if (s != null)
            {
                dica = Juntar(s.SolicitanteNome, s.Solicitante.Length > 0 ? "(" + s.Solicitante + ")" : "", " ");
                if (s.Data.HasValue) dica = Juntar(dica, "pediu em " + s.Data.Value.ToString("dd/MM/yyyy HH:mm"), " — ");
            }
            _dicaCampos.SetToolTip(vSolicitante, dica);

            if (s == null)
            {
                lblNumero.Text = "Nenhuma solicitação aberta";
                lblNumero.Font = FonteGrade; lblNumero.ForeColor = Theme.TextSoft;
                lblNumero.Margin = new Padding(0, Theme.S(6), Theme.S(6), 0);
                seloTipo.Mostrar("", Theme.TextDark, Theme.CardBg);
                seloSituacao.Mostrar("", Theme.TextDark, Theme.CardBg);
                seloUrgente.Mostrar("", Theme.TextDark, Theme.CardBg);
                MostrarReserva();
                return;
            }
            MostrarReserva();
            lblNumero.Text = "Nº " + s.Numero;
            lblNumero.Font = FonteNumero; lblNumero.ForeColor = Theme.TextDark;
            lblNumero.Margin = new Padding(0, Theme.S(2), Theme.S(6), 0);
            seloTipo.Mostrar(TipoCurto(s.Tipo), Theme.Primary, Theme.InfoBg);
            Color cor, fundo;
            switch (s.Situacao)
            {
                case "S": cor = Theme.Success; fundo = Theme.SuccessBg; break;
                case "A": cor = Theme.Error; fundo = Theme.ErrorBg; break;
                default: cor = Theme.Warning; fundo = Theme.WarningBg; break;   // pendente / parcial: falta atender
            }
            seloSituacao.Mostrar(s.SituacaoDescricao, cor, fundo);
            seloUrgente.Mostrar(s.Urgente ? "URGENTE" : "", Color.White, Theme.Error);
        }

        private static string TipoCurto(string t)
        {
            switch (t)
            {
                case "P": return "Pedido de paciente";
                case "S": return "Pedido de setor";
                case "G": return "Pedido de gasto de sala";
                case "E": return "Pedido de estoque";
                case "T": return "Pedido de empresa";
                case "C": return "Devolução de paciente";
                case "D": return "Devolução de setor";
                case "L": return "Devolução de gasto de sala";
                default: return AtenderSolicitacao.DescreverTipo(t);
            }
        }

        private static string Juntar(string a, string b, string sep)
        {
            a = (a ?? "").Trim(); b = (b ?? "").Trim();
            if (a.Length == 0) return b;
            return b.Length == 0 ? a : a + sep + b;
        }

        // "4º ANDAR" e "4° ANDAR" (o MV usa os dois) contam como o mesmo nome.
        private static string Normalizar(string s)
        {
            return PainelPam.SemAcento((s ?? "").Replace("°", "º")).Trim();
        }

        private void MontarLinhas()
        {
            _linhas.Clear();
            bool jaAtendido = false;
            if (_sol != null)
                foreach (var it in _sol.Itens)
                {
                    var l = new LinhaAtender();
                    l.Produto = it.Produto; l.Descricao = it.Descricao; l.Unidade = it.Unidade;
                    _linhas.Add(l);
                    if (it.Atendido > 0) jaAtendido = true;
                }
            if (Atendivel()) _linhas.Add(new LinhaAtender());   // a linha vazia do fim
            grid.Columns[CJaAtendido].Visible = jaAtendido;
            Renderizar();
        }

        // Escolhido o produto na linha vazia, outra linha vazia entra no fim.
        private void GarantirLinhaVazia()
        {
            if (!Atendivel()) return;
            if (_linhas.Count > 0 && _linhas[_linhas.Count - 1].Produto <= 0) return;
            var l = new LinhaAtender();
            _linhas.Add(l);
            _montando = true;
            int r = grid.Rows.Add();
            grid.Rows[r].Tag = l;
            PreencherLinha(grid.Rows[r]);
            _montando = false;
        }

        private void Renderizar()
        {
            _montando = true;
            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var l in _linhas)
            {
                int r = grid.Rows.Add();
                grid.Rows[r].Tag = l;
                PreencherLinha(grid.Rows[r]);
            }
            grid.ResumeLayout();
            _montando = false;
            AtualizarBotoes();
        }

        // A primeira linha do produto mostra o solicitado; as seguintes (outro lote) não repetem,
        // para não parecer pedido em dobro.
        private bool PrimeiraDoProduto(int indice)
        {
            int p = _linhas[indice].Produto;
            for (int i = 0; i < indice; i++) if (_linhas[i].Produto == p) return false;
            return true;
        }

        private void PreencherLinha(DataGridViewRow row)
        {
            var l = row.Tag as LinhaAtender;
            if (l == null) return;
            if (l.Produto <= 0)
            {
                row.Cells[CProduto].Value = "";
                row.Cells[CDescricao].Value = TextoLinhaVazia;
                for (int c = CUnidade; c <= CAtender; c++) row.Cells[c].Value = "";
                row.Cells[CProduto].ReadOnly = false;
                return;
            }
            var it = _sol != null ? _sol.Item(l.Produto) : null;
            bool primeira = row.Index < 0 || PrimeiraDoProduto(row.Index);
            row.Cells[CProduto].Value = l.Produto.ToString();
            row.Cells[CDescricao].Value = l.Descricao ?? "";
            row.Cells[CUnidade].Value = l.Unidade ?? "";
            row.Cells[CSolicitado].Value = it != null && primeira ? AtenderSolicitacao.Num(it.Solicitado) : "";
            row.Cells[CJaAtendido].Value = it != null && primeira && it.Atendido > 0 ? AtenderSolicitacao.Num(it.Atendido) : "";
            row.Cells[CLote].Value = l.Lote ?? "";
            row.Cells[CValidade].Value = l.Validade.HasValue ? l.Validade.Value.ToString("dd/MM/yyyy") : "";
            row.Cells[CAtender].Value = l.Quantidade.HasValue ? AtenderSolicitacao.Num(l.Quantidade.Value) : "";
            row.Cells[CProduto].ReadOnly = true;
        }

        private LoteDisponivel LoteDaLinha(LinhaAtender l)
        {
            if (l == null || l.Produto <= 0 || string.IsNullOrEmpty(l.Lote)) return null;
            LotesProduto lp;
            return _lotes.TryGetValue(l.Produto, out lp) ? lp.Achar(l.Lote) : null;
        }

        private decimal TotalDoProduto(int produto)
        {
            decimal t = 0;
            foreach (var l in _linhas) if (l.Produto == produto && l.Quantidade.HasValue) t += l.Quantidade.Value;
            return t;
        }

        private void OnFormatar(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var l = grid.Rows[e.RowIndex].Tag as LinhaAtender;
            if (l == null) return;
            var cel = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];

            if (l.Produto <= 0)
            {
                if (e.ColumnIndex == CDescricao)
                {
                    e.CellStyle.ForeColor = Theme.TextSoft; e.CellStyle.SelectionForeColor = Theme.TextSoft;
                    e.CellStyle.Font = FonteLinhaVazia;
                }
                return;
            }
            // Produto fora do pedido aparece igual aos outros (Solicitado em branco); a conferência
            // do F12 mostra a situação.
            if ((e.ColumnIndex == CDescricao || e.ColumnIndex == CProduto) && l.ForaDoPedido)
            {
                cel.ToolTipText = "Produto fora do pedido: o MV pergunta se quer continuar e a aba confirma.";
                return;
            }

            if (e.ColumnIndex == CLote && !string.IsNullOrEmpty(l.Lote))
            {
                LotesProduto lp;
                bool lido = _lotes.TryGetValue(l.Produto, out lp);
                var lote = lido ? lp.Achar(l.Lote) : null;
                if (lido && (lote == null || lote.Vencido))
                {
                    e.CellStyle.ForeColor = Theme.Error; e.CellStyle.SelectionForeColor = Theme.Error;
                    cel.ToolTipText = lote == null ? "Este lote não tem saldo neste estoque (F9 mostra os disponíveis)." : "Lote vencido.";
                }
                else cel.ToolTipText = lote != null ? "Saldo do lote: " + AtenderSolicitacao.Num(lote.Quantidade) : "";
                return;
            }
            if (e.ColumnIndex == CValidade && l.Validade.HasValue)
            {
                int dias = (int)(l.Validade.Value.Date - DateTime.Today).TotalDays;
                if (dias < AtenderSolicitacao.DiasVencendo)
                {
                    e.CellStyle.ForeColor = Theme.Warning; e.CellStyle.SelectionForeColor = Theme.Warning;
                    cel.ToolTipText = dias < 0 ? "Vencido." : "Vence em " + dias + " dia(s).";
                }
                return;
            }
            if (e.ColumnIndex == CAtender && l.Quantidade.HasValue)
            {
                var it = _sol != null ? _sol.Item(l.Produto) : null;
                if (it == null) return;
                decimal tot = TotalDoProduto(l.Produto);
                if (tot != it.Pendente)
                {
                    e.CellStyle.ForeColor = Theme.Warning; e.CellStyle.SelectionForeColor = Theme.Warning;
                    cel.ToolTipText = "Total do produto: " + AtenderSolicitacao.Num(tot) + " de " + AtenderSolicitacao.Num(it.Pendente) +
                        (tot < it.Pendente ? " (fica parcial)." : " (a mais).");
                }
                else cel.ToolTipText = "";
            }
        }

        // Onde o cursor está (auditoria de 30/10/2026: o azul-claro da linha sumia na tela):
        //   linha atual: o azul da seleção + barra azul forte na borda esquerda;
        //   célula atual: fundo branco com moldura azul de 2 px (em qualquer coluna, e também
        //   durante a digitação — o editor fica branco dentro dela).
        // Com o foco fora da grade (no nº da solicitação), a moldura fica cinza.
        private void OnPintar(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var cc = grid.CurrentCell;
            if (cc == null || cc.RowIndex != e.RowIndex) return;
            bool celula = cc.ColumnIndex == e.ColumnIndex;
            bool primeira = e.ColumnIndex == PrimeiraColunaVisivel();
            if (!celula && !primeira) return;
            bool foco = grid.ContainsFocus;
            Color cor = foco ? Theme.Primary : Theme.Border;

            if (celula)
            {
                using (var b = new SolidBrush(Color.White)) e.Graphics.FillRectangle(b, e.CellBounds);
                if (!grid.IsCurrentCellInEditMode)
                    e.Paint(e.ClipBounds, DataGridViewPaintParts.ContentForeground);
                var r = e.CellBounds; r.Width -= 2; r.Height -= 2; r.X += 1; r.Y += 1;
                using (var pen = new Pen(cor, 2f)) e.Graphics.DrawRectangle(pen, r);
            }
            else e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.Focus);
            if (primeira)
                using (var b = new SolidBrush(cor)) e.Graphics.FillRectangle(b, e.CellBounds.X, e.CellBounds.Y, Theme.S(4), e.CellBounds.Height - 1);
            e.Handled = true;
        }

        private int PrimeiraColunaVisivel()
        {
            foreach (DataGridViewColumn c in grid.Columns) if (c.Visible) return c.Index;
            return 0;
        }

        private bool Editavel(int linha, int coluna)
        {
            if (!Atendivel() || _ocupado || linha < 0 || linha >= _linhas.Count) return false;
            if (_linhas[linha].Produto <= 0) return coluna == CProduto;
            return coluna == CLote || coluna == CAtender;
        }

        private void OnComecarEdicao(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (!Editavel(e.RowIndex, e.ColumnIndex)) e.Cancel = true;
        }

        private void OnFimEdicao(object sender, DataGridViewCellEventArgs e)
        {
            if (_montando || e.RowIndex < 0 || e.RowIndex >= _linhas.Count) return;
            var row = grid.Rows[e.RowIndex];
            var l = _linhas[e.RowIndex];
            string txt = Convert.ToString(row.Cells[e.ColumnIndex].Value ?? "").Trim();

            if (e.ColumnIndex == CProduto && l.Produto <= 0)
            {
                PreencherLinha(row);
                if (txt.Length == 0) return;
                int cod;
                if (int.TryParse(txt, out cod) && cod > 0) DefinirProdutoPorCodigo(e.RowIndex, cod);
                else avisos.SetState(StatusKind.Warning, "Código inválido. Tecle F9 para procurar o produto pelo nome.");
                return;
            }
            if (e.ColumnIndex == CAtender)
            {
                if (txt.Length == 0) l.Quantidade = null;
                else
                {
                    var q = AtenderSolicitacao.LerNumero(txt);
                    if (!q.HasValue || q.Value < 0) avisos.SetState(StatusKind.Warning, "Quantidade inválida: \"" + txt + "\".");
                    else l.Quantidade = q.Value == 0 ? (decimal?)null : q.Value;
                }
            }
            else if (e.ColumnIndex == CLote)
            {
                l.Lote = txt.Length == 0 ? null : txt;
                var lote = LoteDaLinha(l);
                if (lote != null) { l.Lote = lote.Lote; l.Validade = lote.Validade; }
                else
                {
                    l.Validade = null;
                    if (txt.Length > 0) avisos.SetState(StatusKind.Warning, "O lote \"" + txt + "\" não tem saldo deste produto no estoque. F9 mostra os disponíveis.");
                }
            }
            PreencherLinha(row);
            grid.InvalidateColumn(CAtender);
        }

        private async void DefinirProdutoPorCodigo(int linha, int cod)
        {
            try
            {
                SaldoProduto achado = null;
                var it = _sol != null ? _sol.Item(cod) : null;
                if (it == null && _produtos != null)
                {
                    avisos.SetState(StatusKind.Busy, "Procurando o produto " + cod + "...");
                    var lista = await _produtos;
                    foreach (var p in lista) if (p.Codigo == cod) { achado = p; break; }
                }
                if (IsDisposed) return;
                if (achado == null && it == null)
                {
                    avisos.SetState(StatusKind.Warning, "O produto " + cod + " não tem saldo em " + _sol.DsEstoque + ". Tecle F9 para procurar pelo nome.");
                    return;
                }
                await DefinirProduto(linha, cod, it != null ? it.Descricao : achado.Nome, it != null ? it.Unidade : achado.Unidade);
            }
            catch (SessaoExpiradaException) { if (!IsDisposed) _host.SessaoExpirou(); }
            catch (Exception ex)
            {
                if (!IsDisposed) avisos.SetState(StatusKind.Error, "Não foi possível ler os produtos do estoque: " + ex.Message);
            }
        }

        // O produto escolhido vai para a linha vazia, que passa a ser uma linha comum (código,
        // descrição e unidade, como as pedidas); outra linha vazia entra no fim.
        private async Task DefinirProduto(int linha, int cod, string descricao, string unidade)
        {
            if (linha < 0 || linha >= _linhas.Count) return;
            bool fora = _sol.Item(cod) == null;
            if (fora && !PerguntaMv.Perguntar(this, "Produto não solicitado",
                    cod + "  " + descricao + "\n\nEsse produto não foi solicitado nesse pedido, deseja incluir?", "Incluir", "Não incluir", true))
            {
                if (IsDisposed) return;
                IrPara(linha, CProduto, false);
                return;
            }
            if (IsDisposed) return;
            var l = _linhas[linha];
            l.Produto = cod; l.Descricao = descricao; l.Unidade = unidade;
            l.ForaDoPedido = fora;
            PreencherLinha(grid.Rows[linha]);
            GarantirLinhaVazia();
            avisos.SetState(StatusKind.Busy, "Lendo os lotes de " + descricao + "...");
            _buscando = true; AtualizarBotoes();
            try { await GarantirLotesAsync(cod); }
            finally { _buscando = false; if (!IsDisposed) AtualizarBotoes(); }
            if (IsDisposed) return;
            IrPara(linha, CLote, false);
            AbrirLotes(linha);
        }

        // ===== F9: LOTES E PRODUTOS =====
        // F9 só onde há lista: no Lote, os lotes do produto; no Produto da linha vazia do fim, os
        // produtos do estoque. Em qualquer outra célula (Atender, Descrição...), nada.
        private void AbrirF9()
        {
            var cc = grid.CurrentCell;
            if (cc != null) AbrirLista(cc.RowIndex, cc.ColumnIndex, false);
        }

        // Duplo clique: o mesmo do F9, e na linha vazia também pela Descrição (onde está o convite).
        private void AbrirLista(int linha, int coluna, bool clique)
        {
            if (!Atendivel() || _ocupado || _buscando) return;
            if (linha < 0 || linha >= _linhas.Count) return;
            bool vazia = _linhas[linha].Produto <= 0;
            bool lotes = !vazia && coluna == CLote;
            bool produtos = vazia && (coluna == CProduto || (clique && coluna == CDescricao));
            if (!lotes && !produtos) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            if (lotes) AbrirLotes(linha); else AbrirProdutos(linha);
        }

        private async void AbrirLotes(int linha)
        {
            if (linha < 0 || linha >= _linhas.Count) return;
            var l = _linhas[linha];
            LotesProduto lp;
            try
            {
                if (!_lotes.TryGetValue(l.Produto, out lp))
                {
                    avisos.SetState(StatusKind.Busy, "Lendo os lotes de " + l.Descricao + "...");
                    _buscando = true; AtualizarBotoes();
                    try { lp = await GarantirLotesAsync(l.Produto); }
                    finally { _buscando = false; if (!IsDisposed) AtualizarBotoes(); }
                    if (IsDisposed) return;
                }
            }
            catch (SessaoExpiradaException) { if (!IsDisposed) _host.SessaoExpirou(); return; }
            catch (Exception ex) { avisos.SetState(StatusKind.Error, "Não foi possível ler os lotes: " + ex.Message); return; }

            if (lp.Lotes.Count == 0)
            {
                avisos.SetState(StatusKind.Warning, l.Produto + " " + l.Descricao + " não tem lote com saldo em " + _sol.DsEstoque + ".");
                return;
            }
            var linhas = new List<string[]>();
            var bloqueio = new List<string>();
            foreach (var lo in lp.Lotes)
            {
                linhas.Add(new[] {
                    lo.Lote,
                    lo.Validade.HasValue ? lo.Validade.Value.ToString("dd/MM/yyyy") : "",
                    AtenderSolicitacao.Num(lo.Quantidade),
                    lo.EmKit > 0 ? AtenderSolicitacao.Num(lo.EmKit) : "" });
                bloqueio.Add(lo.Vencido ? "Lote vencido em " + lo.Validade.Value.ToString("dd/MM/yyyy") : null);
            }
            var cols = new[] {
                new ListaEscolhaDialog.Coluna("Lote", 0, false, true),
                new ListaEscolhaDialog.Coluna("Validade", 100, false, false),
                new ListaEscolhaDialog.Coluna("Saldo", 84, true, false),
                new ListaEscolhaDialog.Coluna("Em kit", 80, true, false) };
            int escolhido;
            // Sem busca: são poucos lotes; setas e Enter.
            using (var d = new ListaEscolhaDialog("Lotes do produto", l.Produto + "  " + l.Descricao,
                cols, linhas, bloqueio, null, TamanhoLotes))
            {
                d.ShowDialog(this.FindForm());
                escolhido = d.Escolhido;
            }
            if (IsDisposed) return;
            Tocar();   // escolher na lista conta como uso
            if (escolhido < 0) { IrPara(linha, CLote, false); return; }
            var sel = lp.Lotes[escolhido];
            l.Lote = sel.Lote; l.Validade = sel.Validade;
            PreencherLinha(grid.Rows[linha]);
            grid.InvalidateColumn(CAtender);
            IrPara(linha, CAtender, true);
        }

        private async void AbrirProdutos(int linha)
        {
            if (_sol == null) return;
            PrepararProdutos(_sol.Estoque);
            List<SaldoProduto> lista;
            try
            {
                if (!_produtos.IsCompleted)
                {
                    avisos.SetState(StatusKind.Busy, "Carregando os produtos de " + _sol.DsEstoque + "...");
                    _buscando = true; AtualizarBotoes();
                }
                try { lista = await _produtos; }
                finally { _buscando = false; if (!IsDisposed) AtualizarBotoes(); }
                if (IsDisposed) return;
            }
            catch (SessaoExpiradaException) { if (!IsDisposed) _host.SessaoExpirou(); return; }
            catch (Exception ex)
            {
                _produtos = null;
                if (!IsDisposed) avisos.SetState(StatusKind.Error, "Não foi possível ler os produtos do estoque: " + ex.Message);
                return;
            }

            avisos.Livre();
            var linhas = new List<string[]>();
            foreach (var p in lista)
                linhas.Add(new[] { p.Codigo.ToString(), p.Nome ?? "", p.Unidade ?? "", AtenderSolicitacao.Num(p.Saldo) });
            var cols = new[] {
                new ListaEscolhaDialog.Coluna("Código", 84, false, false),
                new ListaEscolhaDialog.Coluna("Descrição", 0, false, true),
                new ListaEscolhaDialog.Coluna("Unidade", 110, false, false),
                new ListaEscolhaDialog.Coluna("Saldo", 76, true, false) };
            int escolhido;
            using (var d = new ListaEscolhaDialog("Produtos do estoque", _sol.DsEstoque + "  ·  só produtos com saldo",
                cols, linhas, null, "Digite parte do nome ou o código", TamanhoProdutos))
            {
                d.ShowDialog(this.FindForm());
                escolhido = d.Escolhido;
            }
            if (IsDisposed) return;
            Tocar();
            if (escolhido < 0)
            {
                IrPara(linha, CProduto, false);
                return;
            }
            var sel = lista[escolhido];
            var it = _sol.Item(sel.Codigo);
            try { await DefinirProduto(linha, sel.Codigo, it != null ? it.Descricao : sel.Nome, it != null ? it.Unidade : sel.Unidade); }
            catch (SessaoExpiradaException) { if (!IsDisposed) _host.SessaoExpirou(); }
            catch (Exception ex) { if (!IsDisposed) avisos.SetState(StatusKind.Error, "Não foi possível ler os lotes: " + ex.Message); }
        }

        // ===== EXCLUIR, LIMPAR =====
        private void ExcluirLinha()
        {
            if (!Atendivel() || _ocupado || _buscando) return;
            var cc = grid.CurrentCell;
            if (cc == null || cc.RowIndex < 0 || cc.RowIndex >= _linhas.Count) return;
            int r = cc.RowIndex;
            var l = _linhas[r];
            if (l.Produto <= 0) return;   // a linha vazia do fim fica
            if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
            _linhas.RemoveAt(r);
            _montando = true;
            grid.Rows.RemoveAt(r);
            _montando = false;
            // A próxima linha do mesmo produto passa a ser a primeira: volta a mostrar o solicitado.
            for (int i = r; i < _linhas.Count; i++) if (_linhas[i].Produto == l.Produto) { PreencherLinha(grid.Rows[i]); break; }
            grid.InvalidateColumn(CAtender);
            IrPara(Math.Min(r, _linhas.Count - 1), cc.ColumnIndex == CProduto ? CLote : cc.ColumnIndex, false);
            bool pedido = _sol.Item(l.Produto) != null;
            avisos.SetState(StatusKind.Info, "Linha excluída: " + l.Produto + " " + l.Descricao +
                (pedido && !TemLinha(l.Produto) ? " (não será atendido; para voltar, inclua pela linha vazia do fim)." : "."));
        }

        private bool TemLinha(int produto) { foreach (var l in _linhas) if (l.Produto == produto) return true; return false; }

        private bool TemDigitacao()
        {
            foreach (var l in _linhas)
                if (l.Produto > 0 && (l.Quantidade.HasValue || !string.IsNullOrEmpty(l.Lote) || l.ForaDoPedido)) return true;
            return false;
        }

        private void Limpar(bool perguntar)
        {
            if (perguntar)
            {
                if (_ocupado) return;
                if (TemDigitacao() && !PerguntaMv.Perguntar(this, "Limpar a tela?",
                        "Os lotes e quantidades digitados serão descartados.", "Limpar", "Voltar", false))
                    return;
                avisos.Limpar();
                _carga++;
                if (_sol != null) LiberarEmSegundoPlano(_host.Session, "limpou a tela", _sol.Numero);
            }
            _sol = null;
            _reserva = EstadoReserva.Nenhuma; _reservando = false; _avisouInatividade = false; _gravacaoIncerta = false;
            _linhas.Clear();
            _lotes.Clear();
            MostrarCabecalho();
            Renderizar();
            if (perguntar)
            {
                txtNumero.Text = "";
                txtNumero.Focus();
            }
            AtualizarDica();
        }

        // ===== F12: CONFERIR E FECHAR =====
        private void FecharBaixa()
        {
            if (!Atendivel() || _ocupado || _buscando || CarregandoAgora) return;
            if (_reserva != EstadoReserva.Reservada || _reservando)
            {
                // Solta por inatividade: reserva de novo antes (o toque da tecla já pode ter pedido).
                if (!_reservando) ReservarDeNovo();
                return;
            }
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            var r = AtenderSolicitacao.Conferir(_sol, _linhas, delegate(int p) { LotesProduto lp; return _lotes.TryGetValue(p, out lp) ? lp : null; });
            if (r.Erros.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < r.Erros.Count && i < 8; i++) sb.AppendLine("• " + r.Erros[i]);
                if (r.Erros.Count > 8) sb.AppendLine("• e mais " + (r.Erros.Count - 8) + ".");
                if (_avisoCorrigir != null) avisos.Fechar(_avisoCorrigir);
                _avisoCorrigir = avisos.SetState(StatusKind.Warning, sb.ToString().TrimEnd(), "Antes de fechar a baixa, corrija:");
                if (r.PrimeiraLinhaComErro >= 0) IrPara(r.PrimeiraLinhaComErro, CLote, false);
                return;
            }
            // Corrigido: a lista do que faltava não vale mais.
            if (_avisoCorrigir != null) { avisos.Fechar(_avisoCorrigir); _avisoCorrigir = null; }
            DialogResult resp;
            string sub = "Solicitação " + _sol.Numero + "  ·  " + Origem() + "  ·  " + _sol.DsEstoque;
            using (var d = new ConferenciaDialog(sub, r)) resp = d.ShowDialog(this.FindForm());
            if (IsDisposed) return;
            if (resp != DialogResult.OK) { IrPara(grid.CurrentCell != null ? grid.CurrentCell.RowIndex : 0, CLote, false); return; }
            Gravar(r);
        }

        private AvisoMv _avisoCorrigir;   // o "Antes de fechar a baixa, corrija:" na tela

        // Gravação de uma baixa em curso: sair, trocar de módulo ou fechar o programa esperam.
        public static bool GravacaoEmAndamento { get; private set; }
        private bool _gravacaoIncerta;   // o Fechar saiu sem resposta clara: nada de reenviar até reabrir

        // Lança as linhas na tela reservada do MV e fecha a baixa (ver MorphisClient.GravarBaixaAsync).
        // Gravou: limpa a tela para o próximo bipe. Parou antes do Fechar: nada foi gravado — o
        // Retornar descarta o lançamento, a solicitação é reservada de novo e a linha do problema é
        // apontada. Resposta incerta no Fechar: confere pela consulta; continuando incerta, trava o
        // reenvio até a solicitação ser aberta de novo.
        private async void Gravar(ResumoAtender conferencia)
        {
            var linhas = AtenderSolicitacao.LinhasParaGravar(_sol, _linhas, conferencia);
            if (linhas.Count == 0) return;
            long numero = _sol.Numero;
            decimal atendidoAntes = 0;
            foreach (var it in _sol.Itens) atendidoAntes += it.Atendido;
            Session s = _preview ? null : _host.Session;

            _ocupado = true; GravacaoEmAndamento = true;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            AtualizarBotoes();
            avisos.SetState(StatusKind.Busy, "Gravando a baixa da " + numero + " no MV...");
            ResultadoBaixa res = null;
            string erroReserva = null;
            bool tenho = false;
            try
            {
                tenho = await MvGate.EntrarAsync("atender-gravar", 60000);
                if (IsDisposed) return;
                if (!tenho) { avisos.SetState(StatusKind.Warning, "O MV está respondendo outra consulta. Tecle F12 de novo em instantes."); return; }
                try
                {
                    res = await ReservaAtendimento.GravarAsync(s, numero, linhas, delegate(string p) { if (!IsDisposed) avisos.SetState(StatusKind.Busy, p); });
                }
                catch (SessaoExpiradaException) { throw; }
                catch (Exception ex)
                {
                    // Falha de rede no lançamento (antes do Fechar): nada foi gravado.
                    Logger.Log("Atender: gravação da " + numero + " interrompida: " + ex.Message);
                    res = new ResultadoBaixa();
                    res.Mensagem = "A conexão com o MV falhou no lançamento (" + ex.Message + "). Nada foi gravado.";
                }

                if (res.Incerta && !res.Gravada && !_preview)
                {
                    // O Fechar saiu e a resposta não deu certeza: a consulta diz se gravou.
                    avisos.SetState(StatusKind.Busy, "Conferindo no MV se a baixa da " + numero + " foi gravada...");
                    try
                    {
                        var depois = await MorphisClient.LerSolicitacaoParaAtenderAsync(s, numero);
                        decimal atendidoDepois = 0;
                        if (depois != null) foreach (var it in depois.Itens) atendidoDepois += it.Atendido;
                        if (depois != null && (depois.Situacao != _sol.Situacao || atendidoDepois > atendidoAntes))
                        {
                            res.Gravada = true; res.Incerta = false; res.Situacao = depois.Situacao;
                            ReservaAtendimento.Concluir(numero, "baixa conferida pela consulta depois de resposta incerta");
                        }
                    }
                    catch (Exception ex) { Logger.Log("Atender: conferência depois do Fechar incerto falhou: " + ex.Message); }
                }

                if (!res.Gravada && !res.Incerta)
                {
                    // Parou antes do Fechar: Retornar (descarta o lançamento) e reserva de novo.
                    await ReservaAtendimento.LiberarAsync(s, "a gravação parou antes do Fechar", numero);
                    try
                    {
                        var t = await ReservaAtendimento.ReservarAsync(s, numero);
                        if (!IsDisposed && _sol != null && _sol.Numero == numero) AplicarReserva(t);
                        if (t.Resultado != ResultadoReserva.Reservada) erroReserva = t.Mensagem;
                    }
                    catch (Exception ex) { erroReserva = ex.Message; _reserva = EstadoReserva.Liberada; }
                }
            }
            catch (SessaoExpiradaException)
            {
                GravacaoEmAndamento = false;
                if (!IsDisposed) _host.SessaoExpirou();
                return;
            }
            finally
            {
                if (tenho) MvGate.Sair();
                GravacaoEmAndamento = false;
                _ocupado = false;
                if (!IsDisposed) { MostrarReserva(); AtualizarBotoes(); }
            }
            if (IsDisposed || res == null) return;

            if (res.Gravada)
            {
                AtenderSolicitacao.LimparCache();   // saldos de lote mudaram
                string sit = res.Situacao == "C" ? "atendida parcialmente" : res.Situacao == "S" ? "confirmada" : "gravada";
                Limpar(false);
                txtNumero.Focus(); txtNumero.SelectAll();
                avisos.Limpar();   // os avisos desta solicitação não valem mais
                avisos.SetState(StatusKind.Success, "Baixa da " + numero + " gravada no MV: solicitação " + sit + ". Bipe a próxima.");
                return;
            }
            if (res.Incerta)
            {
                _gravacaoIncerta = true;
                MostrarReserva(); Renderizar(); AtualizarBotoes();
                avisos.SetState(StatusKind.Error, "Não deu para confirmar se a baixa da " + numero + " foi gravada: " + res.Mensagem +
                    " Confira a solicitação no MV (ou abra-a de novo aqui) antes de tentar outra vez.");
                return;
            }
            avisos.SetState(StatusKind.Error, "Nada foi gravado. " + res.Mensagem +
                (erroReserva != null ? " A solicitação não pôde ser reservada de novo: " + erroReserva : " Corrija e tecle F12 de novo."));
            if (res.LinhaComProblema >= 0 && res.LinhaComProblema < grid.Rows.Count) IrPara(res.LinhaComProblema, CLote, false);
        }

        private string Origem()
        {
            if (_sol == null) return "";
            if (!string.IsNullOrEmpty(_sol.Paciente)) return _sol.Paciente;
            return !string.IsNullOrEmpty(_sol.Setor) ? _sol.Setor : _sol.DsEstoque;
        }

        // ===== TECLADO =====
        // Enter anda como no MV: Lote → Atender → Lote da linha de baixo (na linha vazia do fim,
        // o Produto). Tab anda igual e Shift+Tab faz o caminho de volta (Atender → Lote → Atender
        // da linha de cima). F9 abre a lista do campo (lotes, ou produtos na linha vazia); Del
        // exclui a linha (fora de edição); F12 fecha a baixa.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Tocar();   // qualquer tecla na aba conta como uso (inatividade da reserva)
            bool naGrade = grid != null && (grid.Focused || grid.IsCurrentCellInEditMode || (grid.EditingControl != null && grid.EditingControl.Focused));
            if (keyData == Keys.F12) { FecharBaixa(); return true; }
            if (naGrade)
            {
                if (keyData == Keys.F9) { AbrirF9(); return true; }
                if (keyData == Keys.Delete && !grid.IsCurrentCellInEditMode) { ExcluirLinha(); return true; }
                if (keyData == Keys.Enter) { Avancar(true); return true; }
                if (keyData == Keys.Tab) { Avancar(false); return true; }
                if (keyData == (Keys.Shift | Keys.Tab)) { Voltar(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // abrirNaVazia: o Enter na linha vazia do fim abre a lista de produtos; o Tab não.
        private void Avancar(bool abrirNaVazia)
        {
            var cc = grid.CurrentCell;
            if (cc == null) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            int r = cc.RowIndex;
            if (r < 0 || r >= _linhas.Count) return;
            if (_linhas[r].Produto <= 0) { if (abrirNaVazia) AbrirProdutos(r); return; }
            if (cc.ColumnIndex == CLote) { IrPara(r, CAtender, false); return; }
            if (cc.ColumnIndex == CAtender)
            {
                if (r + 1 < _linhas.Count && _linhas[r + 1].Produto > 0) IrPara(r + 1, CLote, false);
                else if (r + 1 < _linhas.Count)
                {
                    IrPara(r + 1, CProduto, false);
                }
                return;
            }
            IrPara(r, CLote, false);
        }

        // O inverso do Avancar: do Atender para o Lote da mesma linha; do Lote (ou da linha vazia
        // do fim) para o Atender da linha de cima.
        private void Voltar()
        {
            var cc = grid.CurrentCell;
            if (cc == null) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            int r = cc.RowIndex;
            if (r < 0 || r >= _linhas.Count) return;
            if (_linhas[r].Produto > 0 && cc.ColumnIndex > CLote) { IrPara(r, CLote, false); return; }
            if (r > 0) IrPara(r - 1, CAtender, false);
        }

        private void IrPara(int linha, int coluna, bool editar)
        {
            if (linha < 0 || linha >= grid.Rows.Count) return;
            try
            {
                grid.CurrentCell = grid.Rows[linha].Cells[coluna];
                grid.Focus();
                if (editar && Editavel(linha, coluna)) grid.BeginEdit(true);
            }
            catch { }
        }

        // ===== BOTÕES =====
        private bool CarregandoAgora { get { return carregando != null && carregando.Visible; } }

        private void AtualizarBotoes()
        {
            bool pode = Atendivel() && !_ocupado && !_buscando && !CarregandoAgora;
            btnExcluir.Enabled = pode; btnFechar.Enabled = pode;
            btnLimpar.Enabled = !_ocupado;
            btnAbrir.Enabled = !_ocupado;
            txtNumero.Enabled = !_ocupado;
            grid.Cursor = _ocupado || _buscando ? Cursors.AppStarting : Cursors.Default;
            // Terminou a operação: some o "aguarde" do canto.
            if (avisos != null && !_ocupado && !_buscando && !_reservando && !GravacaoEmAndamento) avisos.Livre();
            AtualizarDica();
        }

        // ===== PRÉ-VISUALIZAÇÃO (--atender-preview): dados da 484416 lidos em 28/10/2026 =====
        // Sem MV. Lotes e saldos como estavam; o lote "VENC-TESTE" é fictício, só para mostrar
        // como o F9 trata lote vencido.
        private void CarregarExemplo()
        {
            var s = new SolicitacaoAtender();
            s.Numero = 484416; s.Tipo = "S"; s.Situacao = "P"; s.Estoque = 5; s.DsEstoque = "FARMACIA CENTRAL";
            // Solicitante fictício (o repositório é público): mostra a regra dos dois nomes com partícula.
            s.Setor = "AMBULATORIO CLINICO"; s.Solicitante = "EN000000"; s.SolicitanteNome = "MARIA DE LOURDES EXEMPLO SILVA";
            s.Data = new DateTime(2026, 10, 28, 9, 36, 26);
            string[] itens = {
                "4979|AGULHA DESCARTAVEL 20X5,5 - 70014248|UNIDADE|200", "5835|COMPRESSA GAZE 7,5CMX7,5CM 13 FIOS ESTERIL 10UNID|UNIDADE|70",
                "6086|LAMINA DE BISTURI N.11|UNIDADE|30", "6089|LAMINA DE BISTURI N.15|UNIDADE|24",
                "10610|ELETRODO DESCARTAVEL PARA ELETROCIRURGIA|UNIDADE|300", "12238|APARELHO PARA TRICOTOMIA PLAST|UNIDADE|5",
                "12255|ALCOOL 70% 100ML|UNIDADE|5", "13478|ESCOVA DE CLOREXIDINA 2%|UNIDADE|4", "13510|AGUA OXIGENADA 10V - 1000ML|FRASCO|2",
                "13690|CLOREXIDINA 0,5% ALCOOLICA 100ML|FRASCO|4", "13691|CLOREXIDINA 0,2% AQUOSA 100ML|FRASCO|4",
                "13701|CREME BARREIRA 60G|TUBO|2", "13751|GEL DE ELETRODO 100GR|FRASCO|2", "13752|HIDROGEL 85G|TUBO|2",
                "13780|LUVA DE PROCEDIMENTO M|CAIXA|4", "13781|LUVA DE PROCEDIMENTO P|CAIXA|4",
                "13860|CURATIVO DE ALGINATO DE CALCIO COM PRATA|UNIDADE|3", "9005655|SORO FISIOLOGICO 0,9% - 100ML - BOL|FR C/100ML|10",
                "9026277|METRONIDAZOL 100MG/G CREME VAG BG C/50G|BG C/50G|2", "9030088|NEOMICINA + BACITRACINA 5,0MG/G+250 UI/G POM BG C/15G|BG C/15G|4" };
            foreach (var t in itens)
            {
                var p = t.Split('|');
                var it = new ItemSolicitado();
                it.Produto = int.Parse(p[0]); it.Descricao = p[1]; it.Unidade = p[2]; it.Solicitado = decimal.Parse(p[3]);
                s.Itens.Add(it);
            }
            AtenderSolicitacao.OrdenarComoImpressa(s);
            string[] lotes = {
                "4979|260113|31/01/2031|278", "5835|E21-1|25/05/2031|1413|4", "5835|E22-1|01/06/2031|2|2", "5835|E30-1|27/07/2031|142|13",
                "6086|N1184|01/03/2029|1|1", "6086|25683|14/10/2030|2|2", "6086|25120802|30/11/2030|100", "6089|25683|14/10/2030|100",
                "10610|4526S06AAAA|22/01/2029|705", "12238|250218|17/02/2030|35", "12255|P26030117|01/03/2028|12",
                "13478|50042176|31/05/2028|27|11", "13510|M43024|02/02/2028|3", "13690|M43793|06/05/2029|34",
                "13691|C10963|31/05/2028|15", "13691|M44262|18/06/2028|3", "13691|C11051|06/07/2028|1",
                "13701|VENC-TESTE|31/08/2026|6", "13701|10287288|31/03/2028|1", "13701|10431896|30/06/2028|11",
                "13751|260098|30/01/2028|1", "13751|260766|30/06/2028|2", "13751|1304|07/08/2028|5", "13751|1298|31/10/2031|5",
                "13752|1357|30/09/2028|2", "13780|ELM10167|30/11/2030|8", "13781|ILLMG011|01/04/2031|29",
                "13860|W00075854|07/05/2028|1", "13860|672H25|31/08/2030|10", "9005655|217483|16/07/2028|932",
                "9026277|2514331|30/09/2027|12", "9026277|2602407|28/02/2028|4", "9026277|2611100|30/08/2028|2",
                "9030088|FKP11829|30/09/2027|10", "9030088|2615585|21/07/2028|5",
                "13665|M43958|21/05/2028|45", "11113|85172|30/05/2030|88" };
            _lotes.Clear();
            foreach (var t in lotes)
            {
                var p = t.Split('|');
                int cod = int.Parse(p[0]);
                LotesProduto lp;
                if (!_lotes.TryGetValue(cod, out lp)) { lp = new LotesProduto(); lp.Produto = cod; _lotes[cod] = lp; }
                var lo = new LoteDisponivel();
                lo.Lote = p[1];
                lo.Validade = DateTime.ParseExact(p[2], "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                lo.Quantidade = decimal.Parse(p[3]);
                lo.EmKit = p.Length > 4 ? decimal.Parse(p[4]) : 0;
                lp.Lotes.Add(lo);
            }
            // O nº digitado vale (484610 simula "ocupada por outro usuário"); os itens são os da 484416.
            long n;
            if (long.TryParse((txtNumero.Text ?? "").Trim(), out n) && n > 0) s.Numero = n;
            _sol = s;
            _reserva = EstadoReserva.Nenhuma; _reservando = true;
            MostrarCabecalho();
            MontarLinhas();
            Carregando("Abrindo a solicitação " + s.Numero + " no MV...");
            ReservarExemplo(s);
        }

        // Pré-visualização: a reserva passa pelo ReservaAtendimento com o MV simulado (Program);
        // a leitura dos lotes é simulada com uma espera (o véu de carregando aparece como no real).
        private async void ReservarExemplo(SolicitacaoAtender s)
        {
            var t = await ReservaAtendimento.ReservarAsync(null, s.Numero);
            if (IsDisposed || _sol != s)
            {
                if (t.Resultado == ResultadoReserva.Reservada) LiberarEmSegundoPlano(null, "a tela mudou durante a reserva", s.Numero);
                return;
            }
            if (!AplicarReserva(t)) { TerminouCarga(txtNumero); return; }
            Carregando("Lendo os lotes dos " + s.Itens.Count + " produtos em " + s.DsEstoque + "...");
            await Task.Delay(1000);
            if (IsDisposed || _sol != s) return;
            TerminouCarga(null);
            PrepararProdutos(s.Estoque);
            IrPara(0, CLote, false);
            AtualizarDica();
        }

        private static List<SaldoProduto> ProdutosExemplo()
        {
            string[] ps = {
                "13510|AGUA OXIGENADA 10V - 1000ML|FRASCO|3", "13665|AGUA OXIGENADA 10V 100ML|UNIDADE|45",
                "4979|AGULHA DESCARTAVEL 20X5,5 - 70014248|UNIDADE|278", "12255|ALCOOL 70% 100ML|UNIDADE|12",
                "12238|APARELHO PARA TRICOTOMIA PLAST|UNIDADE|35", "13690|CLOREXIDINA 0,5% ALCOOLICA 100ML|FRASCO|34",
                "13691|CLOREXIDINA 0,2% AQUOSA 100ML|FRASCO|19", "5835|COMPRESSA GAZE 7,5CMX7,5CM 13 FIOS ESTERIL 10UNID|UNIDADE|1557",
                "13701|CREME BARREIRA 60G|TUBO|12", "13860|CURATIVO DE ALGINATO DE CALCIO COM PRATA|UNIDADE|11",
                "10610|ELETRODO DESCARTAVEL PARA ELETROCIRURGIA|UNIDADE|705", "13478|ESCOVA DE CLOREXIDINA 2%|UNIDADE|27",
                "13751|GEL DE ELETRODO 100GR|FRASCO|13", "13752|HIDROGEL 85G|TUBO|2", "6086|LAMINA DE BISTURI N.11|UNIDADE|103",
                "6089|LAMINA DE BISTURI N.15|UNIDADE|100", "13780|LUVA DE PROCEDIMENTO M|CAIXA|8", "13781|LUVA DE PROCEDIMENTO P|CAIXA|29",
                "9026277|METRONIDAZOL 100MG/G CREME VAG BG C/50G|BG C/50G|18", "9030088|NEOMICINA + BACITRACINA 5,0MG/G+250 UI/G POM BG C/15G|BG C/15G|15",
                "11113|SONDA DE ASPIRACAO TRAQUEAL NR 12|UNIDADE|88", "9005655|SORO FISIOLOGICO 0,9% - 100ML - BOL|FR C/100ML|932" };
            var lista = new List<SaldoProduto>();
            foreach (var t in ps)
            {
                var p = t.Split('|');
                var sp = new SaldoProduto();
                sp.Codigo = int.Parse(p[0]); sp.Nome = p[1]; sp.Unidade = p[2]; sp.Saldo = decimal.Parse(p[3]);
                lista.Add(sp);
            }
            return lista;
        }
    }
}
