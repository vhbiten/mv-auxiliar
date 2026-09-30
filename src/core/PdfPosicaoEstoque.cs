using System;
using System.Collections.Generic;
using System.Globalization;

namespace SoulmvKit.Core
{
    // Saldo de um produto somando TODOS os estoques do hospital.
    public class SaldoProduto
    {
        public int Codigo;
        public string Nome;
        public string Unidade;
        public decimal Saldo;
    }

    public class PosicaoEstoqueResult
    {
        public string ReportUrl;
        public byte[] Pdf;
        public List<SaldoProduto> Produtos = new List<SaldoProduto>();
    }

    // Interpreta o PDF do R_POS_EST_S ("Posição de Estoque Simplificado").
    //
    // O gerador emite os textos de cada linha em ordem reversa de colunas. A ordem abaixo foi
    // MEDIDA pela coordenada x de cada token contra as faixas do cabeçalho, em 2015 linhas de
    // 5 estoques (antes estava escrita de memória, com "Quantidade" e "Qtd Atual" trocados):
    //   [Vl. Total] [Vl. Custo Médio] [Qtd Atual] [Unidade] [Produto] [Quantidade] [Qtd Doado]
    // e o "Produto" vem como "CODIGO - NOME", podendo QUEBRAR em mais de um token quando o
    // nome é longo ("9017228 - AMICACINA 250MG/ML SOL INJ" + "AMP C/ 2ML").
    //
    // Âncora do parser: o token no formato "<número> - <texto>". Dele saem o código e o
    // início do nome; o que vier depois e não for número é continuação do nome.
    //
    // O gerador quebra a célula do produto de DOIS jeitos, e os dois precisam de tratamento:
    //  - o nome continua no token seguinte ("9017228 - AMICACINA 250MG/ML SOL INJ" +
    //    "AMP C/ 2ML"), que é a continuação normal;
    //  - o nome não cabe nem começar ao lado do código, e sobra "14090 -" SOZINHO, com o
    //    nome inteiro no próximo token. Este segundo caso derrubava a linha toda: o produto
    //    sumia do relatório e a análise mostrava saldo 0 em todos os setores. Achado em
    //    02/09/2026 no colírio LAGRIBELL (código 14090), que tinha 8 na CAF e 16 na Central.
    //
    // Saldo = "Qtd Atual", que é o estoque próprio MAIS o doado — é o número que o cadastro
    // do MV mostra como "Estoque Atual". Ele NÃO é o primeiro número depois do nome: ali vem
    // "Quantidade", que exclui doação. A identidade Qtd Atual = Quantidade + Qtd Doado fecha
    // em 2015 de 2015 linhas medidas.
    //
    // Isto já custou caro: lendo "Quantidade", produto com estoque todo doado saía com saldo
    // ZERO num relatório cujo filtro é justamente "saldo diferente de zero" (insulina NPH e
    // regular na CAF, uma dúzia de oncológicos na Quimio), e a análise mandava comprar item
    // que estava na prateleira. A diferença antes anotada aqui como "corrida de tempo entre
    // a abertura do form e a emissão" (ceftriaxona 245 x 243, oxacilina 530 x 522) era, o
    // tempo todo, a coluna de doação.
    public static class PdfPosicaoEstoque
    {
        // O nome é opcional de propósito: "14090 -" é uma linha de produto legítima.
        private static readonly System.Text.RegularExpressions.Regex RxAncora =
            new System.Text.RegularExpressions.Regex(@"^(\d{3,9})\s*-\s*(\S.*)?$");

        // Dígito puro, sem vírgula: no relatório toda coluna numérica vem com casas decimais
        // ("1.500,0000", "0,0000", "14,5972"), então um token só de dígitos não é valor.
        private static readonly System.Text.RegularExpressions.Regex RxDigitos =
            new System.Text.RegularExpressions.Regex(@"^\d+$");

        public static List<SaldoProduto> Interpretar(byte[] pdf)
        {
            var lista = new List<SaldoProduto>();
            var toks = PdfKits.ExtrairTextos(pdf);
            if (toks == null) return lista;

            for (int i = 0; i < toks.Count; i++)
            {
                string t = (toks[i] ?? "").Trim();
                var m = RxAncora.Match(t);
                if (!m.Success) continue;

                int cod;
                if (!int.TryParse(m.Groups[1].Value, out cod) || cod <= 0) continue;

                string nome = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
                int j = i + 1;
                decimal lixo;

                // Código sem nome: só vale como produto se o próximo token for TEXTO, que é
                // o nome que desceu de linha. Com número na frente não é linha de produto
                // (máscara de negativo, rodapé) e seguir adiante inventaria um item.
                if (nome.Length == 0 && (j >= toks.Count || Numero(toks[j], out lixo))) continue;

                // Continuação do nome: tudo que não for número até o primeiro valor. Para
                // também em outra âncora: um código nunca é continuação de nome, e sem esse
                // freio uma linha truncada engoliria o produto seguinte.
                while (j < toks.Count)
                {
                    string c = (toks[j] ?? "").Trim();

                    // Terceira forma de quebra da célula: o nome que TERMINA em código de
                    // catálogo é cortado no traço interno e o código desce sozinho
                    // ("4979 - AGULHA DESCARTAVEL 20X5,5 -" + "70014248"). Como o código é um
                    // número, ele era lido como saldo: as agulhas apareciam com 70 milhões em
                    // estoque, cobertura infinita, situação OK e sugestão de compra zero — os
                    // itens de maior giro sumiam do radar. Dígito puro logo depois de um nome
                    // terminado em traço é nome, não valor.
                    bool codigoDoNome = RxDigitos.IsMatch(c) && nome.TrimEnd().EndsWith("-");

                    if (!codigoDoNome && Numero(c, out lixo)) break;              // começou a Quantidade
                    if (!codigoDoNome && RxAncora.IsMatch(c)) break;              // começou outro produto
                    if (c.Length > 0) nome += (nome.Length > 0 ? " " : "") + c;
                    j++;
                }

                // Linha de produto de verdade tem número depois do nome (a "Quantidade").
                decimal quantidade;
                if (j >= toks.Count || !Numero(toks[j], out quantidade)) continue;

                // Saldo = "Qtd Atual". Ela é a coluna imediatamente ANTES da unidade, e é de lá
                // que se lê direto quando o desenho da linha está completo (unidade em texto no
                // meio garante que toks[i-2] é mesmo a coluna certa, e não um valor em dinheiro).
                // Sem isso, soma-se "Quantidade" + "Qtd Doado", que dá o mesmo número.
                decimal saldo, doado;
                bool unidadeNoLugar = i >= 2 && !Numero(toks[i - 1], out lixo);
                if (!(unidadeNoLugar && Numero(toks[i - 2], out saldo)))
                {
                    saldo = quantidade;
                    if (j + 1 < toks.Count && Numero(toks[j + 1], out doado)) saldo += doado;
                }

                lista.Add(new SaldoProduto
                {
                    Codigo = cod,
                    Nome = System.Text.RegularExpressions.Regex.Replace(nome, @"\s+", " ").Trim(),
                    Unidade = i > 0 ? (toks[i - 1] ?? "").Trim() : "",
                    Saldo = saldo
                });
                i = j;
            }
            return lista;
        }

        internal static bool Numero(string s, out decimal v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim().Replace(".", "").Replace(",", ".");
            return decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out v);
        }
    }
}
