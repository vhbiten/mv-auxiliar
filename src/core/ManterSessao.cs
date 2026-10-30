using System;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Mantém a sessão do MV viva enquanto o app está logado.
    //
    // Medido no log (ago–out/2026) e confirmado ao vivo em 18/10/2026: a sessão cai com cerca
    // de 60 min SEM uso — e cai em silêncio: o MV responde "OK" com a tela suspensa e o
    // alerta "Não existe Empresa cadastrada...". No teste, a sessão que recebeu este
    // keepalive a cada 10 min abriu a tela normalmente depois de 80 min; a parada, não.
    //
    // Duas camadas:
    //   1. a cada 10 min, um WORKSPACE_INIT — a mesma mensagem que abre toda operação do app;
    //      não abre tela, não consulta nem grava nada — renova o relógio de inatividade;
    //   2. se mesmo assim cair, MorphisClient.CallFormAsync RECONHECE a queda (antes falhava
    //      em silêncio), tenta reconectar sem senha e, se o login único do CAS também venceu
    //      — o que acontece no mesmo prazo de ~60–70 min parado (teste de 18/10/2026) —, leva
    //      o usuário ao login com "Sua sessão expirou". Ou seja: sem o keepalive, a queda por
    //      inatividade não tem recuperação sem senha; com ele, ela não acontece.
    // O PEP (/mvpep) tem sessão própria: o keepalive também o toca, mas só se já foi aberto.
    // Ligado por padrão; Configurações > Sessão desliga (preferência "manter_conectado").
    public static class ManterSessao
    {
        public const int IntervaloMin = 10;
        private const string Pref = "manter_conectado";

        private static System.Windows.Forms.Timer _timer;
        private static Func<Session> _sessaoAtual;
        private static bool _emCurso;

        public static bool Habilitado
        {
            get { return Preferencias.Bool(Pref, true); }
        }

        public static bool Rodando { get { return _timer != null; } }

        // Liga o keepalive (se habilitado). Chamado ao entrar no shell, na thread da UI.
        public static void Iniciar(Func<Session> sessaoAtual)
        {
            _sessaoAtual = sessaoAtual;
            if (!Habilitado || _timer != null) return;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = IntervaloMin * 60 * 1000;
            _timer.Tick += new EventHandler(delegate { var t = TickAsync(); });
            _timer.Start();
            Logger.Log("Sessão: keepalive ligado (a cada " + IntervaloMin + " min).");
        }

        // Desliga (Sair, sessão expirada, fechamento do app).
        public static void Parar()
        {
            PararTimer();
            _sessaoAtual = null;
        }

        // Configurações: grava a escolha e liga/desliga já, sem precisar sair.
        public static void DefinirHabilitado(bool ligado)
        {
            Preferencias.Definir(Pref, ligado);
            if (!ligado) PararTimer();
            else if (_sessaoAtual != null) Iniciar(_sessaoAtual);
            Logger.Log("Sessão: manter conectado " + (ligado ? "LIGADO" : "DESLIGADO") + " pelo usuário.");
        }

        private static void PararTimer()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Dispose();
            _timer = null;
            Logger.Log("Sessão: keepalive desligado.");
        }

        private static async Task TickAsync()
        {
            if (_emCurso || _sessaoAtual == null) return;
            Session s = _sessaoAtual();
            if (s == null) return;
            // Alguém falando com o MV (consulta, gravação de uma baixa) já mantém a sessão viva; e
            // duas conversas ao mesmo tempo na mesma sessão atrapalham a outra. Pula esta rodada.
            if (!MvGate.Entrar("keepalive")) return;
            _emCurso = true;
            try
            {
                await ManterAsync(s);
            }
            finally { _emCurso = false; MvGate.Sair(); }
        }

        private static async Task ManterAsync(Session s)
        {
            // forms
            bool formsOk;
            try
            {
                var r = await new MorphisClient(s).WorkspaceInitAsync();
                formsOk = r.IsOk;
            }
            catch (SessaoExpiradaException) { formsOk = false; }
            catch (Exception ex)
            {
                // servidor instável ou rede fora: não mexe na sessão, tenta no próximo ciclo
                Logger.Log("Sessão: keepalive do forms sem resposta (" + ex.Message + ").");
                return;
            }
            if (formsOk) s.UltimoKeepalive = DateTime.Now;
            else await CasClient.RenovarSessaoAsync(s);

            // PEP: EntrarAsync valida a sessão (toca o relógio do PEP) e, se caiu, reabre por SSO
            if (PepClient.Aberto(s))
            {
                try { await PepClient.EntrarAsync(s); }
                catch (Exception ex) { Logger.Log("Sessão: keepalive do PEP falhou (" + ex.Message + ")."); }
            }
        }
    }
}
