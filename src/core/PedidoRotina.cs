using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // A ROTINA de pedidos da Farmácia Central à CAF: em que dias cada tipo é pedido e, daí,
    // quantos dias de saída o pré-pedido repõe. É o que tirou o seletor de PERÍODO da tela:
    // o usuário escolhe o tipo e clica em "Gerar pré-pedido".
    //
    // REGRA (definida pelo farmacêutico responsável em 26/11/2026): o pedido repõe o que
    // saiu nos últimos 2 DIAS; nos INTERVALOS MAIORES, 3 DIAS.
    //
    // "Intervalo maior" = o PRÓXIMO pedido do mesmo tipo está a mais de 2 dias. É o pedido
    // que tem que atravessar o fim de semana: materiais na quarta (o próximo é só na
    // segunda; a farmácia é pequena e não comporta 7 dias, mas tem que durar até domingo —
    // na segunda a CAF abre e o plantão consegue pedido emergencial entregue no dia) e
    // medicamentos na quinta (o próximo é no domingo).
    //
    //   medicamentos/soros (dom, ter, qui): dom 2 dias | ter 2 dias | qui 3 dias
    //   materiais (seg, qua):               seg 2 dias | qua 3 dias
    //
    // A média de 7/30 dias com dia-alvo, tentada na mesma data, foi descartada por ele
    // depois de testar ("não ficou legal se basear na média"): a base voltou a ser a
    // movimentação do período, que é o que a equipe confere de olho.
    //
    // DIAS DE PEDIDO: medidos no log de 01/10 a 25/11/2026 (38 pedidos) — materiais sempre
    // na segunda e na quarta, medicamentos no domingo, na terça e na quinta.
    //
    // Tudo pode ser ajustado em dados\pedido-rotina.txt sem nova versão do programa. O
    // arquivo nasce só com comentários: enquanto uma linha não for escrita, vale o padrão
    // do código (assim uma versão nova consegue corrigir o padrão em todos os PCs).
    public static class PedidoRotina
    {
        public const string Medicamentos = "medicamentos";
        public const string Soros = "soros";
        public const string Materiais = "materiais";
        public const string Hemodialise = "hemodialise";

        private const int PeriodoPadrao = 2;        // dias de saída que o pedido repõe
        private const int PeriodoMaiorPadrao = 3;   // ... quando o próximo pedido demora mais

        private static readonly object _lock = new object();
        private static int _periodo = PeriodoPadrao;
        private static int _periodoMaior = PeriodoMaiorPadrao;
        private static readonly Dictionary<string, bool[]> _dias = new Dictionary<string, bool[]>();

        static PedidoRotina() { DiasPadrao(); }

        private static void DiasPadrao()
        {
            _dias.Clear();
            _dias[Medicamentos] = Semana(DayOfWeek.Sunday, DayOfWeek.Tuesday, DayOfWeek.Thursday);
            _dias[Soros] = Semana(DayOfWeek.Sunday, DayOfWeek.Tuesday, DayOfWeek.Thursday);
            _dias[Materiais] = Semana(DayOfWeek.Monday, DayOfWeek.Wednesday);
            // Hemodiálise não tem dia fixo no log: fica sem rotina (sempre o período normal).
        }

        private static bool[] Semana(params DayOfWeek[] dias)
        {
            var r = new bool[7];
            foreach (var d in dias) r[(int)d] = true;
            return r;
        }

        public static void Init(string baseDir)
        {
            lock (_lock)
            {
                _periodo = PeriodoPadrao; _periodoMaior = PeriodoMaiorPadrao;
                DiasPadrao();
                try
                {
                    string dir = Path.Combine(baseDir, "dados");
                    Directory.CreateDirectory(dir);
                    string caminho = Path.Combine(dir, "pedido-rotina.txt");
                    if (!File.Exists(caminho)) { CriarModelo(caminho); return; }
                    foreach (var linha in File.ReadAllLines(caminho, Encoding.UTF8))
                    {
                        string s = (linha ?? "").Trim();
                        if (s.Length == 0 || s.StartsWith("#")) continue;
                        int cerq = s.IndexOf('#');
                        if (cerq >= 0) s = s.Substring(0, cerq).Trim();
                        int ig = s.IndexOf('=');
                        if (ig <= 0) continue;
                        Aplicar(s.Substring(0, ig).Trim().ToLowerInvariant(), s.Substring(ig + 1).Trim());
                    }
                    if (_periodoMaior < _periodo) _periodoMaior = _periodo;
                    Logger.Log("Pedido CAF: rotina — repõe " + _periodo + " dia(s) de saída; " + _periodoMaior +
                        " quando o próximo pedido está a mais de 2 dias.");
                }
                catch (Exception ex)
                {
                    Logger.Log("Pedido CAF: falha ao ler a rotina (" + ex.Message + ") — usando os padrões.");
                    _periodo = PeriodoPadrao; _periodoMaior = PeriodoMaiorPadrao;
                    DiasPadrao();
                }
            }
        }

        private static void Aplicar(string chave, string valor)
        {
            int n;
            if (chave == "periodo")
            {
                if (int.TryParse(valor, out n)) _periodo = Math.Max(1, Math.Min(7, n));
                return;
            }
            if (chave == "periodo_maior")
            {
                if (int.TryParse(valor, out n)) _periodoMaior = Math.Max(1, Math.Min(7, n));
                return;
            }
            if (chave == Medicamentos || chave == Soros || chave == Materiais || chave == Hemodialise)
            {
                var dias = new bool[7];
                bool algum = false;
                foreach (var parte in valor.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int d = DiaDaSemana(parte);
                    if (d >= 0) { dias[d] = true; algum = true; }
                }
                if (algum) _dias[chave] = dias; else _dias.Remove(chave);   // vazio = sem rotina
            }
            // chave desconhecida (inclusive as da regra de média, que existiu só em teste): ignora
        }

        // "dom", "seg", "ter", "qua", "qui", "sex", "sab" (aceita o nome inteiro e com acento)
        private static int DiaDaSemana(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Length < 3) return -1;
            string t = s.Substring(0, 3);
            if (t == "dom") return 0;
            if (t == "seg") return 1;
            if (t == "ter") return 2;
            if (t == "qua") return 3;
            if (t == "qui") return 4;
            if (t == "sex") return 5;
            if (t == "sab" || t == "sáb") return 6;
            return -1;
        }

        // Quantos dias faltam para o próximo pedido deste tipo (1 a 7). Tipo sem rotina: 2.
        public static int DiasAteProximoPedido(string tipo, DateTime hoje)
        {
            lock (_lock)
            {
                bool[] dias;
                if (tipo == null || !_dias.TryGetValue(tipo, out dias)) return 2;
                for (int i = 1; i <= 7; i++)
                    if (dias[(int)hoje.AddDays(i).DayOfWeek]) return i;
                return 2;
            }
        }

        // Quantos dias de SAÍDA o pré-pedido de hoje repõe: o período normal, ou o maior
        // quando o próximo pedido do tipo está a mais de 2 dias.
        public static int DiasDeConsumo(string tipo, DateTime hoje)
        {
            int n = DiasAteProximoPedido(tipo, hoje);
            lock (_lock) return n > 2 ? _periodoMaior : _periodo;
        }

        private static void CriarModelo(string caminho)
        {
            // Sem acentos, como os demais arquivos de dados do app. Tudo comentado: o que
            // vale e o padrao do programa ate alguem tirar o '#' de uma linha.
            var sb = new StringBuilder();
            sb.AppendLine("# Rotina do pedido da Farmacia Central a CAF (aba Previsao de Consumo).");
            sb.AppendLine("#");
            sb.AppendLine("# O pedido repoe o que saiu nos ultimos dias: o periodo normal, ou o periodo");
            sb.AppendLine("# maior quando o proximo pedido do mesmo tipo esta a mais de 2 dias (e o pedido");
            sb.AppendLine("# que atravessa o fim de semana). Para mudar um valor, tire o '#' da linha,");
            sb.AppendLine("# altere e abra o programa de novo. Linha com '#' = padrao do programa.");
            sb.AppendLine("#");
            sb.AppendLine("# periodo=" + PeriodoPadrao);
            sb.AppendLine("# periodo_maior=" + PeriodoMaiorPadrao);
            sb.AppendLine("#");
            sb.AppendLine("# Dias em que cada tipo e pedido (dom seg ter qua qui sex sab):");
            sb.AppendLine("# medicamentos=dom ter qui");
            sb.AppendLine("# soros=dom ter qui");
            sb.AppendLine("# materiais=seg qua");
            sb.AppendLine("# hemodialise=");
            File.WriteAllText(caminho, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
