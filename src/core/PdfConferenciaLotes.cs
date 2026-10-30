using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Interpreta o PDF do R_CONF_LOTE ("Relatório de Conferência dos Lotes") em lotes por
    // produto. Lido POR POSIÇÃO: cada texto vem com a matriz "1 0 0 1 x y Tm" logo antes, e a
    // coluna sai do x (medido no relatório da solicitação 484416, 28/10/2026):
    //   Produto x≈18 | nome x=52 (quebra em várias linhas) | Unidade x=155 | Estoque Atual
    //   (à direita, até ~252) | Lote (à direita, até ~310; lote comprido QUEBRA e a sobra desce
    //   para a linha de baixo: "4526S06AAA" + "A") | Validade x≈312 | Est. x≈360 | Qt Kit x≈388 |
    //   Endereço | Quantidade (à direita, até ~506) | Qt. Dig. e Fabricante (linhas em branco).
    // Pela ordem do texto não dá: o nome do produto quebra em linhas que se intercalam com as
    // dos lotes, e a coluna de endereço aparece ou não.
    // Conferido: a soma das "Quantidade" dos lotes fecha com o "Estoque Atual" do produto
    // (gaze 5835: 1413 + 2 + 142 = 1557).
    public static class PdfConferenciaLotes
    {
        private static readonly Regex RxTexto = new Regex(@"1 0 0 1 ([\d.\-]+) ([\d.\-]+) Tm|\(((?:[^()\\]|\\.)*)\)\s*Tj");
        private static readonly Regex RxQtd = new Regex(@"^-?\d{1,3}(\.\d{3})*,\d+$");
        private static readonly Regex RxData = new Regex(@"^\d{2}/\d{2}/\d{4}$");
        private static readonly Regex RxCodigo = new Regex(@"^\d{1,9}$");

        // O cabeçalho (título, filtros, nomes das colunas) termina na linha dos títulos, que muda
        // de altura com o tamanho dos filtros: na CAF (485142, 30/10/2026) os títulos ficaram em
        // y=729 e o 1º produto em y=714 — o corte fixo em 705 descartava o DIALISADOR inteiro.
        // Vale a linha do título "Produto" de cada página; o fixo só enquanto ela não aparece.
        private const double YCabecalho = 705;
        private const double YRodape = 25;       // abaixo disso: "FUNDACAO..." e "MV | SoulMV"

        private class Linha
        {
            public int Pagina;
            public double Y;
            public string Lote = "";
            public DateTime? Validade;
            public decimal? Quantidade;
            public decimal EmKit;
        }

        public static Dictionary<int, LotesProduto> Interpretar(byte[] pdf)
        {
            var res = new Dictionary<int, LotesProduto>();
            if (pdf == null) return res;
            string conteudo = PdfKits.ConteudoInflado(pdf);

            LotesProduto atual = null;
            double yProduto = 0;
            var linhas = new Dictionary<string, Linha>();
            int pagina = 0;
            double x = 0, y = 0;
            double yTitulos = 0;   // 0 = a linha dos títulos desta página ainda não apareceu

            foreach (Match m in RxTexto.Matches(conteudo))
            {
                if (m.Groups[1].Success)
                {
                    x = Dbl(m.Groups[1].Value); y = Dbl(m.Groups[2].Value);
                    continue;
                }
                string t = PdfKits.DecodePdfString(m.Groups[3].Value).Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("Relat") && t.Contains("Confer")) { pagina++; yTitulos = 0; continue; }   // nova página
                if (x < 45 && t == "Produto" && y > YRodape) { yTitulos = y; continue; }
                if (y > (yTitulos > 0 ? yTitulos - 1 : YCabecalho) || y < YRodape) continue;

                if (x < 45)
                {
                    int cod;
                    if (!RxCodigo.IsMatch(t) || !int.TryParse(t, out cod)) continue;
                    Fechar(atual, linhas, res);
                    atual = new LotesProduto(); atual.Produto = cod; atual.Nome = "";
                    yProduto = y;
                    linhas = new Dictionary<string, Linha>();
                    continue;
                }
                if (atual == null) continue;

                if (x < 150) { atual.Nome = (atual.Nome + " " + t).Trim(); continue; }
                if (x < 200)
                {
                    // A unidade também quebra ("FR" + "C/100ML"): a sobra desce logo abaixo.
                    if (Math.Abs(y - yProduto) < 0.5) atual.Unidade = t;
                    else if (y < yProduto && y > yProduto - 30) atual.Unidade = ((atual.Unidade ?? "") + " " + t).Trim();
                    continue;
                }
                if (x < 311)
                {
                    decimal q;
                    if (RxQtd.IsMatch(t) && Math.Abs(y - yProduto) < 0.5 && Num(t, out q)) { atual.EstoqueAtual = q; continue; }
                    var l = LinhaDe(linhas, pagina, y);
                    l.Lote += t;
                    continue;
                }
                if (x < 350)
                {
                    DateTime d;
                    if (RxData.IsMatch(t) && DateTime.TryParseExact(t, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                        LinhaDe(linhas, pagina, y).Validade = d;
                    continue;
                }
                if (x < 375) continue;                                    // Est. (código do estoque)
                if (x < 410) { decimal k; if (Num(t, out k)) LinhaDe(linhas, pagina, y).EmKit = k; continue; }
                if (x < 440) continue;                                    // Endereço
                if (x < 508) { decimal q; if (Num(t, out q)) LinhaDe(linhas, pagina, y).Quantidade = q; continue; }
            }
            Fechar(atual, linhas, res);
            return res;
        }

        private static Linha LinhaDe(Dictionary<string, Linha> linhas, int pagina, double y)
        {
            string k = pagina + "|" + Math.Round(y, 1).ToString(CultureInfo.InvariantCulture);
            Linha l;
            if (!linhas.TryGetValue(k, out l)) { l = new Linha(); l.Pagina = pagina; l.Y = y; linhas[k] = l; }
            return l;
        }

        // Linha só com texto de lote (sem validade nem quantidade) é a sobra de um lote
        // comprido que quebrou: emenda no lote da linha de cima.
        private static void Fechar(LotesProduto p, Dictionary<string, Linha> linhas, Dictionary<int, LotesProduto> res)
        {
            if (p == null) return;
            var ordem = new List<Linha>(linhas.Values);
            ordem.Sort(delegate(Linha a, Linha b)
            {
                if (a.Pagina != b.Pagina) return a.Pagina.CompareTo(b.Pagina);
                return b.Y.CompareTo(a.Y);   // de cima para baixo
            });
            LoteDisponivel anterior = null;
            foreach (var l in ordem)
            {
                bool dado = l.Validade.HasValue || l.Quantidade.HasValue;
                if (!dado)
                {
                    if (anterior != null && l.Lote.Length > 0) anterior.Lote += l.Lote;
                    continue;
                }
                if (l.Lote.Length == 0) continue;
                var lote = new LoteDisponivel();
                lote.Lote = l.Lote; lote.Validade = l.Validade;
                lote.Quantidade = l.Quantidade.HasValue ? l.Quantidade.Value : 0;
                lote.EmKit = l.EmKit;
                p.Lotes.Add(lote);
                anterior = lote;
            }
            p.Lotes.Sort(PorValidade);
            p.Nome = Regex.Replace(p.Nome ?? "", @"\s+", " ").Trim();

            // Produto que atravessa a página volta com o MESMO código no topo da seguinte, com o
            // resto do nome e os lotes que sobraram (neomicina 9030088 na 484416: 10 + 5).
            LotesProduto ja;
            if (res.TryGetValue(p.Produto, out ja))
            {
                ja.Nome = (ja.Nome + " " + p.Nome).Trim();
                if (string.IsNullOrEmpty(ja.Unidade)) ja.Unidade = p.Unidade;
                if (p.EstoqueAtual > ja.EstoqueAtual) ja.EstoqueAtual = p.EstoqueAtual;
                ja.Lotes.AddRange(p.Lotes);
                ja.Lotes.Sort(PorValidade);
                return;
            }
            res[p.Produto] = p;
        }

        private static int PorValidade(LoteDisponivel a, LoteDisponivel b)
        {
            DateTime va = a.Validade.HasValue ? a.Validade.Value : DateTime.MaxValue;
            DateTime vb = b.Validade.HasValue ? b.Validade.Value : DateTime.MaxValue;
            return va.CompareTo(vb);
        }

        private static double Dbl(string s)
        {
            double v; double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v); return v;
        }

        private static bool Num(string s, out decimal v)
        {
            v = 0;
            string t = (s ?? "").Trim().Replace(".", "").Replace(",", ".");
            return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out v);
        }
    }
}
