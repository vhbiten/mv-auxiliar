using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Um produto do "Relatório de Movimentações por Estoque" (R_MOVI_ESTOQ):
    // consumo no período + estoque atual — a matéria-prima do pré-pedido à CAF.
    public class MovProduto
    {
        public int Codigo;
        public string Nome;
        public string Unidade;
        public decimal Consumo;        // coluna "Qtd" (movimentado no período)
        public decimal EstoqueAtual;   // coluna "Qtd Atual"
        public decimal VlUnitario;     // coluna "Vl Unit" (custo unitário do MV)
        public decimal VlTotal;        // coluna "Vl Total" (custo do movimentado no período)
    }

    // Resultado da geração do relatório: a URL do PDF + os produtos interpretados.
    public class MovimentacaoResult
    {
        public string ReportUrl;
        public List<MovProduto> Produtos = new List<MovProduto>();
    }

    // Interpreta o PDF do R_MOVI_ESTOQ. O gerador (Oracle Reports) emite os textos de
    // cada linha em ordem REVERSA de colunas, um token por célula:
    //   [Qtd Atual] [Vl Total] [Vl Unit] [Qtd] [Unidade] [Nome] [Código]
    // (validado com o PDF real: "204,0000","177,29","2,7702","64,0000","UNIDADE",
    //  "ABOCATH 20G","13507"). Âncora do parser: 4 números decimais pt-BR seguidos
    // + 2 textos + 1 inteiro. Cabeçalhos, totais e rodapés não casam com o padrão.
    public static class PdfMovimentacao
    {
        public static List<MovProduto> Interpretar(byte[] pdf)
        {
            var toks = PdfKits.ExtrairTextos(pdf);
            var res = new List<MovProduto>();
            var vistos = new HashSet<int>();

            for (int i = 0; i + 6 < toks.Count; i++)
            {
                decimal qtdAtual, vlTotal, vlUnit, consumo, dummy;
                if (!Numero(toks[i], out qtdAtual)) continue;
                if (!Numero(toks[i + 1], out vlTotal)) continue;
                if (!Numero(toks[i + 2], out vlUnit)) continue;
                if (!Numero(toks[i + 3], out consumo)) continue;
                string unid = toks[i + 4], nome = toks[i + 5];
                if (Numero(unid, out dummy) || Numero(nome, out dummy)) continue;  // não é linha de produto
                int cod;
                if (!int.TryParse(toks[i + 6].Trim(), out cod) || cod <= 0) continue;

                var p = new MovProduto();
                p.Codigo = cod;
                p.Nome = nome.Trim();
                p.Unidade = unid.Trim();
                p.Consumo = consumo;
                p.EstoqueAtual = qtdAtual;
                p.VlUnitario = vlUnit;
                p.VlTotal = vlTotal;
                if (vistos.Add(cod)) res.Add(p);
                i += 6;   // pula o grupo consumido (o for ainda soma 1)
            }
            return res;
        }

        // Número pt-BR do relatório: "64,0000", "177,29", "1.131,0000". Também aceita
        // NEGATIVO ("-12,0000" ou máscara Oracle com sinal à direita "12,0000-") — estoque
        // negativo é estado real no MV e o produto NÃO pode sumir do pré-pedido — e valor
        // >= 1000 sem agrupamento ("1131,0000"), caso alguma máscara venha sem o ponto.
        private static bool Numero(string s, out decimal v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (!Regex.IsMatch(s, "^-?(\\d{1,3}(\\.\\d{3})+|\\d+),\\d{2,4}-?$")) return false;
            bool neg = s.StartsWith("-") || s.EndsWith("-");
            s = s.Trim('-');
            if (!decimal.TryParse(s.Replace(".", "").Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out v)) return false;
            if (neg) v = -v;
            return true;
        }
    }
}
