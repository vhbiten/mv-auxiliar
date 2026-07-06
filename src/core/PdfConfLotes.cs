using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SoulmvKit.Core
{
    // Lê o PDF do "Relatório de Conferência dos Lotes" (R_CONF_LOTE) e extrai os
    // pares código -> nome do produto, usando a POSIÇÃO do texto na página:
    //   código na coluna x<40; linhas do nome na coluna 40<=x<150 (o nome quebra
    //   em várias linhas); as demais colunas (unidade, lote, qtd...) ficam à direita.
    // É a fonte do "aprendizado" de nomes do ProdutoNomes.
    public static class PdfConfLotes
    {
        private class TextoPos { public double X; public double Y; public string S; }

        public static Dictionary<int, string> ExtrairNomes(byte[] pdf)
        {
            var pares = new Dictionary<int, string>();
            List<TextoPos> itens;
            try { itens = ItensPosicionados(PdfKits.ConteudoInflado(pdf)); }
            catch (Exception ex) { Logger.Log("PdfConfLotes: " + ex.Message); return pares; }

            int atual = -1;
            var linhas = new List<string>();
            for (int i = 0; i <= itens.Count; i++)
            {
                TextoPos it = i < itens.Count ? itens[i] : null;
                bool fecha = it == null || it.X < 40;
                if (fecha)
                {
                    if (atual > 0 && linhas.Count > 0)
                    {
                        string nome = string.Join(" ", linhas.ToArray());
                        while (nome.IndexOf("  ") >= 0) nome = nome.Replace("  ", " ");
                        nome = nome.Trim();
                        if (nome.Length >= 3 && !pares.ContainsKey(atual)) pares[atual] = nome;
                    }
                    atual = -1; linhas.Clear();
                    if (it == null) break;
                    // novo código? (número de 3 a 8 dígitos na coluna esquerda)
                    string s = it.S.Trim();
                    int cd;
                    if (s.Length >= 3 && s.Length <= 8 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out cd))
                        atual = cd;
                    continue;
                }
                if (atual > 0 && it.X >= 40 && it.X < 150 && it.S.Trim().Length > 0)
                    linhas.Add(it.S.Trim());
            }
            return pares;
        }

        // ---- interpretador mínimo do content stream: rastreia Tm/Td/TD/TL/T* e
        // devolve cada string desenhada (Tj/'/TJ) com sua posição (x, y). ----
        private static List<TextoPos> ItensPosicionados(string content)
        {
            var itens = new List<TextoPos>();
            double x = 0, y = 0, lx = 0, ly = 0, leading = 0;
            var pend = new List<double>();
            var strs = new List<string>();
            var arr = new List<string>();
            bool inArr = false;

            int i = 0, n = content.Length;
            while (i < n)
            {
                char c = content[i];
                if (c == ' ' || c == '\r' || c == '\n' || c == '\t' || c == '\f') { i++; continue; }

                if (c == '(')
                {
                    int j = i + 1, depth = 1;
                    var sb = new StringBuilder();
                    while (j < n && depth > 0)
                    {
                        char ch = content[j];
                        if (ch == '\\' && j + 1 < n) { sb.Append(ch).Append(content[j + 1]); j += 2; continue; }
                        if (ch == '(') depth++;
                        else if (ch == ')') { depth--; if (depth == 0) break; }
                        sb.Append(ch); j++;
                    }
                    string dec = PdfKits.DecodePdfString(sb.ToString());
                    if (inArr) arr.Add(dec); else strs.Add(dec);
                    i = j + 1; continue;
                }
                if (c == '[') { inArr = true; arr.Clear(); pend.Clear(); i++; continue; }
                if (c == ']') { inArr = false; i++; continue; }
                if (c == '/')
                {
                    int j = i + 1;
                    while (j < n && !EhDelim(content[j])) j++;
                    i = j; continue;
                }
                if (c == '<')
                {
                    // <<dict>> ou <hex>: pula até o fechamento correspondente
                    if (i + 1 < n && content[i + 1] == '<') { i += 2; continue; }
                    int j = content.IndexOf('>', i + 1);
                    i = j < 0 ? n : j + 1; continue;
                }
                if (c == '>') { i++; continue; }

                // palavra: número ou operador
                int k = i;
                while (k < n && !EhDelim(content[k])) k++;
                if (k == i) { i++; continue; }   // delimitador solto (ex.: ')') — ignora
                string w = content.Substring(i, k - i);
                i = k;

                double num;
                if (double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out num) &&
                    (char.IsDigit(w[0]) || w[0] == '-' || w[0] == '+' || w[0] == '.'))
                {
                    pend.Add(num); continue;
                }

                switch (w)
                {
                    case "Tm": if (pend.Count >= 6) { x = pend[pend.Count - 2]; y = pend[pend.Count - 1]; lx = x; ly = y; } break;
                    case "Td": if (pend.Count >= 2) { lx += pend[pend.Count - 2]; ly += pend[pend.Count - 1]; x = lx; y = ly; } break;
                    case "TD": if (pend.Count >= 2) { leading = -pend[pend.Count - 1]; lx += pend[pend.Count - 2]; ly += pend[pend.Count - 1]; x = lx; y = ly; } break;
                    case "TL": if (pend.Count >= 1) leading = pend[pend.Count - 1]; break;
                    case "T*": ly -= leading; x = lx; y = ly; break;
                    case "Tj":
                    case "'":
                        if (strs.Count > 0)
                        {
                            string s = strs[strs.Count - 1]; strs.RemoveAt(strs.Count - 1);
                            if (s.Trim().Length > 0) itens.Add(new TextoPos { X = x, Y = y, S = s });
                        }
                        break;
                    case "TJ":
                        {
                            string s = string.Join("", arr.ToArray());
                            if (s.Trim().Length > 0) itens.Add(new TextoPos { X = x, Y = y, S = s });
                            arr.Clear();
                        }
                        break;
                }
                pend.Clear();
            }
            return itens;
        }

        private static bool EhDelim(char c)
        {
            return c == ' ' || c == '\r' || c == '\n' || c == '\t' || c == '\f' ||
                   c == '(' || c == ')' || c == '[' || c == ']' || c == '<' || c == '>' || c == '/';
        }
    }
}
