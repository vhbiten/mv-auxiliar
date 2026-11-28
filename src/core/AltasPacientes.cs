using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // O que o PEP diz da alta de um atendimento (hora local; null = não aconteceu).
    public class AltaPep
    {
        public DateTime? AltaMedica;   // alta dada pelo médico (o paciente pode seguir no leito)
        public DateTime? Saida;        // saída do paciente
        public DateTime LidoEm;
    }

    // A alta do paciente de UMA solicitação, pronta para a tela.
    public class AltaDaSolicitacao
    {
        public DateTime? AltaMedica;
        public DateTime? Saida;
        public bool SaidaSemHora;      // o MV só informou o dia
        public bool Saiu { get { return Saida.HasValue; } }

        public string Resumo
        {
            get
            {
                string saida = Saida.HasValue ? Saida.Value.ToString(SaidaSemHora ? "dd/MM" : "dd/MM HH:mm") : null;
                if (AltaMedica.HasValue)
                    return "Alta médica " + AltaMedica.Value.ToString("dd/MM HH:mm")
                        + (saida != null ? " · saída " + saida : " · sem saída registrada");
                return saida != null ? "Saída " + saida : "";
            }
        }
    }

    // Alta dos pacientes das solicitações. O MV emite e imprime os turnos mesmo de quem já teve
    // alta (27/11/2026: paciente saiu às 14:49 e o turno da manhã seguinte foi impresso às 20h),
    // e a farmácia só descobria abrindo o prontuário de cada um.
    //
    // Duas fontes, as duas só de leitura:
    //  - SAÍDA do paciente: vem na própria lista do MV (Solicitacao.Alta), sem custo;
    //  - ALTA MÉDICA com o paciente ainda no leito: só o PEP sabe. É consultado um atendimento
    //    por vez (~80 ms cada, fora do portão do MV), só para quem tem solicitação NÃO atendida
    //    e ainda sem saída, e de novo a cada MinutosEntreLeituras.
    //
    // Quem recebe o selo (De): a não atendida de paciente com alta médica ou saída, e a
    // atendida cujo horário é POSTERIOR à saída (foi separada para quem já não está). A
    // atendida comum fica sem selo de propósito: todo paciente do pronto atendimento vai
    // embora no mesmo dia, e marcar tudo isso esconderia as que pedem alguma ação.
    public static class AltasPacientes
    {
        private const int MinutosEntreLeituras = 5;
        private const int MinutosSemPep = 10;       // PEP fora ou login sem PEP: não insistir a cada 30 s
        private const int HorasNaMemoria = 36;
        private const int Paralelo = 6;

        private static readonly Dictionary<string, AltaPep> _porAtendimento = new Dictionary<string, AltaPep>();
        private static bool _lendo;
        private static DateTime _pepForaAte = DateTime.MinValue;
        private static int _geracao;                // muda no Limpar: descarta leitura em voo de outra sessão

        // O PEP trouxe alta nova (ou desfeita): quem mostra o selo se redesenha.
        public static event Action Mudou;

        public static AltaDaSolicitacao De(Solicitacao s)
        {
            if (s == null) return null;
            string at = (s.Atendimento ?? "").Trim();
            if (at.Length == 0) return null;

            bool semHora;
            DateTime? saida = SaidaNoMv(s.Alta, out semHora);
            DateTime? medica = null;
            AltaPep p;
            lock (_porAtendimento) _porAtendimento.TryGetValue(at, out p);
            if (p != null)
            {
                medica = p.AltaMedica;
                if (!saida.HasValue) { saida = p.Saida; semHora = false; }
            }

            if (s.Situacao == "S")
            {
                DateTime pedido;
                if (!saida.HasValue || !QuandoDoPedido(s, out pedido)) return null;
                // Só o dia da saída: vale o fim dele (marca apenas os pedidos dos dias seguintes).
                DateTime limite = semHora ? saida.Value.Date.AddDays(1) : saida.Value;
                if (pedido < limite) return null;
            }
            else if (!saida.HasValue && !medica.HasValue) return null;

            var a = new AltaDaSolicitacao();
            a.AltaMedica = medica; a.Saida = saida; a.SaidaSemHora = saida.HasValue && semHora;
            return a;
        }

        // Consulta no PEP os atendimentos da lista que ainda podem ganhar alta médica. Não
        // espera nem segura o portão do MV: quem chama segue a vida, e Mudou avisa se veio algo.
        public static async void Conferir(Session s, IList<Solicitacao> itens)
        {
            if (s == null || itens == null || _lendo) return;
            DateTime agora = DateTime.Now;
            if (agora < _pepForaAte) return;

            var alvos = new List<string>();
            var vistos = new HashSet<string>();
            lock (_porAtendimento)
            {
                foreach (var it in itens)
                {
                    if (it == null || it.Situacao == "S" || !string.IsNullOrEmpty(it.Alta)) continue;
                    string at = (it.Atendimento ?? "").Trim();
                    if (at.Length == 0 || !SoDigitos(at) || !vistos.Add(at)) continue;
                    AltaPep a;
                    if (_porAtendimento.TryGetValue(at, out a)
                        && (a.Saida.HasValue || (agora - a.LidoEm).TotalMinutes < MinutosEntreLeituras)) continue;
                    alvos.Add(at);
                }
            }
            if (alvos.Count == 0) return;

            _lendo = true;
            int geracao = _geracao;
            bool mudou = false;
            try
            {
                if (!await PepClient.EntrarAsync(s))
                {
                    _pepForaAte = DateTime.Now.AddMinutes(MinutosSemPep);
                    return;
                }
                var lidas = new Dictionary<string, AltaPep>();
                var falharam = await LerAsync(s, alvos, lidas);
                // Sessão do PEP vencida no meio: reabre UMA vez e repete só quem falhou.
                if (falharam.Count > 0 && await PepClient.EntrarAsync(s)) falharam = await LerAsync(s, falharam, lidas);
                if (geracao != _geracao) return;   // logout no meio: nada disto vale mais
                if (falharam.Count == alvos.Count) _pepForaAte = DateTime.Now.AddMinutes(MinutosSemPep);

                int comAlta = 0;
                lock (_porAtendimento)
                {
                    foreach (var kv in lidas)
                    {
                        AltaPep antes;
                        _porAtendimento.TryGetValue(kv.Key, out antes);
                        DateTime? mAntes = antes != null ? antes.AltaMedica : null;
                        DateTime? sAntes = antes != null ? antes.Saida : null;
                        if (mAntes != kv.Value.AltaMedica || sAntes != kv.Value.Saida) mudou = true;
                        if (kv.Value.AltaMedica.HasValue || kv.Value.Saida.HasValue) comAlta++;
                        _porAtendimento[kv.Key] = kv.Value;
                    }
                    Enxugar();
                }
                if (mudou || falharam.Count > 0)
                    Logger.Log("Altas (PEP): " + lidas.Count + " atendimento(s) consultado(s), " + comAlta + " com alta"
                        + (falharam.Count > 0 ? "; " + falharam.Count + " sem resposta" : "") + ".");
            }
            catch (Exception ex) { Logger.Log("Altas (PEP): " + ex.Message); }
            finally { _lendo = false; }

            if (!mudou) return;
            var aviso = Mudou;
            if (aviso != null)
            {
                try { aviso(); }
                catch (Exception ex) { Logger.Log("Altas (PEP): erro ao avisar a tela: " + ex.Message); }
            }
        }

        // Lê em lotes paralelos; devolve os atendimentos em que o PEP não respondeu.
        private static async Task<List<string>> LerAsync(Session s, List<string> alvos, Dictionary<string, AltaPep> lidas)
        {
            var falharam = new List<string>();
            for (int i = 0; i < alvos.Count; i += Paralelo)
            {
                var lote = new List<Task<AltaPep>>();
                var donos = new List<string>();
                for (int k = i; k < alvos.Count && k < i + Paralelo; k++)
                {
                    donos.Add(alvos[k]);
                    lote.Add(PepClient.AltaDoAtendimentoAsync(s, alvos[k]));
                }
                var res = await Task.WhenAll(lote);
                for (int k = 0; k < donos.Count; k++)
                {
                    if (res[k] == null) falharam.Add(donos[k]);
                    else lidas[donos[k]] = res[k];
                }
            }
            return falharam;
        }

        // Fim de sessão: a próxima pessoa não herda o que foi lido com o login anterior.
        public static void Limpar()
        {
            _geracao++;
            _pepForaAte = DateTime.MinValue;
            lock (_porAtendimento) _porAtendimento.Clear();
        }

        // Pré-visualização e testes: grava o que o PEP diria de um atendimento.
        public static void Registrar(string atendimento, DateTime? altaMedica, DateTime? saida)
        {
            var a = new AltaPep();
            a.AltaMedica = altaMedica; a.Saida = saida; a.LidoEm = DateTime.Now;
            lock (_porAtendimento) _porAtendimento[atendimento] = a;
        }

        // O app fica aberto por dias: solta o que não é relido há mais de HorasNaMemoria
        // (quem ainda estiver na lista é consultado de novo). Chamar com a trava.
        private static void Enxugar()
        {
            if (_porAtendimento.Count < 500) return;
            DateTime corte = DateTime.Now.AddHours(-HorasNaMemoria);
            var fora = new List<string>();
            foreach (var kv in _porAtendimento) if (kv.Value.LidoEm < corte) fora.Add(kv.Key);
            foreach (var k in fora) _porAtendimento.Remove(k);
        }

        // DSP_HR_ALTA = "27/11/2026 14:49:02"; DSP_DT_ALTA (quando só ela vem) = "27/11/2026 00:00:00".
        private static DateTime? SaidaNoMv(string alta, out bool semHora)
        {
            semHora = false;
            if (string.IsNullOrEmpty(alta)) return null;
            string t = alta.Trim();
            DateTime d;
            string[] comHora = new[] { "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm" };
            if (DateTime.TryParseExact(t, comHora, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            {
                semHora = d.TimeOfDay == TimeSpan.Zero;
                return d;
            }
            if (t.Length >= 10 && DateTime.TryParseExact(t.Substring(0, 10), "dd/MM/yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out d))
            {
                semHora = true;
                return d;
            }
            return null;
        }

        // Dia e hora do pedido (o turno, nos que vêm de prescrição).
        private static bool QuandoDoPedido(Solicitacao s, out DateTime quando)
        {
            quando = DateTime.MinValue;
            string data = s.Data ?? "";
            if (data.Length < 10) return false;
            DateTime dia;
            if (!DateTime.TryParseExact(data.Substring(0, 10), "dd/MM/yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dia)) return false;
            var m = Regex.Match(s.Hora ?? "", "(\\d{1,2}):(\\d{2})(?::\\d{2})?\\s*$");
            if (!m.Success) return false;
            int h = int.Parse(m.Groups[1].Value), min = int.Parse(m.Groups[2].Value);
            if (h > 23 || min > 59) return false;
            quando = dia.AddHours(h).AddMinutes(min);
            return true;
        }

        private static bool SoDigitos(string t)
        {
            for (int i = 0; i < t.Length; i++) if (t[i] < '0' || t[i] > '9') return false;
            return true;
        }
    }
}
