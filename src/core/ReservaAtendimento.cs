using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    public enum ResultadoReserva { Reservada, OcupadaPorOutro, Recusada }

    // Liberada: o MV voltou para a tela inicial. SessaoEncerrada: a sessão do servidor já tinha
    // morrido (a trava caiu junto). Incerta: o MV respondeu, mas não voltou para a tela inicial.
    // SemConexao: não deu para falar com o MV — tentar de novo.
    public enum ResultadoLiberacao { Liberada, SessaoEncerrada, Incerta, SemConexao }

    // O que identifica a reserva no servidor: a sessão (cookies) e a tela aberta nela (task).
    public class ReservaAberta
    {
        public long Numero;
        public string Usuario = "";
        public string Host = "";
        public string FormTask = "";
        public string Mvto = "";
        public DateTime Desde;
        public DateTime Visto;            // último sinal de vida do programa com a reserva aberta
        public List<string[]> Cookies = new List<string[]>();
        public string Arquivo = "";       // arquivo de recuperação desta reserva (um por reserva)
    }

    public class TentativaReserva
    {
        public ResultadoReserva Resultado;
        public ReservaAberta Reserva;     // só quando Reservada
        public string Mvto = "";
        public string Mensagem = "";
    }

    // Quem fala com o MV (BaixaMvReal) ou finge (BaixaMvSimulada: pré-visualização e testes).
    public interface IBaixaMv
    {
        Task<TentativaReserva> ReservarAsync(Session s, long numero);
        // s null = fala com a sessão da reserva pelos cookies guardados (recuperação após queda).
        Task<ResultadoLiberacao> LiberarAsync(ReservaAberta r, Session s);
        // Lança as linhas na tela reservada e fecha a baixa (GRAVA).
        Task<ResultadoBaixa> GravarAsync(Session s, ReservaAberta r, List<LinhaBaixa> linhas, Action<string> progresso);
    }

    public class BaixaMvReal : IBaixaMv
    {
        public Task<TentativaReserva> ReservarAsync(Session s, long numero)
        {
            return MorphisClient.ReservarSolicitacaoAsync(s, numero);
        }

        public Task<ResultadoBaixa> GravarAsync(Session s, ReservaAberta r, List<LinhaBaixa> linhas, Action<string> progresso)
        {
            return MorphisClient.GravarBaixaAsync(s, r.FormTask, linhas, progresso);
        }

        public async Task<ResultadoLiberacao> LiberarAsync(ReservaAberta r, Session s)
        {
            // A sessão viva do programa só serve se for a MESMA da reserva (a reconexão sem senha
            // troca o JSESSIONID); senão, a dos cookies guardados.
            Session sessao = s != null && s.User == r.Usuario && s.Host == r.Host && MesmaSessao(s, r) ? s
                : CasClient.SessaoSalva(r.Host, r.Usuario, r.Cookies);
            try
            {
                return await MorphisClient.RetornarSolicitacaoAsync(sessao, r.FormTask) ? ResultadoLiberacao.Liberada : ResultadoLiberacao.Incerta;
            }
            catch (SessaoExpiradaException) { return ResultadoLiberacao.SessaoEncerrada; }
            catch (Exception ex)
            {
                Logger.Log("Reserva: sem conexão para liberar a " + r.Numero + " (" + ex.Message + ").");
                return ResultadoLiberacao.SemConexao;
            }
        }

        private static bool MesmaSessao(Session s, ReservaAberta r)
        {
            if (s.Cookies == null) return false;
            foreach (System.Net.Cookie ck in s.Cookies.GetCookies(new Uri(s.Host + "/soul-product-forms/services/message/message")))
                if (ck.Name == "JSESSIONID")
                    foreach (var c in r.Cookies) if (c.Length > 1 && c[0] == "JSESSIONID") return c[1] == ck.Value;
            return false;
        }
    }

    // Pré-visualização (--atender-preview) e testes: nada vai ao MV. 484610 finge estar ocupada.
    public class BaixaMvSimulada : IBaixaMv
    {
        public static readonly List<string> Chamadas = new List<string>();
        public static readonly HashSet<long> Ocupadas = new HashSet<long>(new long[] { 484610 });
        public static bool SemConexao;     // testes: a liberação falha como se a rede tivesse caído
        public static int FalharNaLinha = -1;   // testes: a gravação para nesta linha (antes do Fechar)
        public static bool FechamentoIncerto;   // testes: o Fechar fica sem resposta clara

        public async Task<ResultadoBaixa> GravarAsync(Session s, ReservaAberta r, List<LinhaBaixa> linhas, Action<string> progresso)
        {
            var res = new ResultadoBaixa();
            for (int i = 0; i < linhas.Count; i++)
            {
                if (progresso != null) progresso("Lançando " + (i + 1) + " de " + linhas.Count + ": " + linhas[i].Produto + " " + linhas[i].Descricao);
                await Task.Delay(60);
                if (i == FalharNaLinha)
                {
                    lock (Chamadas) Chamadas.Add("gravar " + r.Numero + ": parou na linha " + (i + 1));
                    res.LinhaComProblema = linhas[i].Indice;
                    res.Mensagem = "O lote " + linhas[i].Lote + " não aparece no MV para o produto " + linhas[i].Produto + " (simulado).";
                    return res;
                }
            }
            if (progresso != null) progresso("Fechando a baixa no MV...");
            await Task.Delay(150);
            res.AntesDoFechar = false;
            lock (Chamadas) Chamadas.Add("gravar " + r.Numero + ": " + linhas.Count + " linha(s)" + (FechamentoIncerto ? " — fechamento incerto" : ""));
            if (FechamentoIncerto) { res.Incerta = true; res.Mensagem = "O MV não respondeu ao fechar a baixa (simulado)."; return res; }
            res.Gravada = true;
            res.Situacao = "S";
            return res;
        }

        public async Task<TentativaReserva> ReservarAsync(Session s, long numero)
        {
            await Task.Delay(150);
            lock (Chamadas) Chamadas.Add("reservar " + numero);
            var t = new TentativaReserva();
            if (Ocupadas.Contains(numero))
            {
                t.Resultado = ResultadoReserva.OcupadaPorOutro;
                t.Mensagem = "Atenção.: Esta solicitação já está sendo confirmada neste momento por outro usuário.";
                return t;
            }
            t.Resultado = ResultadoReserva.Reservada;
            t.Mvto = "999999";
            t.Reserva = new ReservaAberta();
            t.Reserva.Numero = numero; t.Reserva.Usuario = "PREVIEW"; t.Reserva.Host = "preview";
            t.Reserva.FormTask = Guid.NewGuid().ToString(); t.Reserva.Mvto = t.Mvto;
            t.Reserva.Desde = DateTime.Now; t.Reserva.Visto = DateTime.Now;
            return t;
        }

        public async Task<ResultadoLiberacao> LiberarAsync(ReservaAberta r, Session s)
        {
            await Task.Delay(100);
            lock (Chamadas) Chamadas.Add("liberar " + r.Numero + (s == null ? " (pelos cookies)" : ""));
            return SemConexao ? ResultadoLiberacao.SemConexao : ResultadoLiberacao.Liberada;
        }
    }

    // A reserva da solicitação aberta na aba Atender ("Confirmar produtos" no MV) e tudo o que
    // garante que ela não fica presa numa sessão fantasma:
    //   - liberação em toda saída normal (borracha, outra solicitação, sair da aba, Sair, fechar
    //     o programa, Windows desligando) — quem chama é a tela ou a janela;
    //   - liberação por inatividade (a aba conta o tempo; o "manter conectado" renovaria a sessão
    //     para sempre);
    //   - arquivo de recuperação (dados\reservas\<nº>.dat, protegido pelo Windows/DPAPI, só deste
    //     usuário): guarda os cookies da sessão e o task da tela. Se o programa morrer (erro,
    //     processo encerrado, queda de energia), na próxima abertura ele volta à MESMA sessão e
    //     faz o "Retornar". Se a sessão já morreu, o servidor já soltou a trava (~60 min);
    //   - liberação que falhou por falta de rede fica pendente e é tentada de novo a cada minuto.
    // Nunca há duas reservas: reservar outra libera a anterior antes.
    public static class ReservaAtendimento
    {
        public static IBaixaMv Mv = new BaixaMvReal();
        public static string Pasta = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dados", "reservas");

        // Inatividade (segundos; campos para os testes encurtarem): aviso e liberação.
        public static int SegundosAviso = 8 * 60;
        public static int SegundosInatividade = 10 * 60;
        // Depois disso sem sinal do programa, a sessão do servidor já morreu (cai com ~60 min).
        public const int MinutosVidaSessao = 75;
        public static int SegundosNovaTentativa = 60;

        private static readonly byte[] Entropia = Encoding.UTF8.GetBytes("mv-auxiliar/reserva-atender/1");
        private static readonly object _lock = new object();
        private static ReservaAberta _atual;
        private static readonly List<ReservaAberta> _pendentes = new List<ReservaAberta>();
        private static System.Threading.Timer _timer;
        private static DateTime _ultimaGravacao;

        // Recado para a aba mostrar (ex.: a reserva que ficou da queda foi liberada).
        public static string Recado;

        public static ReservaAberta Atual { get { lock (_lock) return _atual; } }
        public static bool TemPendente { get { lock (_lock) return _pendentes.Count > 0; } }

        // ===== reservar / liberar =====
        public static async Task<TentativaReserva> ReservarAsync(Session s, long numero)
        {
            var anterior = Atual;
            if (anterior != null) await LiberarAsync(s, anterior.Numero == numero ? "reabriu a solicitação" : "abriu outra solicitação");
            var t = await Mv.ReservarAsync(s, numero);
            if (t.Resultado == ResultadoReserva.OcupadaPorOutro)
            {
                // O "outro usuário" pode ser a sessão antiga DESTE programa (caiu e a reserva ainda
                // não foi solta): solta a antiga e tenta uma vez de novo.
                var antiga = PendenteDe(numero);
                if (antiga != null)
                {
                    var res = await LiberarReservaAsync(antiga, null, "a reserva antiga deste programa prendia a solicitação");
                    if (res == ResultadoLiberacao.Liberada || res == ResultadoLiberacao.SessaoEncerrada) t = await Mv.ReservarAsync(s, numero);
                }
            }
            if (t.Resultado == ResultadoReserva.Reservada && t.Reserva != null)
            {
                lock (_lock) _atual = t.Reserva;
                Gravar(t.Reserva);
            }
            return t;
        }

        public static async Task<ResultadoLiberacao> LiberarAsync(Session s, string motivo)
        {
            ReservaAberta r;
            lock (_lock) { r = _atual; _atual = null; }
            if (r == null) return ResultadoLiberacao.Liberada;
            return await LiberarReservaAsync(r, s, motivo);
        }

        // Solta só se a reserva atual for desta solicitação (o pedido pode chegar atrasado, depois
        // de outra solicitação já ter sido reservada).
        public static async Task<ResultadoLiberacao> LiberarAsync(Session s, string motivo, long numero)
        {
            ReservaAberta r;
            lock (_lock)
            {
                r = _atual;
                if (r == null || r.Numero != numero) return ResultadoLiberacao.Liberada;
                _atual = null;
            }
            return await LiberarReservaAsync(r, s, motivo);
        }

        private static async Task<ResultadoLiberacao> LiberarReservaAsync(ReservaAberta r, Session s, string motivo)
        {
            ResultadoLiberacao res;
            try { res = await Mv.LiberarAsync(r, s); }
            catch (Exception ex) { Logger.Log("Reserva: erro ao liberar a " + r.Numero + ": " + ex.Message); res = ResultadoLiberacao.SemConexao; }
            Logger.Log("Reserva: liberar " + r.Numero + " (" + motivo + ") -> " + res);
            if (res == ResultadoLiberacao.SemConexao && (DateTime.Now - r.Visto).TotalMinutes < MinutosVidaSessao)
            {
                lock (_lock) { if (!_pendentes.Contains(r)) _pendentes.Add(r); }
                LigarNovasTentativas();
            }
            else
            {
                if (res == ResultadoLiberacao.SemConexao)
                    Logger.Log("Reserva: " + r.Numero + " sem sinal há mais de " + MinutosVidaSessao + " min — a sessão do servidor já caiu e levou a trava.");
                lock (_lock) _pendentes.Remove(r);
                Apagar(r);
            }
            return res;
        }

        // Reserva antiga deste programa (pendente ou deixada no disco por uma queda) para a solicitação.
        private static ReservaAberta PendenteDe(long numero)
        {
            lock (_lock) { foreach (var p in _pendentes) if (p.Numero == numero) { _pendentes.Remove(p); return p; } }
            var atual = Atual;
            foreach (var r in LerTodas())
                if (r.Numero == numero && (atual == null || atual.Arquivo != r.Arquivo)) return r;
            return null;
        }

        // Fechar a Baixa na tela reservada. Gravou: a tela do MV voltou para o início e não há trava
        // a soltar — a reserva acaba aqui (sem Retornar).
        public static async Task<ResultadoBaixa> GravarAsync(Session s, long numero, List<LinhaBaixa> linhas, Action<string> progresso)
        {
            var r = Atual;
            if (r == null || r.Numero != numero)
            {
                var f = new ResultadoBaixa();
                f.Mensagem = "A solicitação " + numero + " não está reservada no MV.";
                return f;
            }
            var res = await Mv.GravarAsync(s, r, linhas, progresso);
            if (res.Gravada) Concluir(numero, "baixa gravada");
            return res;
        }

        // A baixa foi gravada (confirmada pelo MV ou pela consulta depois de uma resposta incerta).
        public static void Concluir(long numero, string motivo)
        {
            ReservaAberta r;
            lock (_lock)
            {
                r = _atual;
                if (r == null || r.Numero != numero) return;
                _atual = null;
            }
            Apagar(r);
            Logger.Log("Reserva: " + numero + " concluída (" + motivo + ").");
        }

        // Saída sem volta (Windows desligando, erro fatal, processo encerrando): espera no máximo
        // 'ms'. Roda fora da thread da tela (Task.Run) para não travar esperando a si mesma. Não
        // passa pelo MvGate — o programa está acabando. Se não der, o arquivo de recuperação fica.
        public static bool LiberarAgora(Session s, int ms)
        {
            if (Atual == null) return true;
            try
            {
                var t = Task.Run(delegate { return LiberarAsync(s, "programa encerrando"); });
                return t.Wait(ms) && t.Result != ResultadoLiberacao.SemConexao;
            }
            catch (Exception ex) { Logger.Log("Reserva: liberação de emergência falhou: " + ex.Message); return false; }
        }

        // A sessão do programa morreu (expirou, troca de login): a reserva desta sessão vai para
        // as pendentes — a nova tentativa, pelos cookies, confirma que o servidor a soltou.
        public static void SessaoPerdida()
        {
            lock (_lock)
            {
                if (_atual == null) return;
                if (!_pendentes.Contains(_atual)) _pendentes.Add(_atual);
                _atual = null;
            }
            LigarNovasTentativas();
        }

        // Sinal de vida (o atendente mexeu na aba): regrava o "visto" no arquivo, no máximo 1x/min.
        public static void Tocar()
        {
            var r = Atual;
            if (r == null) return;
            r.Visto = DateTime.Now;
            if ((DateTime.Now - _ultimaGravacao).TotalSeconds >= 60) Gravar(r);
        }

        // ===== recuperação depois de queda =====
        // Abertura do programa: reservas que ficaram no disco. Não precisa de login: fala com a
        // sessão antiga pelos cookies. Liberou ou a sessão já tinha morrido -> apaga o arquivo.
        public static async Task RecuperarAsync()
        {
            foreach (var r in LerTodas())
            {
                lock (_lock) { if (_atual != null && _atual.Arquivo == r.Arquivo) continue; }   // a reserva viva desta execução
                var res = await LiberarReservaAsync(r, null, "recuperação após o programa fechar de repente");
                if (res == ResultadoLiberacao.Liberada)
                    Recado = "A solicitação " + r.Numero + ", que ficou reservada quando o programa fechou de repente, foi liberada no MV.";
            }
        }

        // A nova tentativa pode cair na MESMA sessão que o resto do programa está usando: roda na
        // thread da tela e entra pelo MvGate (duas conversas ao mesmo tempo corrompem a consulta
        // da outra). Sem tela (recuperação antes do login), roda direto.
        private static System.Threading.SynchronizationContext _ui;
        private static bool _tentando;

        // Chamado na thread da tela ao abrir o programa (a MainWindow).
        public static void Iniciar() { _ui = System.Threading.SynchronizationContext.Current; }

        private static void LigarNovasTentativas()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                _timer = new System.Threading.Timer(delegate
                {
                    var ui = _ui;
                    if (ui != null) ui.Post(delegate { var t = NovaTentativaAsync(true); }, null);
                    else { var t = NovaTentativaAsync(false); }
                }, null, SegundosNovaTentativa * 1000, SegundosNovaTentativa * 1000);
            }
        }

        private static async Task NovaTentativaAsync(bool naUi)
        {
            if (_tentando) return;
            _tentando = true;
            bool tenho = false;
            try
            {
                if (naUi && !(tenho = await MvGate.EntrarAsync("reserva-liberar", 30000))) return;   // a próxima volta tenta
                List<ReservaAberta> fila;
                lock (_lock) fila = new List<ReservaAberta>(_pendentes);
                foreach (var r in fila)
                {
                    lock (_lock) _pendentes.Remove(r);
                    await LiberarReservaAsync(r, null, "nova tentativa");
                }
            }
            catch (Exception ex) { Logger.Log("Reserva: nova tentativa falhou: " + ex.Message); }
            finally
            {
                if (tenho) MvGate.Sair();
                _tentando = false;
                lock (_lock)
                {
                    if (_pendentes.Count == 0 && _timer != null) { _timer.Dispose(); _timer = null; }
                }
            }
        }

        // ===== arquivo de recuperação (DPAPI, usuário atual do Windows) =====
        // Um arquivo por reserva (nº + instante): reservar de novo a mesma solicitação nunca
        // sobrescreve nem apaga o arquivo de uma reserva antiga que ainda precisa ser solta.
        private static void Gravar(ReservaAberta r)
        {
            if (string.IsNullOrEmpty(r.Arquivo)) r.Arquivo = Path.Combine(Pasta, r.Numero + "-" + DateTime.Now.Ticks + ".dat");
            try
            {
                var sb = new StringBuilder();
                Linha(sb, "numero", r.Numero.ToString());
                Linha(sb, "usuario", r.Usuario); Linha(sb, "host", r.Host);
                Linha(sb, "task", r.FormTask); Linha(sb, "mvto", r.Mvto);
                Linha(sb, "desde", r.Desde.Ticks.ToString()); Linha(sb, "visto", r.Visto.Ticks.ToString());
                foreach (var c in r.Cookies) Linha(sb, "cookie", string.Join("\t", c));
                byte[] dados = ProtectedData.Protect(Encoding.UTF8.GetBytes(sb.ToString()), Entropia, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(r.Arquivo));
                string arq = r.Arquivo, tmp = arq + ".tmp";
                File.WriteAllBytes(tmp, dados);
                if (File.Exists(arq)) File.Delete(arq);
                File.Move(tmp, arq);
                _ultimaGravacao = DateTime.Now;
            }
            catch (Exception ex) { Logger.Log("Reserva: não gravei o arquivo de recuperação da " + r.Numero + ": " + ex.Message); }
        }

        private static void Linha(StringBuilder sb, string chave, string valor)
        {
            sb.Append(chave).Append('=').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(valor ?? ""))).Append('\n');
        }

        private static void Apagar(ReservaAberta r)
        {
            try { if (!string.IsNullOrEmpty(r.Arquivo) && File.Exists(r.Arquivo)) File.Delete(r.Arquivo); }
            catch (Exception ex) { Logger.Log("Reserva: não apaguei o arquivo da " + r.Numero + ": " + ex.Message); }
        }

        // Arquivo ilegível (outro usuário do Windows, corrompido) ou com mais de um dia: a sessão
        // dele já morreu há muito — apaga.
        private static List<ReservaAberta> LerTodas()
        {
            var res = new List<ReservaAberta>();
            try
            {
                if (!Directory.Exists(Pasta)) return res;
                foreach (var arq in Directory.GetFiles(Pasta, "*.dat"))
                {
                    try
                    {
                        if ((DateTime.Now - File.GetLastWriteTime(arq)).TotalHours > 24) { File.Delete(arq); continue; }
                        byte[] dados = ProtectedData.Unprotect(File.ReadAllBytes(arq), Entropia, DataProtectionScope.CurrentUser);
                        var r = new ReservaAberta();
                        r.Arquivo = arq;
                        foreach (var l in Encoding.UTF8.GetString(dados).Split('\n'))
                        {
                            int i = l.IndexOf('=');
                            if (i <= 0) continue;
                            string k = l.Substring(0, i), v = Encoding.UTF8.GetString(Convert.FromBase64String(l.Substring(i + 1)));
                            switch (k)
                            {
                                case "numero": long.TryParse(v, out r.Numero); break;
                                case "usuario": r.Usuario = v; break;
                                case "host": r.Host = v; break;
                                case "task": r.FormTask = v; break;
                                case "mvto": r.Mvto = v; break;
                                case "desde": long d; if (long.TryParse(v, out d)) r.Desde = new DateTime(d); break;
                                case "visto": long w; if (long.TryParse(v, out w)) r.Visto = new DateTime(w); break;
                                case "cookie": r.Cookies.Add(v.Split('\t')); break;
                            }
                        }
                        if (r.Numero > 0) res.Add(r); else File.Delete(arq);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("Reserva: arquivo de recuperação ilegível (" + Path.GetFileName(arq) + "): " + ex.Message);
                        try { if ((DateTime.Now - File.GetLastWriteTime(arq)).TotalHours > 2) File.Delete(arq); } catch { }
                    }
                }
            }
            catch (Exception ex) { Logger.Log("Reserva: não li a pasta de recuperação: " + ex.Message); }
            return res;
        }
    }
}
