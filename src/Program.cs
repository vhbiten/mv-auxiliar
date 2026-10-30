using System;
using System.Windows.Forms;
using SoulmvKit.UI;

namespace SoulmvKit
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // O app é DPI-aware (manifest): mede o DPI real UMA vez e alimenta o
            // Theme.S(), que escala as medidas de layout em pixel fixo.
            using (var probe = new Control())
            using (var g = probe.CreateGraphics())
                SoulmvKit.UI.Theme.Escala = g.DpiX / 96f;
            SoulmvKit.Core.Logger.Init(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"));
            SoulmvKit.Core.ProdutoNomes.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.Navegador.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.PdfEfemero.LimparResiduos();
            SoulmvKit.Core.SolicitacoesCache.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.ContagemCaf.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.ContagemFarmacia.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.PedidoEmbalagens.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.ProdutosSoro.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.ProdutosHemodialise.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.Posologia.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.PonteEstoque.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.DispensaJustificativa.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.EstoqueSetores.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.EstoqueCache.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.EstoqueExcluidos.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.EstoqueRegua.Init(AppDomain.CurrentDomain.BaseDirectory);

            // --caixas-preview: abre SÓ o modal "Cadastro de embalagens" (dados locais de
            // dados\pedido-embalagens.txt, sem login) — é como o laço de screenshot enxerga
            // um diálogo que, no app, só abre por clique.
            if (args != null && Array.IndexOf(args, "--caixas-preview") >= 0)
            {
                var dlg = new SoulmvKit.UI.CaixasDialog(null);
                // Sem taskbar o Process.MainWindowHandle fica 0 e o capturador não acha a janela.
                dlg.ShowInTaskbar = true;
                Application.Run(dlg);
                return;
            }

            // Flags de desenvolvimento para visualizar o shell sem fazer login real.
            bool shellPrev = args != null && Array.IndexOf(args, "--shell-preview") >= 0;
            bool confPrev = args != null && Array.IndexOf(args, "--conf-preview") >= 0;
            bool consPrev = args != null && Array.IndexOf(args, "--consulta-preview") >= 0;
            bool solicPrev = args != null && Array.IndexOf(args, "--solic-preview") >= 0;
            bool pedidoPrev = args != null && Array.IndexOf(args, "--pedido-preview") >= 0;
            bool atbPrev = args != null && Array.IndexOf(args, "--atb-preview") >= 0;
            bool justPrev = args != null && Array.IndexOf(args, "--just-preview") >= 0;
            bool estoquePrev = args != null && Array.IndexOf(args, "--estoque-preview") >= 0;
            bool cfgPrev = args != null && Array.IndexOf(args, "--config-preview") >= 0;
            bool pamPrev = args != null && Array.IndexOf(args, "--pam-preview") >= 0;
            bool atenderPrev = args != null && Array.IndexOf(args, "--atender-preview") >= 0;
            bool algumPreview = shellPrev || confPrev || consPrev || solicPrev || pedidoPrev || atbPrev || justPrev || estoquePrev || cfgPrev || pamPrev || atenderPrev;

            // A janela só lembra o estado (maximizada/tamanho) na abertura NORMAL, sem
            // argumento nenhum: preview e captura de tela precisam de tamanho controlado.
            var win = new MainWindow(args == null || args.Length == 0);

            // Solicitação reservada na aba Atender: erro fatal ou processo encerrando ainda tentam
            // soltá-la no MV; se não der, o arquivo de recuperação solta na próxima abertura.
            AppDomain.CurrentDomain.UnhandledException += delegate { SoulmvKit.Core.ReservaAtendimento.LiberarAgora(win.Session, 4000); };
            AppDomain.CurrentDomain.ProcessExit += delegate { SoulmvKit.Core.ReservaAtendimento.LiberarAgora(win.Session, 1500); };

            if (algumPreview)
            {
                // Pré-visualização nunca reserva nada no MV: o atendimento usa o MV simulado.
                SoulmvKit.Core.ReservaAtendimento.Mv = new SoulmvKit.Core.BaixaMvSimulada();
                SoulmvKit.Core.ReservaAtendimento.Pasta = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dados", "reservas-preview");
                SoulmvKit.UI.ConferenciaView.PreviewCaf = args != null && Array.IndexOf(args, "--caf") >= 0;
                win.Usuario = "PREVIEW";
                var shell = new SoulmvKit.UI.ShellView(win);
                win.ShowView(shell);
                if (cfgPrev) shell.AbrirConfiguracoes();
                else if (confPrev) shell.IrParaConferencia();
                else if (consPrev) shell.IrParaConsulta();
                else if (solicPrev) shell.IrParaSolicitacoes();
                else if (pamPrev) shell.IrParaEntregasPam();
                else if (atenderPrev) shell.IrParaAtender();
                else if (pedidoPrev) shell.IrParaPedidoCaf();
                else if (atbPrev) shell.IrParaAntimicrobianos();
                else if (justPrev) shell.IrParaJustificativas();
                else if (estoquePrev) shell.IrParaEstoque();
            }
            else
            {
                // Atualização automática (releases públicas do GitHub): roda em paralelo com a
                // tela de login, CONTANDO cada estágio no rodapé do login ("Verificando...",
                // "Baixando..."). Achou versão nova, trocou o exe e ninguém logou? Avisa que
                // vai reabrir, espera o aviso ser lido, grava o estado da janela (para voltar
                // maximizada se estava maximizada) e reinicia. Se alguém entrou no meio, nada
                // de derrubar quem trabalha: a versão nova fica valendo no próximo início.
                SoulmvKit.Core.AtualizadorApp.LimparResiduo();
                // Reforço de primeiro plano: no reinício pós-atualização a janela nasce
                // com a permissão transferida pelo processo antigo (AllowSetForegroundWindow
                // + WaitForInputIdle no Reiniciar) — o Activate garante o uso. Na abertura
                // comum é um no-op: a janela já nasce ativa.
                win.Shown += delegate { try { win.Activate(); } catch { } };
                win.Shown += async delegate
                {
                    string nova = await SoulmvKit.Core.AtualizadorApp.AtualizarAsync(win.MostrarStatusAtualizacao);
                    if (nova == null) return;
                    if (!string.IsNullOrEmpty(win.Usuario)) { win.MostrarStatusAtualizacao(""); return; }
                    win.MostrarStatusAtualizacao("Atualizado para a versão " + nova + ". O programa vai reabrir sozinho...");
                    await System.Threading.Tasks.Task.Delay(2500);
                    // Alguém pode ter logado (ou fechado o app) enquanto o aviso estava na
                    // tela: aí não se reabre nada — a versão nova vale no próximo início.
                    if (win.IsDisposed || win.Disposing) return;
                    if (!string.IsNullOrEmpty(win.Usuario)) { win.MostrarStatusAtualizacao(""); return; }
                    win.SalvarJanela();
                    SoulmvKit.Core.AtualizadorApp.Reiniciar();
                };

                // Licença: valida na abertura (em paralelo, sem atrasar o login) e revalida a
                // cada 6h com o app aberto. Reprovou, derruba para a tela de bloqueio — menos
                // no meio de uma gravação no MV, aí tenta de novo em 10 minutos.
                win.Shown += async delegate
                {
                    var estado = await SoulmvKit.Core.Licenca.ValidarAsync();
                    if (estado != SoulmvKit.Core.Licenca.Estado.Ok)
                        win.ShowView(new SoulmvKit.UI.BloqueioView(win, estado));
                };
                var relogioLicenca = new Timer();
                relogioLicenca.Interval = 6 * 60 * 60 * 1000;
                relogioLicenca.Tick += async delegate
                {
                    if (SoulmvKit.UI.ProduzirView.ProducaoEmAndamento || SoulmvKit.UI.PedidoCafView.EnvioEmAndamento)
                    {
                        relogioLicenca.Interval = 10 * 60 * 1000;
                        return;
                    }
                    relogioLicenca.Interval = 6 * 60 * 60 * 1000;
                    var estado = await SoulmvKit.Core.Licenca.ValidarAsync();
                    if (estado != SoulmvKit.Core.Licenca.Estado.Ok)
                        win.ShowView(new SoulmvKit.UI.BloqueioView(win, estado));
                };
                relogioLicenca.Start();
            }
            Application.Run(win);
        }
    }
}
