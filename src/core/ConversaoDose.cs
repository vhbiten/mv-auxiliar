using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Converte dose prescrita em MEDIDA (miligrama, grama, UI, mL) para EMBALAGENS
    // (frasco, ampola, bolsa...), que é a unidade em que o estoque conta.
    //
    // O MV não padroniza a unidade do item prescrito: a MESMA vancomicina aparece num
    // paciente como "2 FA C/500MG" e no outro como "250 MILIGRAMA". Somar os dois cru
    // dava 8.270 "unidades" contra um saldo de 240 frascos (caso real de 29/08/2026).
    //
    // A conversão só acontece quando dá para fazer com segurança:
    //   - a unidade prescrita é uma medida pura (MILIGRAMA, GRAMA, UI, ML...);
    //   - alguma apresentação declara o conteúdo por embalagem num token "C/500MG",
    //     "C/ 4,5G", "C/100ML" ou "C/500UI/MIL" — no nome do produto de estoque ligado
    //     ou, sem ele, no nome do item prescrito.
    // Sem as duas coisas o item fica FORA da soma, com o motivo na tela — melhor um
    // "não sei" visível do que 3.000 miligramas somados como 3.000 frascos.
    public static class ConversaoDose
    {
        // Quantas EMBALAGENS o item consome por horário. Devolve:
        //   a própria quantidade   quando a unidade já é embalagem (FA, COMP, BOLS...);
        //   a quantidade convertida quando a dose é medida e o conteúdo é conhecido;
        //   -1                     quando é medida mas NÃO dá para converter.
        public static decimal PorAplicacao(decimal quantidade, string unidade, string via,
            string nomePrescricao, string nomeEstoque)
        {
            decimal fator; char baseUn;
            if (quantidade <= 0 || !MedidaPura(unidade, out fator, out baseUn)) return quantidade;

            // O ESTOQUE primeiro: a embalagem que se dispensa (e que o saldo conta) é a
            // do produto ligado. O nome da prescrição é o plano B, para item sem ligação
            // — quando a ligação é coerente os dois declaram o mesmo conteúdo.
            decimal conteudo;
            if (!Conteudo(nomeEstoque, baseUn, out conteudo)
                && !Conteudo(nomePrescricao, baseUn, out conteudo)) return -1;

            decimal emb = quantidade * fator / conteudo;
            // Injetável é frasco aberto por horário: 250mg do frasco de 500mg gasta o
            // frasco inteiro (o reconstituído não guarda para o próximo horário). Via
            // oral/tópica raciona a mesma embalagem entre os horários, então a fração
            // fica — o total do tratamento é que interessa.
            return Parenteral(via) ? Math.Ceiling(emb) : emb;
        }

        // Unidade de MEDIDA pura -> fator para a base (M = mg, L = mL, U = UI).
        // false = é embalagem (ou não deu para reconhecer), e a quantidade vale como está.
        // "UNIDADE" sozinha é embalagem (comprimido avulso do metronidazol vem assim);
        // só vale como UI acompanhada de "INTERNACIONAL/INTERNACIONAIS".
        public static bool MedidaPura(string unidade, out decimal fator, out char baseUn)
        {
            fator = 0; baseUn = ' ';
            string u = Regex.Replace((unidade ?? "").Trim().ToUpperInvariant(), @"\s+", " ");
            if (u.Length == 0) return false;
            switch (u)
            {
                case "MG": case "MILIGRAMA": case "MILIGRAMAS":
                    fator = 1m; baseUn = 'M'; return true;
                case "G": case "GR": case "GRAMA": case "GRAMAS":
                    fator = 1000m; baseUn = 'M'; return true;
                case "MCG": case "UG": case "MICROGRAMA": case "MICROGRAMAS":
                    fator = 0.001m; baseUn = 'M'; return true;
                case "ML": case "MILILITRO": case "MILILITROS":
                    fator = 1m; baseUn = 'L'; return true;
                case "UI": case "U.I.": case "U.I":
                    fator = 1m; baseUn = 'U'; return true;
            }
            if (Regex.IsMatch(u, @"^UNIDADES? INTERNACIONA")) { fator = 1m; baseUn = 'U'; return true; }
            return false;
        }

        // Conteúdo por embalagem na base pedida: "FA C/500MG" -> 500 mg; "FRASCO C/ 4,5G"
        // -> 4.500 mg; "FA C/500UI/MIL" -> 500.000 UI; "BOLS C/100ML" -> 100 mL.
        public static bool Conteudo(string texto, char baseUn, out decimal valor)
        {
            valor = 0;
            foreach (var t in Tokens(texto))
                if (t.Key == baseUn) { valor = t.Value; return true; }
            return false;
        }

        // Token de DOSAGEM canônico para casar prescrição x catálogo na PonteEstoque:
        // "C/500MG" -> "500MG"; "C/ 4,5G" -> "4500MG"; "C/500UI/MIL" -> "500000UI".
        // Volume fica DE FORA de propósito: "C/2ML" não distingue a apresentação
        // (amicacina 100MG/2ML e 500MG/2ML são ambas "AMP C/2ML") — ligar por ele
        // mostraria o saldo da dosagem errada. null = não declara dosagem.
        public static string TokenDosagem(string texto)
        {
            foreach (var t in Tokens(texto))
                if (t.Key != 'L')
                    return t.Value.ToString("0.####", CultureInfo.InvariantCulture)
                         + (t.Key == 'U' ? "UI" : "MG");
            return null;
        }

        // Todos os tokens "C/..." válidos do texto, já na base (M = mg, L = mL, U = UI).
        //
        // O qualificador após a unidade é capturado INTEIRO ("(/[A-Z]+)?") e decidido no
        // código: "/MIL" multiplica por mil; qualquer outro ("/ML", "/MILHAO",
        // "/MILILITRO") invalida o token — é concentração ou grandeza que não conhecemos.
        // Deixar o regex decidir com grupos opcionais permitia backtracking aceitar
        // "C/500UI/MILHAO" como 500 UI (erro de um milhão de vezes, silencioso).
        private static List<KeyValuePair<char, decimal>> Tokens(string texto)
        {
            var lista = new List<KeyValuePair<char, decimal>>();
            if (string.IsNullOrEmpty(texto)) return lista;
            foreach (Match m in Regex.Matches(texto.ToUpperInvariant(),
                @"C/\s*(\d+(?:[.,]\d+)?)\s*(MG|MCG|UG|GR|G|ML|UI)(/[A-Z]+)?(?![A-Z])"))
            {
                string qual = m.Groups[3].Success ? m.Groups[3].Value : "";
                if (qual.Length > 0 && qual != "/MIL") continue;
                decimal v = LerNumero(m.Groups[1].Value);
                if (v <= 0) continue;
                string un = m.Groups[2].Value;
                char b = un == "ML" ? 'L' : un == "UI" ? 'U' : 'M';
                if (un == "G" || un == "GR") v *= 1000m;
                else if (un == "MCG" || un == "UG") v /= 1000m;
                if (qual == "/MIL") v *= 1000m;              // "500UI/MIL" = 500 mil UI
                lista.Add(new KeyValuePair<char, decimal>(b, v));
            }
            return lista;
        }

        // "4,5" -> 4.5; "1.000" -> 1000 (separador de milhar: vírgula/ponto seguido de
        // exatamente 3 dígitos com parte inteira não nula); "0,375" -> 0.375.
        private static decimal LerNumero(string s)
        {
            string t = s.Trim();
            var mil = Regex.Match(t, @"^([1-9]\d*)[.,](\d{3})$");
            if (mil.Success) t = mil.Groups[1].Value + mil.Groups[2].Value;
            else t = t.Replace(",", ".");
            decimal v;
            return decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0m;
        }

        private static bool Parenteral(string via)
        {
            string v = (via ?? "").Trim().ToUpperInvariant();
            if (v == "EV" || v == "IV" || v == "IM" || v == "SC") return true;
            return v.Contains("ENDOVEN") || v.Contains("INTRAVEN") || v.Contains("INTRAMUSC")
                || v.Contains("SUBCUT") || v.Contains("INTRATEC") || v.Contains("PERIDURAL")
                || v.Contains("EPIDURAL");
        }
    }
}
